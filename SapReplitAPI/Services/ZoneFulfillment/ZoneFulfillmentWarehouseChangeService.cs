using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Owns the full lifecycle of ZF post-allocation warehouse reassignment.
///
/// Design invariants:
///   • Validation gates run BEFORE SapService.UpdateOrder() — SAP mutations = 0 on any block.
///   • SAP is the primary store; SoLineFragment mirrors SAP RDR1 after every change.
///   • SoLineFragment.OriginalWhsCode is write-once (COALESCE in SQL).
///   • AllocationFragment.WhsCode is never modified (Tiered allocation history).
///   • Non-fatal contract: if SQL sync fails after SAP success, the divergence is logged
///     with WAREHOUSE_REASSIGNMENT_RECONCILIATION_REQUIRED for durable recovery.
/// </summary>
public sealed class ZoneFulfillmentWarehouseChangeService : IZoneFulfillmentWarehouseChangeService
{
    private readonly ZoneFulfillmentRepository    _repo;
    private readonly IZfWhsChangeSapReader        _sapReader;
    private readonly ILogger<ZoneFulfillmentWarehouseChangeService> _log;

    public ZoneFulfillmentWarehouseChangeService(
        ZoneFulfillmentRepository                      repo,
        IZfWhsChangeSapReader                          sapReader,
        ILogger<ZoneFulfillmentWarehouseChangeService> log)
    {
        _repo      = repo;
        _sapReader = sapReader;
        _log       = log;
    }

    // ── PreflightAsync ─────────────────────────────────────────────────────────

    public async Task<WfPreflightResult> PreflightAsync(
        int            soDocEntry,
        UpdateOrderDto dto,
        CancellationToken ct)
    {
        // G1: ZF enrolled?
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null)
            return WfPreflightResult.NotZf();

        // G2: Orchestration in Accepted state?
        if (orch.State != OrchestrationState.Accepted)
            return WfPreflightResult.Blocked(
                "ZF_INVALID_STATE",
                $"Warehouse change requires orchestration State=Accepted. Current: {orch.State}");

        // G3: Load fragments
        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0)
            return WfPreflightResult.Blocked("ZF_NO_FRAGMENTS", "No SoLineFragment rows found for this order.");

        // Build per-line warehouse change candidates
        var changes = new List<WarehouseLineChange>();
        foreach (var line in dto.UpdatedLines)
        {
            if (string.IsNullOrWhiteSpace(line.WhsCode)) continue;
            var frag = fragments.FirstOrDefault(f => f.SoLineNum == line.LineNum);
            if (frag == null) continue;
            if (!string.Equals(frag.WhsCode, line.WhsCode, StringComparison.OrdinalIgnoreCase))
                changes.Add(new WarehouseLineChange(line.LineNum, frag.Id, frag.ItemCode, frag.WhsCode, line.WhsCode));
        }

        if (changes.Count == 0)
            return WfPreflightResult.NoOp();

        // G4: No active delivery
        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
            return WfPreflightResult.Blocked(
                "ZF_ACTIVE_DELIVERY",
                "An active Delivery (Status=Created) exists. Warehouse change is blocked.");

        // Per-line checks: G5 (PLR PickedQty), G6/G7 (SAP PKL1), G8 (SAP PKL2)
        foreach (var change in changes)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, change.SoLineNum, ct);

            // G5: MolasIntegration PLR picked quantity
            if (plrs.Any(p => p.PickedQty > 0))
                return WfPreflightResult.Blocked(
                    "ZF_LINE_PICKED",
                    $"Line {change.SoLineNum} ({change.ItemCode}) has PickedQty > 0.",
                    change.SoLineNum);

            // G6/G7/G8: SAP-native OPKL checks (only when a PLR exists for this line)
            var activePlr = plrs.FirstOrDefault(p => p.PickListAbsEntry > 0
                                                   && p.Status != PickListStatus.Closed);
            if (activePlr != null)
            {
                // G6.5: Active OPKL exists — SAP already assigned bins for the current WHS.
                // When RDR1.WhsCode changes, SAP may reassign OPKL bins from a different
                // physical warehouse (wherever stock exists), causing a bin/WHS mismatch
                // at ODLN.Add() time: ODLN carries the new WHS but the picked bin belongs
                // to the old bin pool. The picker must cancel the OPKL in SAP first
                // (so the next AutoCreatePickLists run creates a new one targeting the
                // correct WHS), then retry the warehouse change.
                return WfPreflightResult.Blocked(
                    "ZF_OPKL_EXISTS",
                    $"An active pick list (AbsEntry={activePlr.PickListAbsEntry}) already exists for " +
                    $"line {change.SoLineNum} ({change.ItemCode}). Cancel it in SAP before changing " +
                    $"warehouse {change.CurrentWhsCode}→{change.RequestedWhsCode}.",
                    change.SoLineNum);
            }
        }

        return WfPreflightResult.Pass(changes);
    }

    // ── ApplyOperationalWarehouseChangesAsync ──────────────────────────────────

    public async Task<WhsApplyResult> ApplyOperationalWarehouseChangesAsync(
        int                              soDocEntry,
        IReadOnlyList<WarehouseLineChange> changes,
        string                           changedBy,
        CancellationToken                ct)
    {
        var lineResults = new List<WhsLineApplyResult>();
        bool anySyncFailed = false;
        string? syncError  = null;

        foreach (var change in changes)
        {
            try
            {
                int rows = await _repo.UpdateSoLineFragmentWhsCodeAsync(
                    change.SoLineFragmentId,
                    change.CurrentWhsCode,
                    change.RequestedWhsCode,
                    changedBy,
                    ct);

                if (rows == 0)
                {
                    // Check idempotency: already at target?
                    var frags = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
                    var frag  = frags.FirstOrDefault(f => f.Id == change.SoLineFragmentId);
                    bool alreadyAtTarget = frag != null
                        && string.Equals(frag.WhsCode, change.RequestedWhsCode, StringComparison.OrdinalIgnoreCase);

                    if (alreadyAtTarget)
                    {
                        lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, Skipped = true });
                    }
                    else
                    {
                        _log.LogWarning(
                            "[ZF-WHC] Optimistic concurrency conflict SoDocEntry={Doc} SoLineNum={Line} " +
                            "PriorWhs={Prior} RequestedWhs={New}",
                            soDocEntry, change.SoLineNum, change.CurrentWhsCode, change.RequestedWhsCode);
                        lineResults.Add(new WhsLineApplyResult
                        {
                            SoLineNum = change.SoLineNum,
                            ConcurrencyConflict = true
                        });
                        anySyncFailed = true;
                        syncError = $"Concurrency conflict on line {change.SoLineNum}";
                    }
                }
                else
                {
                    _log.LogInformation(
                        "[ZF-WHC] SoLineFragment updated SoDocEntry={Doc} SoLineNum={Line} " +
                        "{Old}→{New} ChangedBy={By}",
                        soDocEntry, change.SoLineNum, change.CurrentWhsCode, change.RequestedWhsCode, changedBy);

                    // Mirror WHS change to PLR so CreatePickListsAsync idempotency check
                    // can locate the existing PLR after a WHS change.
                    int plrRows = await _repo.UpdatePickListRecordWhsCodeAsync(
                        change.SoLineFragmentId, change.CurrentWhsCode, change.RequestedWhsCode, ct);
                    if (plrRows > 0)
                        _log.LogInformation(
                            "[ZF-WHC] PLR WhsCode updated SoDocEntry={Doc} SoLineNum={Line} {Old}→{New} rows={Rows}",
                            soDocEntry, change.SoLineNum, change.CurrentWhsCode, change.RequestedWhsCode, plrRows);

                    lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, Applied = true });
                }
            }
            catch (Exception ex)
            {
                anySyncFailed = true;
                syncError = ex.Message;
                lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, Error = ex.Message });

                _log.LogError(ex,
                    "[ZF-WHC] WAREHOUSE_REASSIGNMENT_RECONCILIATION_REQUIRED " +
                    "SoDocEntry={Doc} SoLineNum={Line} {Old}→{New} — SAP succeeded but SQL sync failed. " +
                    "Reconciliation required: SAP RDR1 WhsCode != SoLineFragment.WhsCode.",
                    soDocEntry, change.SoLineNum, change.CurrentWhsCode, change.RequestedWhsCode);
            }
        }

        return new WhsApplyResult
        {
            AllApplied  = lineResults.All(r => r.Applied || r.Skipped),
            SyncFailed  = anySyncFailed,
            SyncError   = syncError,
            LineResults = lineResults
        };
    }

    // ── ValidateDeliveryWhsConsistencyAsync ────────────────────────────────────

    public async Task<WhsDeliveryGateResult> ValidateDeliveryWhsConsistencyAsync(
        Guid requestId, CancellationToken ct)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch == null || !orch.SoDocEntry.HasValue)
            return WhsDeliveryGateResult.Ok(); // no orchestration → not ZF, pass through

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(orch.SoDocEntry.Value, ct);
        if (fragments.Count == 0)
            return WhsDeliveryGateResult.Ok();

        foreach (var frag in fragments)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(orch.SoDocEntry.Value, frag.SoLineNum, ct);
            foreach (var plr in plrs)
            {
                if (plr.Status == PickListStatus.Closed) continue;
                if (!string.Equals(frag.WhsCode, plr.WhsCode, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError(
                        "[ZF-WHC-DLV-GATE] WAREHOUSE_BIN_MISMATCH SoDocEntry={Doc} SoLineNum={Line} " +
                        "Fragment.WhsCode={FragWhs} PLR.WhsCode={PlrWhs} AbsEntry={Abs}",
                        orch.SoDocEntry.Value, frag.SoLineNum, frag.WhsCode, plr.WhsCode, plr.PickListAbsEntry);
                    return WhsDeliveryGateResult.Fail(
                        "WAREHOUSE_BIN_MISMATCH",
                        $"SoLineFragment.WhsCode={frag.WhsCode} != PLR.WhsCode={plr.WhsCode} " +
                        $"for SO line {frag.SoLineNum}. Run warehouse reconciliation before delivery.");
                }
            }
        }

        return WhsDeliveryGateResult.Ok();
    }

    // ── ReconcileWhsStateBySoDocEntryAsync ─────────────────────────────────────

    public async Task<int> ReconcileWhsStateBySoDocEntryAsync(
        int    soDocEntry,
        string reconciledBy,
        CancellationToken ct)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null)
        {
            _log.LogWarning("[ZF-WHC-RECON] No FulfillmentOrchestration for SoDocEntry={Doc}", soDocEntry);
            return 0;
        }

        if (orch.State != OrchestrationState.Accepted)
        {
            _log.LogWarning("[ZF-WHC-RECON] Skipping reconciliation: State={State} SoDocEntry={Doc}",
                orch.State, soDocEntry);
            return 0;
        }

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0) return 0;

        // Safety: fail closed if any picks exist
        foreach (var frag in fragments)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, frag.SoLineNum, ct);
            if (plrs.Any(p => p.PickedQty > 0))
            {
                _log.LogError(
                    "[ZF-WHC-RECON] FAIL CLOSED: PickedQty > 0 SoDocEntry={Doc} SoLineNum={Line}. " +
                    "Auto-reconciliation blocked after physical pick.",
                    soDocEntry, frag.SoLineNum);
                return 0;
            }
        }

        // Safety: fail closed if active delivery exists
        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
        {
            _log.LogWarning("[ZF-WHC-RECON] Active delivery exists — skipping reconciliation SoDocEntry={Doc}", soDocEntry);
            return 0;
        }

        int repaired = 0;
        foreach (var frag in fragments)
        {
            string? sapWhs = await _repo.GetRdr1WhsCodeAsync(soDocEntry, frag.SoLineNum, ct);
            if (sapWhs == null) continue;
            if (string.Equals(sapWhs, frag.WhsCode, StringComparison.OrdinalIgnoreCase)) continue;

            // Divergence detected: SAP RDR1 has sapWhs, SoLineFragment has frag.WhsCode
            _log.LogInformation(
                "[ZF-WHC-RECON] Divergence SoDocEntry={Doc} SoLineNum={Line} " +
                "SAP={Sap} Molas={Molas} — repairing",
                soDocEntry, frag.SoLineNum, sapWhs, frag.WhsCode);

            int rows = await _repo.UpdateSoLineFragmentWhsCodeAsync(
                frag.Id, frag.WhsCode, sapWhs, reconciledBy, ct);

            if (rows == 1) repaired++;
            else _log.LogWarning("[ZF-WHC-RECON] Update returned 0 rows SoDocEntry={Doc} SoLineNum={Line}",
                soDocEntry, frag.SoLineNum);
        }

        if (repaired > 0)
            _log.LogInformation("[ZF-WHC-RECON] Repaired {N} line(s) SoDocEntry={Doc}", repaired, soDocEntry);

        return repaired;
    }
}
