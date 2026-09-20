using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.SoDelivery;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Coordinates ZF Sales Order edits for in-flight (Accepted) orders.
/// GREEN path: no OPKL — UpdateOrder + sync fragment.
/// AMBER path: released zero-pick OPKL — durable replan tracked in dbo.ZfReplanOperation.
/// Failure rules A-E: see IZoneFulfillmentOrderEditCoordinator summary.
/// </summary>
public sealed class ZoneFulfillmentOrderEditCoordinator : IZoneFulfillmentOrderEditCoordinator
{
    private readonly ZoneFulfillmentRepository      _repo;
    private readonly SapService                     _sap;
    private readonly IZfWhsChangeSapReader          _sapReader;
    private readonly ZoneFulfillmentPickListService _plService;
    private readonly IPickListEventRefreshService   _refresh;
    private readonly ILogger<ZoneFulfillmentOrderEditCoordinator> _log;

    public ZoneFulfillmentOrderEditCoordinator(
        ZoneFulfillmentRepository      repo,
        SapService                     sap,
        IZfWhsChangeSapReader          sapReader,
        ZoneFulfillmentPickListService plService,
        IPickListEventRefreshService   refresh,
        ILogger<ZoneFulfillmentOrderEditCoordinator> log)
    {
        _repo      = repo;
        _sap       = sap;
        _sapReader = sapReader;
        _plService = plService;
        _refresh   = refresh;
        _log       = log;
    }

    public async Task<OrderEditResult> ExecuteEditAsync(
        int soDocEntry, UpdateOrderDto dto, string changedBy, CancellationToken ct)
    {
        // G1: ZF enrolled?
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null)
            return OrderEditResult.NotZf();

        // G2: Must be Accepted
        if (orch.State != OrchestrationState.Accepted)
            return OrderEditResult.Blocked(
                "ZF_INVALID_STATE",
                $"ZF edit requires orchestration State=Accepted. Current: {orch.State}");

        // G3: Must have fragments
        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0)
            return OrderEditResult.Blocked("ZF_NO_FRAGMENTS", "No SoLineFragment rows found for this order.");

        // G4: No active delivery
        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
            return OrderEditResult.Blocked("ZF_ACTIVE_DELIVERY", "An active Delivery exists — edit blocked.");

        // Detect fulfillment-affecting changes
        var dtoMap = dto.UpdatedLines.ToDictionary(l => l.LineNum);
        var changedFragments = new List<(SoLineFragmentRecord frag, OrderLineDto? dtoLine)>();

        foreach (var frag in fragments)
        {
            if (!dtoMap.TryGetValue(frag.SoLineNum, out var dtoLine))
            {
                changedFragments.Add((frag, null));
                continue;
            }
            bool whsChanged  = !string.IsNullOrWhiteSpace(dtoLine.WhsCode)
                                && !string.Equals(frag.WhsCode, dtoLine.WhsCode, StringComparison.OrdinalIgnoreCase);
            bool itemChanged = !string.IsNullOrWhiteSpace(dtoLine.ItemCode)
                                && !string.Equals(frag.ItemCode, dtoLine.ItemCode, StringComparison.OrdinalIgnoreCase);
            bool qtyChanged  = dtoLine.Quantity > 0
                                && Math.Abs((double)frag.SoLineQty - dtoLine.Quantity) > 0.001;
            if (whsChanged || itemChanged || qtyChanged)
                changedFragments.Add((frag, dtoLine));
        }

        if (changedFragments.Count == 0)
            return OrderEditResult.NotZf();

        // Section 1: Classify ALL affected lines BEFORE any SAP mutation.
        // If any line fails, zero mutations occur and we return a clean block.
        var amberLines = new List<(SoLineFragmentRecord frag, PickListRecordModel activePlr)>();

        foreach (var (frag, _) in changedFragments)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, frag.SoLineNum, ct);

            if (plrs.Any(p => p.PickedQty > 0))
                return OrderEditResult.Blocked(
                    "ZF_LINE_PICKED",
                    $"Line {frag.SoLineNum} ({frag.ItemCode}) has PickedQty > 0. Cannot edit.",
                    frag.SoLineNum);

            var activePlr = plrs.FirstOrDefault(
                p => p.PickListAbsEntry > 0 && p.Status != PickListStatus.Closed);

            if (activePlr == null) continue; // GREEN

            var pkl1      = _sapReader.ReadPickListLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum);
            decimal pkl1q = pkl1?.PickQtty ?? 0m;
            decimal pkl2q = _sapReader.GetPkl2PickQttyForLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum);

            if (pkl1q > 0 || pkl2q > 0)
                return OrderEditResult.Blocked(
                    "ZF_PHYSICAL_PICK_STARTED",
                    $"Line {frag.SoLineNum} ({frag.ItemCode}) has physical picks in progress " +
                    $"(PKL1={pkl1q}, PKL2={pkl2q}). Cannot edit.",
                    frag.SoLineNum);

            amberLines.Add((frag, activePlr)); // AMBER: zero picks
        }

        bool isAmber = amberLines.Count > 0;

        if (!isAmber)
        {
            // GREEN path — no durable record needed
            bool sapOk = _sap.UpdateOrder(dto);
            if (!sapOk)
                return OrderEditResult.Blocked("ZF_SAP_UPDATE_FAILED",
                    $"SapService.UpdateOrder returned false for DocEntry={soDocEntry}.");

            _log.LogInformation("[ZF-EDIT] GREEN UpdateOrder succeeded SoDocEntry={Doc}", soDocEntry);

            foreach (var (frag, dtoLine) in changedFragments)
            {
                if (dtoLine == null) continue;
                string  newWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag.WhsCode  : dtoLine.WhsCode;
                string  newItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag.ItemCode : dtoLine.ItemCode;
                decimal newQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag.SoLineQty;
                try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, newWhs, newItem, newQty, changedBy, ct); }
                catch (Exception ex) { _log.LogError(ex, "[ZF-EDIT] Fragment sync non-fatal SoDocEntry={Doc} Line={L}", soDocEntry, frag.SoLineNum); }
            }

            return OrderEditResult.Success(wasAmber: false);
        }

        // ── AMBER path ────────────────────────────────────────────────────────

        // Concurrency guard: reject if another replan is already active (Section 6)
        var existing = await _repo.FindActiveReplanOperationAsync(soDocEntry, ct);
        if (existing != null)
            return OrderEditResult.Blocked(
                "ZF_REPLAN_IN_PROGRESS",
                $"Replan {existing.OperationId} is already active for this order (step={existing.CurrentStep}). " +
                "Resume it or wait for it to complete.");

        var oldAbsEntries = amberLines.Select(a => a.activePlr.PickListAbsEntry).ToList();
        var operationId = await _repo.CreateReplanOperationAsync(new ZfReplanOperationRecord
        {
            SoDocEntry        = soDocEntry,
            RequestId         = orch.RequestId,
            ChangedBy         = changedBy,
            CurrentStep       = ReplanStep.Prepared,
            DtoJson           = System.Text.Json.JsonSerializer.Serialize(dto),
            OldAbsEntriesJson = System.Text.Json.JsonSerializer.Serialize(oldAbsEntries),
            StartedAtUtc      = DateTime.UtcNow
        }, ct);

        _log.LogInformation("[ZF-EDIT] AMBER replan created OperationId={OpId} SoDocEntry={Doc}", operationId, soDocEntry);

        // Rule A/B: Cancel OPKLs. First failure before any cancel → FailedBeforeMutation.
        // First failure after any cancel → RecoveryRequired.
        bool anyOPKLCancelled = false;
        foreach (var (frag, activePlr) in amberLines)
        {
            var (closed, closeErr) = _sap.CloseZoneFulfillmentPickList(activePlr.PickListAbsEntry);
            if (!closed)
            {
                if (!anyOPKLCancelled)
                {
                    // Rule A: no mutation occurred — safe failure
                    await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FailedBeforeMutation, ct);
                    return OrderEditResult.Blocked("ZF_OPKL_CLOSE_FAILED",
                        $"Failed to cancel OPKL AbsEntry={activePlr.PickListAbsEntry}: {closeErr}. " +
                        "No SAP mutations occurred.",
                        frag.SoLineNum);
                }
                // Rule B: partial cancel — RecoveryRequired
                await _repo.SetReplanRecoveryAsync(operationId,
                    $"Partial cancel: AbsEntry={activePlr.PickListAbsEntry} failed after earlier OPKL(s) retired: {closeErr}", ct);
                _log.LogError("[ZF-EDIT] AMBER_PARTIAL_CANCEL OperationId={OpId} FailedAbsEntry={Abs}",
                    operationId, activePlr.PickListAbsEntry);
                return OrderEditResult.RecoveryRequired(
                    "ZF_PARTIAL_CANCEL_FAILED",
                    $"AbsEntry={activePlr.PickListAbsEntry} cancel failed after earlier OPKL(s) were already retired. " +
                    "Manual recovery required.",
                    operationId);
            }

            await _repo.UpdatePickListPickedQtyAsync(activePlr.Id, 0m, PickListStatus.Closed, ct);
            anyOPKLCancelled = true;
            _log.LogInformation("[ZF-EDIT] AMBER PLR retired Id={Id} AbsEntry={Abs}", activePlr.Id, activePlr.PickListAbsEntry);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.OldPickListsRetired, ct);

        // Rule C: UpdateOrder. Failure after all OPKLs retired → RecoveryRequired.
        bool sapUpdated = _sap.UpdateOrder(dto);
        if (!sapUpdated)
        {
            await _repo.SetReplanRecoveryAsync(operationId, "UpdateOrder returned false.", ct);
            _log.LogError("[ZF-EDIT] AMBER_SAP_UPDATE_FAILED OperationId={OpId} SoDocEntry={Doc}", operationId, soDocEntry);
            return OrderEditResult.RecoveryRequired(
                "ZF_SAP_UPDATE_FAILED",
                $"SO update failed for DocEntry={soDocEntry}. Old OPKLs were retired; SO not updated.",
                operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.SalesOrderUpdated, ct);
        _log.LogInformation("[ZF-EDIT] AMBER UpdateOrder succeeded OperationId={OpId}", operationId);

        // Rule D: Fragment sync. Failure → RecoveryRequired; delivery will fail closed.
        bool syncFailed  = false;
        string? syncError = null;
        foreach (var (frag, dtoLine) in changedFragments)
        {
            if (dtoLine == null) continue;
            string  newWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag.WhsCode  : dtoLine.WhsCode;
            string  newItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag.ItemCode : dtoLine.ItemCode;
            decimal newQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag.SoLineQty;
            try
            {
                await _repo.UpdateSoLineFragmentEditAsync(frag.Id, newWhs, newItem, newQty, changedBy, ct);
            }
            catch (Exception ex)
            {
                syncFailed = true;
                syncError  = ex.Message;
                _log.LogError(ex, "[ZF-EDIT] AMBER_FRAGMENT_SYNC_FAILED OperationId={OpId} SoDocEntry={Doc} Line={L}",
                    operationId, soDocEntry, frag.SoLineNum);
                break;
            }
        }

        if (syncFailed)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"Fragment sync failed: {syncError}", ct);
            return OrderEditResult.RecoveryRequired(
                "ZF_FRAGMENT_SYNC_FAILED",
                $"SO updated but fragment sync failed: {syncError}. Delivery blocked pending recovery.",
                operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FragmentsSynchronized, ct);

        // Rule E: Create replacement OPKLs. Failure → RecoveryRequired; no delivery until resolved.
        List<int> newAbsEntries;
        try
        {
            var plResult = await _plService.CreatePickListsAsync(orch.RequestId, ct);
            newAbsEntries = plResult.PickLists.Select(p => p.PickListAbsEntry).ToList();
        }
        catch (Exception ex)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"OPKL creation failed: {ex.Message}", ct);
            _log.LogError(ex, "[ZF-EDIT] AMBER_OPKL_CREATE_FAILED OperationId={OpId} SoDocEntry={Doc}", operationId, soDocEntry);
            return OrderEditResult.RecoveryRequired(
                "ZF_REPLAN_OPKL_CREATE_FAILED",
                $"SO updated but OPKL creation failed: {ex.Message}. Order is REPLAN INCOMPLETE.",
                operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ReplacementPickListsCreated, ct);
        await _repo.SetReplanNewAbsEntriesAsync(operationId,
            System.Text.Json.JsonSerializer.Serialize(newAbsEntries), ct);
        await _repo.CompleteReplanAsync(operationId, ct);

        _log.LogInformation("[ZF-EDIT] AMBER replan completed OperationId={OpId} newOPKLs={N}",
            operationId, newAbsEntries.Count);

        return OrderEditResult.Success(wasAmber: true, plAbsEntries: newAbsEntries, operationId: operationId);
    }

    public async Task<OrderEditResult> ResumeReplanAsync(Guid operationId, CancellationToken ct)
    {
        var op = await _repo.FindReplanByOperationIdAsync(operationId, ct);
        if (op == null)
            return OrderEditResult.Blocked("ZF_REPLAN_NOT_FOUND", $"Replan {operationId} not found.");

        if (op.CurrentStep == ReplanStep.Completed)
        {
            var existingEntries = op.NewAbsEntriesJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.NewAbsEntriesJson) ?? []
                : (List<int>)[];
            return OrderEditResult.Success(wasAmber: true, plAbsEntries: existingEntries, operationId: operationId);
        }

        if (op.CurrentStep == ReplanStep.FailedBeforeMutation)
            return OrderEditResult.Blocked("ZF_REPLAN_FAILED_BEFORE_MUTATION",
                $"Replan {operationId} failed before any SAP mutation. Submit a new edit request instead.");

        // Use LastGoodStep to determine where to resume from
        int resumeOrd = ReplanStep.Ordinal(op.LastGoodStep); // -1 if no step completed yet

        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(op.SoDocEntry, ct);
        if (orch == null)
            return OrderEditResult.Blocked("ZF_NO_ORCHESTRATION",
                $"No orchestration found for SoDocEntry={op.SoDocEntry}.");

        var dto       = System.Text.Json.JsonSerializer.Deserialize<UpdateOrderDto>(op.DtoJson)!;
        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(op.SoDocEntry, ct);
        var oldAbsEntries = System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.OldAbsEntriesJson) ?? [];

        // Step 1: Retire old OPKLs (idempotent — skip already-closed PLRs)
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.OldPickListsRetired))
        {
            foreach (var absEntry in oldAbsEntries)
            {
                // Check if this PLR is already closed
                bool alreadyClosed = false;
                foreach (var frag in fragments)
                {
                    var plrs = await _repo.GetPickListRecordsBySoLineAsync(op.SoDocEntry, frag.SoLineNum, ct);
                    var plr  = plrs.FirstOrDefault(p => p.PickListAbsEntry == absEntry);
                    if (plr?.Status == PickListStatus.Closed) { alreadyClosed = true; break; }
                }
                if (alreadyClosed) continue;

                var (closed, closeErr) = _sap.CloseZoneFulfillmentPickList(absEntry);
                if (!closed)
                {
                    await _repo.SetReplanRecoveryAsync(operationId,
                        $"Resume: cancel AbsEntry={absEntry} failed: {closeErr}", ct);
                    return OrderEditResult.RecoveryRequired("ZF_OPKL_CLOSE_FAILED",
                        $"Resume: cancel AbsEntry={absEntry} failed: {closeErr}", operationId);
                }

                foreach (var frag in fragments)
                {
                    var plrs = await _repo.GetPickListRecordsBySoLineAsync(op.SoDocEntry, frag.SoLineNum, ct);
                    var plr  = plrs.FirstOrDefault(p => p.PickListAbsEntry == absEntry && p.Status != PickListStatus.Closed);
                    if (plr != null)
                        await _repo.UpdatePickListPickedQtyAsync(plr.Id, 0m, PickListStatus.Closed, ct);
                }
            }
            await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.OldPickListsRetired, ct);
            resumeOrd = ReplanStep.Ordinal(ReplanStep.OldPickListsRetired);
        }

        // Step 2: UpdateOrder (internal) or ExternalSalesOrderAccepted (SAP GUI path)
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.SalesOrderUpdated))
        {
            bool isExternal = op.ChangedBy.StartsWith("sap-gui:", StringComparison.Ordinal);
            if (isExternal)
            {
                // SO was already updated by SAP GUI — do not call UpdateOrder (causes 17/U loop)
                await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ExternalSalesOrderAccepted, ct);
                resumeOrd = ReplanStep.Ordinal(ReplanStep.ExternalSalesOrderAccepted);
            }
            else
            {
                bool sapOk = _sap.UpdateOrder(dto);
                if (!sapOk)
                {
                    await _repo.SetReplanRecoveryAsync(operationId, "Resume: UpdateOrder returned false.", ct);
                    return OrderEditResult.RecoveryRequired("ZF_SAP_UPDATE_FAILED",
                        $"Resume: SO update failed for DocEntry={op.SoDocEntry}.", operationId);
                }
                await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.SalesOrderUpdated, ct);
                resumeOrd = ReplanStep.Ordinal(ReplanStep.SalesOrderUpdated);
            }
        }

        // Step 3: Fragment sync (idempotent — updating to same values is harmless)
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.FragmentsSynchronized))
        {
            var dtoMap = dto.UpdatedLines.ToDictionary(l => l.LineNum);
            foreach (var frag in fragments)
            {
                if (!dtoMap.TryGetValue(frag.SoLineNum, out var dtoLine)) continue;
                string  newWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag.WhsCode  : dtoLine.WhsCode;
                string  newItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag.ItemCode : dtoLine.ItemCode;
                decimal newQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag.SoLineQty;
                try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, newWhs, newItem, newQty, op.ChangedBy, ct); }
                catch (Exception ex)
                {
                    await _repo.SetReplanRecoveryAsync(operationId, $"Resume: fragment sync failed: {ex.Message}", ct);
                    return OrderEditResult.RecoveryRequired("ZF_FRAGMENT_SYNC_FAILED",
                        $"Resume: fragment sync failed: {ex.Message}", operationId);
                }
            }
            await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FragmentsSynchronized, ct);
            resumeOrd = ReplanStep.Ordinal(ReplanStep.FragmentsSynchronized);
        }

        // Step 4: Create replacement OPKLs (idempotent via FindPickListRecordAsync in plService)
        List<int> newAbsEntries;
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.ReplacementPickListsCreated))
        {
            try
            {
                var plResult = await _plService.CreatePickListsAsync(orch.RequestId, ct);
                newAbsEntries = plResult.PickLists.Select(p => p.PickListAbsEntry).ToList();
            }
            catch (Exception ex)
            {
                await _repo.SetReplanRecoveryAsync(operationId, $"Resume: OPKL creation failed: {ex.Message}", ct);
                return OrderEditResult.RecoveryRequired("ZF_REPLAN_OPKL_CREATE_FAILED",
                    $"Resume: OPKL creation failed: {ex.Message}", operationId);
            }
            await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ReplacementPickListsCreated, ct);
            await _repo.SetReplanNewAbsEntriesAsync(operationId,
                System.Text.Json.JsonSerializer.Serialize(newAbsEntries), ct);
        }
        else
        {
            newAbsEntries = op.NewAbsEntriesJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.NewAbsEntriesJson) ?? []
                : [];
        }

        await _repo.CompleteReplanAsync(operationId, ct);
        _log.LogInformation("[ZF-EDIT] AMBER replan resumed and completed OperationId={OpId}", operationId);
        return OrderEditResult.Success(wasAmber: true, plAbsEntries: newAbsEntries, operationId: operationId);
    }

    public async Task<OrderEditResult> ReconcileExternalSapEditAsync(
        int soDocEntry, string eventId, string changedBy, CancellationToken ct)
    {
        // G1: ZF enrolled?
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null)
            return OrderEditResult.NotZf();

        // G2: Must be Accepted
        if (orch.State != OrchestrationState.Accepted)
            return OrderEditResult.Blocked("ZF_INVALID_STATE",
                $"ZF reconcile requires orchestration State=Accepted. Current: {orch.State}");

        // G3: Must have fragments
        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0)
            return OrderEditResult.NotZf();

        // G4: No active delivery
        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
            return OrderEditResult.Blocked("ZF_ACTIVE_DELIVERY",
                "An active Delivery exists — external reconcile blocked.");

        // Read current RDR1 state (SAP GUI already updated it)
        var rdr1Lines = _sap.GetOpenSoLines(soDocEntry);
        var rdr1Map   = rdr1Lines.ToDictionary(l => l.LineNum);

        // Find diverged fragments
        var divergedPairs = new List<(SoLineFragmentRecord frag, OpenSoLineDto rdr1)>();
        foreach (var frag in fragments)
        {
            if (!rdr1Map.TryGetValue(frag.SoLineNum, out var rdr1)) continue;
            bool whsDiff  = !string.Equals(frag.WhsCode,  rdr1.WhsCode,  StringComparison.OrdinalIgnoreCase);
            bool itemDiff = !string.Equals(frag.ItemCode, rdr1.ItemCode, StringComparison.OrdinalIgnoreCase);
            bool qtyDiff  = Math.Abs((double)frag.SoLineQty - (double)rdr1.Quantity) > 0.001;
            if (whsDiff || itemDiff || qtyDiff)
                divergedPairs.Add((frag, rdr1));
        }

        if (divergedPairs.Count == 0)
            return OrderEditResult.Success(wasAmber: false); // no divergence — no-op

        // Classify ALL diverged lines before any mutation
        var amberPairs = new List<(SoLineFragmentRecord frag, OpenSoLineDto rdr1, PickListRecordModel activePlr)>();
        foreach (var (frag, rdr1) in divergedPairs)
        {
            var plrs      = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, frag.SoLineNum, ct);
            var activePlr = plrs.FirstOrDefault(p => p.PickListAbsEntry > 0 && p.Status != PickListStatus.Closed);

            if (activePlr == null) continue; // GREEN-diverge: no active OPKL

            var pkl1  = _sapReader.ReadPickListLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum);
            decimal pkl1q = pkl1?.PickQtty ?? 0m;
            decimal pkl2q = _sapReader.GetPkl2PickQttyForLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum);

            if (pkl1q > 0 || pkl2q > 0)
                return OrderEditResult.Blocked(
                    "ZF_PHYSICAL_PICK_STARTED",
                    $"Line {frag.SoLineNum} ({frag.ItemCode}) has physical picks " +
                    $"(PKL1={pkl1q}, PKL2={pkl2q}). External reconcile blocked.",
                    frag.SoLineNum);

            amberPairs.Add((frag, rdr1, activePlr));
        }

        if (amberPairs.Count == 0)
        {
            // All diverged lines are GREEN-diverge (no active OPKLs) — sync fragments directly
            foreach (var (frag, rdr1) in divergedPairs)
            {
                try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, rdr1.WhsCode, rdr1.ItemCode, rdr1.Quantity, changedBy, ct); }
                catch (Exception ex) { _log.LogError(ex, "[ZF-EXT] Fragment sync non-fatal SoDocEntry={Doc} Line={L}", soDocEntry, frag.SoLineNum); }
            }
            return OrderEditResult.Success(wasAmber: false);
        }

        // Concurrency guard
        var existing = await _repo.FindActiveReplanOperationAsync(soDocEntry, ct);
        if (existing != null)
        {
            // Same event retried — resume the existing operation idempotently
            if (existing.ChangedBy == $"sap-gui:{eventId}")
            {
                _log.LogInformation("[ZF-EXT] Resuming existing replan for same event OperationId={OpId}", existing.OperationId);
                return await ResumeReplanAsync(existing.OperationId, ct);
            }
            return OrderEditResult.Blocked("ZF_REPLAN_IN_PROGRESS",
                $"Replan {existing.OperationId} already active (step={existing.CurrentStep}).");
        }

        // Build synthetic DTO from current RDR1 for idempotent resume (stores desired state)
        var syntheticDto = new UpdateOrderDto
        {
            DocEntry     = soDocEntry,
            UpdatedLines = divergedPairs.Select(p => new OrderLineDto
            {
                LineNum  = p.rdr1.LineNum,
                ItemCode = p.rdr1.ItemCode,
                WhsCode  = p.rdr1.WhsCode,
                Quantity = (int)Math.Round(p.rdr1.Quantity, MidpointRounding.AwayFromZero)
            }).ToList()
        };

        var oldAbsEntries = amberPairs.Select(a => a.activePlr.PickListAbsEntry).ToList();
        var operationId = await _repo.CreateReplanOperationAsync(new ZfReplanOperationRecord
        {
            SoDocEntry        = soDocEntry,
            RequestId         = orch.RequestId,
            ChangedBy         = $"sap-gui:{eventId}",
            CurrentStep       = ReplanStep.Prepared,
            DtoJson           = System.Text.Json.JsonSerializer.Serialize(syntheticDto),
            OldAbsEntriesJson = System.Text.Json.JsonSerializer.Serialize(oldAbsEntries),
            StartedAtUtc      = DateTime.UtcNow
        }, ct);

        _log.LogInformation("[ZF-EXT] AMBER external replan created OperationId={OpId} SoDocEntry={Doc} EventId={EventId}",
            operationId, soDocEntry, eventId);

        // Cancel OPKLs (Rules A/B)
        bool anyOPKLCancelled = false;
        foreach (var (frag, _, activePlr) in amberPairs)
        {
            var (closed, closeErr) = _sap.CloseZoneFulfillmentPickList(activePlr.PickListAbsEntry);
            if (!closed)
            {
                if (!anyOPKLCancelled)
                {
                    await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FailedBeforeMutation, ct);
                    return OrderEditResult.Blocked("ZF_OPKL_CLOSE_FAILED",
                        $"Failed to cancel OPKL AbsEntry={activePlr.PickListAbsEntry}: {closeErr}. No SAP mutations occurred.",
                        frag.SoLineNum);
                }
                await _repo.SetReplanRecoveryAsync(operationId,
                    $"Partial cancel: AbsEntry={activePlr.PickListAbsEntry} failed: {closeErr}", ct);
                return OrderEditResult.RecoveryRequired("ZF_PARTIAL_CANCEL_FAILED",
                    $"AbsEntry={activePlr.PickListAbsEntry} cancel failed after earlier OPKL(s) retired.", operationId);
            }
            await _repo.UpdatePickListPickedQtyAsync(activePlr.Id, 0m, PickListStatus.Closed, ct);
            anyOPKLCancelled = true;
        }
        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.OldPickListsRetired, ct);

        // Skip UpdateOrder — SAP GUI already made the change
        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ExternalSalesOrderAccepted, ct);
        _log.LogInformation("[ZF-EXT] External SO accepted OperationId={OpId}", operationId);

        // Fragment sync from current RDR1 state
        bool syncFailed  = false;
        string? syncError = null;
        foreach (var (frag, rdr1, _) in amberPairs)
        {
            try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, rdr1.WhsCode, rdr1.ItemCode, rdr1.Quantity, changedBy, ct); }
            catch (Exception ex)
            {
                syncFailed = true;
                syncError  = ex.Message;
                _log.LogError(ex, "[ZF-EXT] AMBER_FRAGMENT_SYNC_FAILED OperationId={OpId} Line={L}", operationId, frag.SoLineNum);
                break;
            }
        }

        // Also sync GREEN-diverge lines (no active OPKL) while we have the replan operation open
        if (!syncFailed)
        {
            var amberFragIds = amberPairs.Select(a => a.frag.Id).ToHashSet();
            foreach (var (frag, rdr1) in divergedPairs.Where(p => !amberFragIds.Contains(p.frag.Id)))
            {
                try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, rdr1.WhsCode, rdr1.ItemCode, rdr1.Quantity, changedBy, ct); }
                catch (Exception ex) { _log.LogError(ex, "[ZF-EXT] Fragment sync non-fatal SoDocEntry={Doc} Line={L}", soDocEntry, frag.SoLineNum); }
            }
        }

        if (syncFailed)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"Fragment sync failed: {syncError}", ct);
            return OrderEditResult.RecoveryRequired("ZF_FRAGMENT_SYNC_FAILED",
                $"External SO accepted but fragment sync failed: {syncError}. Delivery blocked pending recovery.",
                operationId);
        }
        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FragmentsSynchronized, ct);

        // Create replacement OPKLs
        List<int> newAbsEntries;
        try
        {
            var plResult = await _plService.CreatePickListsAsync(orch.RequestId, ct);
            newAbsEntries = plResult.PickLists.Select(p => p.PickListAbsEntry).ToList();
        }
        catch (Exception ex)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"OPKL creation failed: {ex.Message}", ct);
            _log.LogError(ex, "[ZF-EXT] AMBER_OPKL_CREATE_FAILED OperationId={OpId} SoDocEntry={Doc}", operationId, soDocEntry);
            return OrderEditResult.RecoveryRequired("ZF_REPLAN_OPKL_CREATE_FAILED",
                $"External SO accepted but OPKL creation failed: {ex.Message}. Order is REPLAN INCOMPLETE.",
                operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ReplacementPickListsCreated, ct);
        await _repo.SetReplanNewAbsEntriesAsync(operationId,
            System.Text.Json.JsonSerializer.Serialize(newAbsEntries), ct);
        await _repo.CompleteReplanAsync(operationId, ct);

        _log.LogInformation("[ZF-EXT] AMBER external replan completed OperationId={OpId} newOPKLs={N}",
            operationId, newAbsEntries.Count);
        return OrderEditResult.Success(wasAmber: true, plAbsEntries: newAbsEntries, operationId: operationId);
    }
}
