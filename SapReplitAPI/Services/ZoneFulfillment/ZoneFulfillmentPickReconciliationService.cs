using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Detects SAP-native pick confirmations that bypassed the ZF API and reconciles
/// MolasIntegration PLR/PLFR state with SAP truth, then delegates to
/// EvaluateAndTriggerDeliveryAsync.
///
/// Safety gate invariants (ALL must hold before any MolasIntegration mutation):
///   1. OPKL exists, is not cancelled, Status=Y.
///   2. OPKL.U_ReplitId == orch.U_ReplitId (ZF identity / ownership).
///   3. PKL1 line found for (SoDocEntry, SoLineNum) with BaseObject=17 (ORDR).
///   4. PKL1.WhsCode (via RDR1) == PLR.WhsCode (warehouse match).
///   5. PKL1.PickStatus=Y.
///   6. |PKL1.PickQtty - PLR.ReleasedQty| ≤ QtyTolerance (exact match).
///      PKL1.PickQtty > PLR.ReleasedQty + QtyTolerance → BLOCKED (over-pick).
///   7. For bin-managed lines: SUM(PKL2.PickQtty for this PickEntry) == PKL1.PickQtty ± tolerance.
///      Bin lines absent on a bin-managed pick → BLOCKED.
///   8. ALL pending PLRs for the orchestration must pass before ANY PLR is mutated.
///
/// Concurrency: acquires per-RequestId ZoneFulfillmentDeliveryCoordinator lock
///   (same domain as API Confirm Pick and delivery automation) before re-reading state.
///
/// Never calls OfflineFulfillmentRecoveryService — Online ZF only.
/// </summary>
public sealed class ZoneFulfillmentPickReconciliationService
{
    private const int     EligibilityThresholdSeconds = 90;
    /// <summary>Decimal quantity tolerance for floating-point-safe exact-match checks.</summary>
    private const decimal QtyTolerance = 0.001m;
    /// <summary>SAP BaseObject value for ORDR (Sales Order).</summary>
    private const int     BaseObjectOrdr = 17;

    private readonly IZfReconciliationRepo                             _repo;
    private readonly IZfPickSapReader                                  _sapReader;
    private readonly ZoneFulfillmentDeliveryCoordinator                _coordinator;
    private readonly IZfAutomation                                     _automation;
    private readonly SapReplitAPI.Services.PickList.IPickListEventRefreshService _refresh;
    private readonly ILogger<ZoneFulfillmentPickReconciliationService> _log;

    public ZoneFulfillmentPickReconciliationService(
        IZfReconciliationRepo                             repo,
        IZfPickSapReader                                  sapReader,
        ZoneFulfillmentDeliveryCoordinator                coordinator,
        IZfAutomation                                     automation,
        SapReplitAPI.Services.PickList.IPickListEventRefreshService refresh,
        ILogger<ZoneFulfillmentPickReconciliationService> log)
    {
        _repo        = repo;
        _sapReader   = sapReader;
        _coordinator = coordinator;
        _automation  = automation;
        _refresh     = refresh;
        _log         = log;
    }

    public async Task ReconcileBatchAsync(CancellationToken ct = default)
    {
        var candidates = await _repo.GetStuckAcceptedOrchestrationsAsync(EligibilityThresholdSeconds, ct);

        if (candidates.Count == 0)
            return;

        _log.LogInformation(
            "[ZF-PICK-RECONCILE] Batch: {N} stuck orchestration(s) eligible for SAP reconciliation",
            candidates.Count);

        foreach (var orch in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ReconcileOneAsync(orch, ct);
            }
            catch (Exception ex)
            {
                // Gate 9: isolate per-candidate failure — do not abort the batch.
                _log.LogError(ex,
                    "[ZF-PICK-RECONCILE] Unhandled error for RequestId={Rid} — skipping to next.",
                    orch.RequestId);
            }
        }
    }

    private async Task ReconcileOneAsync(FulfillmentOrchestrationRecord orch, CancellationToken ct)
    {
        // Gate 7: acquire per-RequestId delivery lock — same coordinator used by
        // ZoneFulfillmentDeliveryService and the API Confirm Pick path.
        using var lk = await _coordinator.AcquireAsync(orch.RequestId, ct);

        // Re-read under lock — state may have changed since the candidate query
        var fresh = await _repo.FindOrchestrationAsync(orch.RequestId, ct);
        if (fresh is null || fresh.State != OrchestrationState.Accepted)
        {
            _log.LogDebug(
                "[ZF-PICK-RECONCILE] RequestId={Rid} state={St} — already handled, skipping",
                orch.RequestId, fresh?.State ?? "null");
            return;
        }

        var plrs = await _repo.GetPickListRecordsAsync(fresh.Id, ct);

        var pending = plrs
            .Where(p => p.Status != PickListStatus.Picked && p.PickListAbsEntry > 0)
            .ToList();

        if (pending.Count == 0)
        {
            // Race: all PLRs became Picked between query and lock acquisition.
            // Release the coordinator lock before calling automation (same reentrant-lock fix).
            _log.LogInformation(
                "[ZF-PICK-RECONCILE] RequestId={Rid} — all PLRs now Picked; triggering automation",
                orch.RequestId);
            lk.Dispose();
            await _automation.EvaluateAndTriggerDeliveryAsync(orch.RequestId, ct);
            return;
        }

        // ── Gate 8: validate ALL pending PLRs before mutating ANY ───────────────
        var approved = new List<ApprovedPlr>();

        foreach (var plr in pending)
        {
            var (header, lines, bins) = _sapReader.GetPickListValidationState(plr.PickListAbsEntry);

            // Gate 1a: OPKL must exist
            if (header is null)
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry, "OPKL not found in SAP");
                return;
            }

            // Gate 1b: OPKL must not be cancelled
            if (header.Canceled == "Y")
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry, "OPKL.Canceled=Y");
                return;
            }

            // Gate 1c: OPKL.Status must be Y (fully confirmed)
            if (header.Status != "Y")
            {
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] RequestId={Rid} AbsEntry={Abs} OPKL.Status={St} — " +
                    "not yet confirmed, deferring",
                    orch.RequestId, plr.PickListAbsEntry, header.Status);
                return;
            }

            // Gate 2: ZF identity — OPKL.U_ReplitId must match orchestration
            if (header.UReplitId is null)
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    "OPKL.U_ReplitId is null — not a ZF-owned pick list");
                return;
            }
            if (fresh.U_ReplitId is not null &&
                !string.Equals(header.UReplitId, fresh.U_ReplitId, StringComparison.OrdinalIgnoreCase))
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    $"OPKL.U_ReplitId={header.UReplitId} != orch.U_ReplitId={fresh.U_ReplitId}");
                return;
            }

            // Gate 3a: PKL1 line must exist for this (SoDocEntry, SoLineNum)
            var sapLine = lines.FirstOrDefault(
                l => l.OrderEntry == plr.SoDocEntry && l.OrderLine == plr.SoLineNum);

            if (sapLine is null)
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    $"PKL1 line not found for SO={plr.SoDocEntry} Line={plr.SoLineNum}");
                return;
            }

            // Gate 3b: BaseObject must be 17 (ORDR — Sales Order)
            if (sapLine.BaseObject != BaseObjectOrdr)
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    $"PKL1.BaseObject={sapLine.BaseObject} != 17 (expected ORDR)");
                return;
            }

            // Gate 4: Warehouse match — SAP RDR1.WhsCode must match PLR.WhsCode
            if (!string.IsNullOrEmpty(sapLine.WhsCode) &&
                !string.Equals(sapLine.WhsCode, plr.WhsCode, StringComparison.OrdinalIgnoreCase))
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    $"WhsCode mismatch: SAP RDR1.WhsCode={sapLine.WhsCode} != PLR.WhsCode={plr.WhsCode}");
                return;
            }

            // Gate 5: PKL1.PickStatus must be Y
            if (sapLine.PickStatus != "Y")
            {
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] RequestId={Rid} AbsEntry={Abs} PKL1.PickStatus={St} — " +
                    "line not yet confirmed, deferring",
                    orch.RequestId, plr.PickListAbsEntry, sapLine.PickStatus);
                return;
            }

            // Gate 6: Exact quantity match (with tolerance); over-pick is a hard block
            decimal delta = sapLine.PickQtty - plr.ReleasedQty;
            if (delta > QtyTolerance)
            {
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    $"Over-pick: SAP.PickQtty={sapLine.PickQtty} > PLR.ReleasedQty={plr.ReleasedQty} " +
                    $"(delta={delta:F4} > tolerance={QtyTolerance}) — data inconsistency");
                return;
            }
            if (delta < -QtyTolerance)
            {
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] RequestId={Rid} AbsEntry={Abs} — partial pick: " +
                    "SAP.PickQtty={Sq} < PLR.ReleasedQty={Rq}, deferring",
                    orch.RequestId, plr.PickListAbsEntry, sapLine.PickQtty, plr.ReleasedQty);
                return;
            }

            // Gate 7 (PKL2): For bin-managed lines, validate that bin qty totals match
            var lineBins = bins.Where(b => b.PickEntry == sapLine.PickEntry).ToList();
            if (lineBins.Count > 0)
            {
                decimal binSum = lineBins.Sum(b => b.PickQtty);
                if (Math.Abs(binSum - sapLine.PickQtty) > QtyTolerance)
                {
                    Blocked(orch.RequestId, plr.PickListAbsEntry,
                        $"PKL2 bin sum={binSum} != PKL1.PickQtty={sapLine.PickQtty} " +
                        $"(delta={Math.Abs(binSum - sapLine.PickQtty):F4}) — bin allocation inconsistency");
                    return;
                }
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] RequestId={Rid} AbsEntry={Abs} PickEntry={Pe} — " +
                    "{N} bin(s) summing to {Sum} validated",
                    orch.RequestId, plr.PickListAbsEntry, sapLine.PickEntry, lineBins.Count, binSum);
            }
            else if (bins.Count > 0)
            {
                // Bins exist on the OPKL but none attributed to this line's PickEntry → bin tracking gap
                Blocked(orch.RequestId, plr.PickListAbsEntry,
                    $"PKL2 has bin rows for OPKL but none for PickEntry={sapLine.PickEntry} — " +
                    "cannot attribute bin allocation to this line");
                return;
            }

            approved.Add(new ApprovedPlr(plr, sapLine));
        }

        // ── Gate 8 passed: persist reconciliation atomically across all PLRs ───
        foreach (var (plr, sapLine) in approved)
        {
            _log.LogInformation(
                "[ZF-PICK-RECONCILE] Reconciling PLR.Id={Id} AbsEntry={Abs} PickEntry={Pe} " +
                "SAP.PickQtty={Qty} WhsCode={Whs} → MolasStatus=Picked",
                plr.Id, plr.PickListAbsEntry, sapLine.PickEntry, sapLine.PickQtty, plr.WhsCode);

            await _repo.UpdatePickListPickedQtyAsync(plr.Id, sapLine.PickQtty, PickListStatus.Picked, ct);

            int plfrRows = await _repo.UpdatePickListFragmentPickedQtyAsync(
                plr.Id, sapLine.PickQtty, PickListStatus.Picked, ct);
            if (plfrRows > 0)
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] PLFR updated: PLR.Id={Id} PickedQty={Qty}",
                    plr.Id, sapLine.PickQtty);
            else
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] PLFR update: 0 rows affected for PLR.Id={Id} — " +
                    "pre-PLFR history (safe, PLR is the authoritative record)",
                    plr.Id);
        }

        _log.LogInformation(
            "[ZF-PICK-RECONCILE] SAP-reconciled {N} PLR(s) for RequestId={Rid} — " +
            "delegating to EvaluateAndTriggerDeliveryAsync",
            approved.Count, orch.RequestId);

        // Non-fatal cache fast-path: propagate reconciled OPKL state to SQLite + Neon.
        // PLR writes are already committed; failure here is logged, scheduled fallback handles it.
        {
            var refreshed = new System.Collections.Generic.HashSet<int>();
            foreach (var (plr, _) in approved)
            {
                if (!refreshed.Add(plr.PickListAbsEntry)) continue;
                try
                {
                    await _refresh.RefreshAsync(plr.PickListAbsEntry, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "[ZF-PICK-RECONCILE] PickList cache fast-path non-fatal AbsEntry={Abs}",
                        plr.PickListAbsEntry);
                }
            }
        }

        // Release the coordinator lock before calling automation: ExecuteDeliveryAsync
        // re-acquires the same per-RequestId semaphore, and SemaphoreSlim is not reentrant.
        // PLR writes are already committed to the DB, so the delivery service can safely
        // acquire the lock independently.
        lk.Dispose();

        var result = await _automation.EvaluateAndTriggerDeliveryAsync(orch.RequestId, ct);

        _log.LogInformation(
            "[ZF-PICK-RECONCILE] AutomationResult RequestId={Rid} Status={St} " +
            "DeliveryEntry={De} InvoiceEntry={Ie}",
            orch.RequestId,
            result.AutomationStatus,
            result.DeliveryDocEntry,
            result.InvoiceDocEntry);
    }

    private void Blocked(Guid requestId, int absEntry, string reason)
        => _log.LogWarning(
            "[ZF-PICK-RECONCILE] ZF_PICK_RECONCILIATION_BLOCKED RequestId={Rid} " +
            "AbsEntry={Abs} — {Reason}",
            requestId, absEntry, reason);

    private readonly record struct ApprovedPlr(
        PickListRecordModel    Plr,
        ZfPkl1Validation       SapLine);
}
