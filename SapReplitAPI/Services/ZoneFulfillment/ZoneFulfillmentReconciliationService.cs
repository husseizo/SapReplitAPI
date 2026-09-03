using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// §6-§8: Reconciles ghost orchestration state for cancelled SAP sales orders.
///
/// Invariants:
///   - Read-only SAP access only (no ORDR/OPKL/ODLN/OINV mutation).
///   - MolasIntegration PLR rows updated to Closed from SAP truth.
///   - Orchestration moved to Canceled terminal state.
///   - WAREHOUSE_PHYSICAL_RECONCILIATION_REQUIRED list generated for
///     any OPKL with PickQtty > 0 and no ODLN (stock physically pulled).
///   - workflow_eligible = false for all reconciled pick lists.
/// </summary>
public sealed class ZoneFulfillmentReconciliationService
{
    private readonly ZoneFulfillmentRepository                     _repo;
    private readonly SapService                                    _sap;
    private readonly ILogger<ZoneFulfillmentReconciliationService> _log;

    public ZoneFulfillmentReconciliationService(
        ZoneFulfillmentRepository                     repo,
        SapService                                    sap,
        ILogger<ZoneFulfillmentReconciliationService> log)
    {
        _repo = repo;
        _sap  = sap;
        _log  = log;
    }

    /// <summary>
    /// §6-§8: Reconcile a ghost orchestration against live SAP truth.
    ///
    /// If the parent ORDR is cancelled (ORDR.CANCELED=Y):
    ///   1. Set orchestration state → Canceled.
    ///   2. For each PLR: read SAP PKL1 truth, update MolasIntegration PLR to Closed.
    ///   3. Return WAREHOUSE_PHYSICAL_RECONCILIATION_REQUIRED for any OPKL
    ///      where PickQtty > 0 and no ODLN exists.
    ///
    /// This method never creates or modifies any SAP document.
    /// </summary>
    public async Task<CancelledOrderReconciliationResult> ReconcileCancelledOrderAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
            throw new InvalidOperationException($"RequestId {requestId} not found.");

        if (orch.SoDocEntry is null)
            throw new InvalidOperationException($"Orchestration {requestId} has no SoDocEntry — cannot reconcile.");

        int soDocEntry = orch.SoDocEntry.Value;

        // §6: Check live SAP ORDR.CANCELED
        bool isCancelled = _sap.GetSapOrderCancelledState(soDocEntry);
        _log.LogInformation("[ZF-RECONCILE] RequestId={Rid} SoDocEntry={So} CANCELED={C}",
            requestId, soDocEntry, isCancelled);

        if (!isCancelled)
        {
            return new CancelledOrderReconciliationResult
            {
                RequestId          = requestId,
                SoDocEntry         = soDocEntry,
                SoIsCancelled      = false,
                PreviousOrchState  = orch.State,
                NewOrchState       = orch.State,
                StateMutated       = false,
                PickLists          = new(),
                PhysicalPickExceptions = new(),
                Verdict            = "ORDER_NOT_CANCELLED — no reconciliation needed"
            };
        }

        // Load all fragments and their latest PLRs
        var fragments = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        var plrs      = await _repo.GetPickListRecordsAsync(orch.Id, ct);

        string previousState = orch.State;
        var pickListInfos    = new List<CancelledPickListInfo>();
        var physicalExceptions = new List<WarehousePhysicalReconciliationItem>();

        // Check if a ODLN exists for this orchestration (MolasIntegration truth)
        var deliveryRecords = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        bool hasDelivery    = deliveryRecords.Any(d => d.Status == DeliveryRecordStatus.Created);

        foreach (var plr in plrs)
        {
            // §7: Read SAP PKL1 truth for this PLR
            var frag = fragments.FirstOrDefault(f => f.Id == plr.SoLineFragmentId);
            if (frag is null) continue;

            var sapPkl1 = _sap.ReadZoneFulfillmentPickListLine(
                plr.PickListAbsEntry, plr.SoDocEntry, plr.SoLineNum);

            decimal sapPickQtty = sapPkl1?.PickQtty ?? 0m;
            string  sapPickStatus = sapPkl1?.PickStatus ?? "?";
            string  sapOpklStatus = sapPkl1 is not null ? "C" : "?"; // auto-closed by SAP on ORDR cancel

            string newMolasStatus = PickListStatus.Closed;

            // §7: Update PLR to Closed with SAP-truth PickedQty
            await _repo.UpdatePickListPickedQtyAsync(plr.Id, sapPickQtty, newMolasStatus, ct);
            _log.LogInformation(
                "[ZF-RECONCILE] §7 PLR.Id={Id} AbsEntry={Abs} SAP PickQtty={Qty} → MolasStatus={St}",
                plr.Id, plr.PickListAbsEntry, sapPickQtty, newMolasStatus);

            pickListInfos.Add(new CancelledPickListInfo
            {
                AbsEntry         = plr.PickListAbsEntry,
                SapStatus        = sapOpklStatus,
                RelQtty          = plr.ReleasedQty,
                PickQtty         = sapPickQtty,
                SapPickStatus    = sapPickStatus,
                MolasStatus      = plr.Status,
                NewMolasStatus   = newMolasStatus,
                WorkflowEligible = false
            });

            // §8: Physical pick exception — PickQtty > 0 and no ODLN
            if (sapPickQtty > 0 && !hasDelivery)
            {
                // Get PKL2 bin detail for this OPKL to identify physical location
                var (_, _, pkl2Bins) = _sap.GetPickListFullState(plr.PickListAbsEntry);
                foreach (var bin in pkl2Bins.Where(b => b.PickQtty > 0))
                {
                    physicalExceptions.Add(new WarehousePhysicalReconciliationItem
                    {
                        PickListAbsEntry = plr.PickListAbsEntry,
                        ItemCode         = frag.ItemCode,
                        WhsCode          = frag.WhsCode,
                        SoDocEntry       = plr.SoDocEntry,
                        SoLineNum        = plr.SoLineNum,
                        PickQtty         = bin.PickQtty,
                        SapPickStatus    = sapPickStatus,
                        BinAbs           = bin.BinAbs,
                        BinCode          = bin.BinCode
                    });
                }
                // If no PKL2 rows but PickQtty > 0, still add exception with BinAbs=0
                if (pkl2Bins.Count == 0 || !pkl2Bins.Any(b => b.PickQtty > 0))
                {
                    physicalExceptions.Add(new WarehousePhysicalReconciliationItem
                    {
                        PickListAbsEntry = plr.PickListAbsEntry,
                        ItemCode         = frag.ItemCode,
                        WhsCode          = frag.WhsCode,
                        SoDocEntry       = plr.SoDocEntry,
                        SoLineNum        = plr.SoLineNum,
                        PickQtty         = sapPickQtty,
                        SapPickStatus    = sapPickStatus,
                        BinAbs           = 0,
                        BinCode          = "BIN_UNKNOWN"
                    });
                }
            }
        }

        // §6: Set orchestration to Canceled (only if not already)
        bool stateMutated = false;
        string newOrchState = orch.State;
        if (orch.State != OrchestrationState.Canceled)
        {
            await _repo.UpdateStateAsync(orch.Id, OrchestrationState.Canceled, ct);
            newOrchState = OrchestrationState.Canceled;
            stateMutated = true;
            _log.LogInformation("[ZF-RECONCILE] §6 OrchestrationId={Id} state: {Prev} → Canceled",
                orch.Id, previousState);
        }

        string verdict = physicalExceptions.Count > 0
            ? $"CANCELED_ORDER_RECONCILED — WAREHOUSE_PHYSICAL_RECONCILIATION_REQUIRED for {physicalExceptions.Count} item(s)"
            : "CANCELED_ORDER_RECONCILED — no undelivered physical picks";

        _log.LogInformation("[ZF-RECONCILE] RequestId={Rid} verdict={V}", requestId, verdict);

        return new CancelledOrderReconciliationResult
        {
            RequestId          = requestId,
            SoDocEntry         = soDocEntry,
            SoIsCancelled      = true,
            PreviousOrchState  = previousState,
            NewOrchState       = newOrchState,
            StateMutated       = stateMutated,
            PickLists          = pickListInfos,
            PhysicalPickExceptions = physicalExceptions,
            Verdict            = verdict
        };
    }
}
