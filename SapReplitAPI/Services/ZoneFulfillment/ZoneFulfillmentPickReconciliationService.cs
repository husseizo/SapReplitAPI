using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Detects SAP-native pick confirmations that bypassed the ZF API and reconciles
/// MolasIntegration PLR state with SAP truth, then delegates to EvaluateAndTriggerDeliveryAsync.
///
/// Invariants:
///   - Only processes Accepted orchestrations with non-Picked PLRs older than EligibilityThreshold.
///   - FAIL CLOSED on any SAP validation mismatch — no mutations, emits ZF_PICK_RECONCILIATION_BLOCKED.
///   - All PLR validations for one orchestration must pass before ANY PLR is mutated.
///   - Concurrency: acquires per-RequestId delivery lock before re-reading state.
///   - Never calls OfflineFulfillmentRecoveryService — Online ZF only.
/// </summary>
public sealed class ZoneFulfillmentPickReconciliationService
{
    private const int EligibilityThresholdSeconds = 90;

    private readonly ZoneFulfillmentRepository                         _repo;
    private readonly SapService                                        _sap;
    private readonly ZoneFulfillmentDeliveryCoordinator                _coordinator;
    private readonly ZoneFulfillmentAutomationService                  _automation;
    private readonly ILogger<ZoneFulfillmentPickReconciliationService> _log;

    public ZoneFulfillmentPickReconciliationService(
        ZoneFulfillmentRepository                         repo,
        SapService                                        sap,
        ZoneFulfillmentDeliveryCoordinator                coordinator,
        ZoneFulfillmentAutomationService                  automation,
        ILogger<ZoneFulfillmentPickReconciliationService> log)
    {
        _repo        = repo;
        _sap         = sap;
        _coordinator = coordinator;
        _automation  = automation;
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
                _log.LogError(ex,
                    "[ZF-PICK-RECONCILE] Unhandled error for RequestId={Rid} — skipping to next.",
                    orch.RequestId);
            }
        }
    }

    private async Task ReconcileOneAsync(FulfillmentOrchestrationRecord orch, CancellationToken ct)
    {
        // Acquire per-RequestId delivery lock — same lock used by delivery service
        using var lk = await _coordinator.AcquireAsync(orch.RequestId, ct);

        // Re-read under lock — state may have changed since the query
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
            // Race: all PLRs became Picked between query and lock acquisition
            _log.LogInformation(
                "[ZF-PICK-RECONCILE] RequestId={Rid} — all PLRs now Picked; triggering automation",
                orch.RequestId);
            await _automation.EvaluateAndTriggerDeliveryAsync(orch.RequestId, ct);
            return;
        }

        // ── SAP validation pass — ALL must pass before ANY PLR is mutated ──────
        var approved = new List<(PickListRecordModel Plr, SapService.Pkl1Row SapLine)>();

        foreach (var plr in pending)
        {
            var (header, lines, _) = _sap.GetPickListFullState(plr.PickListAbsEntry);

            if (header is null)
            {
                _log.LogWarning(
                    "[ZF-PICK-RECONCILE] ZF_PICK_RECONCILIATION_BLOCKED RequestId={Rid} " +
                    "AbsEntry={Abs} — OPKL not found in SAP",
                    orch.RequestId, plr.PickListAbsEntry);
                return;
            }

            if (header.Canceled == "Y")
            {
                _log.LogWarning(
                    "[ZF-PICK-RECONCILE] ZF_PICK_RECONCILIATION_BLOCKED RequestId={Rid} " +
                    "AbsEntry={Abs} — OPKL.Canceled=Y",
                    orch.RequestId, plr.PickListAbsEntry);
                return;
            }

            if (header.Status != "Y")
            {
                // Not yet fully confirmed — defer; normal case while picker is still working
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] RequestId={Rid} AbsEntry={Abs} OPKL.Status={St} — " +
                    "not yet fully confirmed, deferring",
                    orch.RequestId, plr.PickListAbsEntry, header.Status);
                return;
            }

            var sapLine = lines.FirstOrDefault(
                l => l.OrderEntry == plr.SoDocEntry && l.OrderLine == plr.SoLineNum);

            if (sapLine is null)
            {
                _log.LogWarning(
                    "[ZF-PICK-RECONCILE] ZF_PICK_RECONCILIATION_BLOCKED RequestId={Rid} " +
                    "AbsEntry={Abs} SO={So} Line={Ln} — PKL1 line not found in SAP",
                    orch.RequestId, plr.PickListAbsEntry, plr.SoDocEntry, plr.SoLineNum);
                return;
            }

            if (sapLine.PickStatus != "Y")
            {
                _log.LogInformation(
                    "[ZF-PICK-RECONCILE] RequestId={Rid} AbsEntry={Abs} PKL1.PickStatus={St} — " +
                    "line not yet confirmed, deferring",
                    orch.RequestId, plr.PickListAbsEntry, sapLine.PickStatus);
                return;
            }

            if (sapLine.PickQtty < plr.ReleasedQty)
            {
                _log.LogWarning(
                    "[ZF-PICK-RECONCILE] ZF_PICK_RECONCILIATION_BLOCKED RequestId={Rid} " +
                    "AbsEntry={Abs} SAP.PickQtty={Sq} < PLR.ReleasedQty={Rq} — partial pick, blocking",
                    orch.RequestId, plr.PickListAbsEntry, sapLine.PickQtty, plr.ReleasedQty);
                return;
            }

            approved.Add((plr, sapLine));
        }

        // ── All validations passed — persist reconciliation ───────────────────
        foreach (var (plr, sapLine) in approved)
        {
            _log.LogInformation(
                "[ZF-PICK-RECONCILE] Reconciling PLR.Id={Id} AbsEntry={Abs} " +
                "SAP.PickQtty={Qty} → MolasStatus=Picked",
                plr.Id, plr.PickListAbsEntry, sapLine.PickQtty);

            await _repo.UpdatePickListPickedQtyAsync(plr.Id, sapLine.PickQtty, PickListStatus.Picked, ct);

            int plfrRows = await _repo.UpdatePickListFragmentPickedQtyAsync(
                plr.Id, sapLine.PickQtty, PickListStatus.Picked, ct);
            if (plfrRows > 0)
                _log.LogInformation("[ZF-PICK-RECONCILE] PLFR updated: PLR.Id={Id}", plr.Id);
        }

        _log.LogInformation(
            "[ZF-PICK-RECONCILE] SAP-reconciled {N} PLR(s) for RequestId={Rid} — " +
            "delegating to EvaluateAndTriggerDeliveryAsync",
            approved.Count, orch.RequestId);

        var result = await _automation.EvaluateAndTriggerDeliveryAsync(orch.RequestId, ct);

        _log.LogInformation(
            "[ZF-PICK-RECONCILE] AutomationResult RequestId={Rid} Status={St} " +
            "DeliveryEntry={De} InvoiceEntry={Ie}",
            orch.RequestId,
            result.AutomationStatus,
            result.DeliveryDocEntry,
            result.InvoiceDocEntry);
    }
}
