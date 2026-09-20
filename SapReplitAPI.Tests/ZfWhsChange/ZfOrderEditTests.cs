using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.SoDelivery;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfWhsChange;

// ─── Fakes for coordinator tests ─────────────────────────────────────────────

public sealed class FakeZfOrderEditRepo
{
    private FulfillmentOrchestrationRecord?    _orch;
    private readonly List<SoLineFragmentRecord>  _fragments = [];
    private readonly List<PickListRecordModel>   _plrs      = [];
    private bool _hasActiveDelivery;
    private readonly Dictionary<(int docEntry, int lineNum), string?> _rdr1Whs = new();
    private readonly Dictionary<Guid, ZfReplanOperationRecord> _replanOps = new();

    public List<(long Id, decimal Qty, string Status)> PlrRetirements { get; } = [];
    public List<(long Id, string Whs, string Item, decimal Qty)> FragmentEdits { get; } = [];
    public bool SimulateSqlFailure { get; set; }
    public bool SimulateFragmentSyncFailure { get; set; }
    public ZfReplanOperationRecord? LastReplanOp =>
        _replanOps.Values.OrderByDescending(o => o.StartedAtUtc).FirstOrDefault();

    public void SetOrchestration(FulfillmentOrchestrationRecord orch) => _orch = orch;
    public void AddFragment(SoLineFragmentRecord frag) => _fragments.Add(frag);
    public void AddPlr(PickListRecordModel plr) => _plrs.Add(plr);
    public void SetActiveDelivery(bool v) => _hasActiveDelivery = v;
    public void SetRdr1Whs(int docEntry, int lineNum, string? whs)
        => _rdr1Whs[(docEntry, lineNum)] = whs;
    public void SeedReplanOp(ZfReplanOperationRecord op) => _replanOps[op.OperationId] = op;

    public Task<FulfillmentOrchestrationRecord?> FindOrchestrationBySoDocEntryAsync(
        int docEntry, CancellationToken ct = default)
        => Task.FromResult(_orch?.SoDocEntry == docEntry ? _orch : null);

    public Task<List<SoLineFragmentRecord>> GetSoLineFragmentsBySoDocEntryAsync(
        int docEntry, CancellationToken ct = default)
        => Task.FromResult(_fragments.Where(f => f.SoDocEntry == docEntry).ToList());

    public Task<List<PickListRecordModel>> GetPickListRecordsBySoLineAsync(
        int docEntry, int lineNum, CancellationToken ct = default)
        => Task.FromResult(_plrs.Where(p => p.SoDocEntry == docEntry && p.SoLineNum == lineNum).ToList());

    public Task<bool> HasActiveDeliveryRecordAsync(long orchId, CancellationToken ct = default)
        => Task.FromResult(_hasActiveDelivery);

    public Task UpdatePickListPickedQtyAsync(long id, decimal qty, string status, CancellationToken ct = default)
    {
        PlrRetirements.Add((id, qty, status));
        var plr = _plrs.FirstOrDefault(p => p.Id == id);
        if (plr != null) plr.Status = status;
        return Task.CompletedTask;
    }

    public Task<int> UpdateSoLineFragmentEditAsync(
        long fragId, string newWhs, string newItem, decimal newQty, string changedBy, CancellationToken ct = default)
    {
        if (SimulateFragmentSyncFailure) throw new InvalidOperationException("Simulated fragment sync failure.");
        FragmentEdits.Add((fragId, newWhs, newItem, newQty));
        var frag = _fragments.FirstOrDefault(f => f.Id == fragId);
        if (frag != null) { frag.WhsCode = newWhs; frag.ItemCode = newItem; frag.SoLineQty = newQty; }
        return Task.FromResult(1);
    }

    public Task<string?> GetRdr1WhsCodeAsync(int docEntry, int lineNum, CancellationToken ct = default)
        => Task.FromResult(_rdr1Whs.TryGetValue((docEntry, lineNum), out var v) ? v : null);

    // ── Replan operation CRUD ─────────────────────────────────────────────────

    public Task<Guid> CreateReplanOperationAsync(ZfReplanOperationRecord op, CancellationToken ct = default)
    {
        op.OperationId = Guid.NewGuid();
        _replanOps[op.OperationId] = op;
        return Task.FromResult(op.OperationId);
    }

    public Task AdvanceReplanStepAsync(Guid operationId, string step, CancellationToken ct = default)
    {
        if (_replanOps.TryGetValue(operationId, out var op) && op.CurrentStep != ReplanStep.Completed)
        {
            op.CurrentStep  = step;
            op.LastGoodStep = step;
        }
        return Task.CompletedTask;
    }

    public Task SetReplanRecoveryAsync(Guid operationId, string error, CancellationToken ct = default)
    {
        if (_replanOps.TryGetValue(operationId, out var op))
        {
            op.CurrentStep = ReplanStep.RecoveryRequired;
            op.LastError   = error;
        }
        return Task.CompletedTask;
    }

    public Task SetReplanNewAbsEntriesAsync(Guid operationId, string json, CancellationToken ct = default)
    {
        if (_replanOps.TryGetValue(operationId, out var op))
            op.NewAbsEntriesJson = json;
        return Task.CompletedTask;
    }

    public Task CompleteReplanAsync(Guid operationId, CancellationToken ct = default)
    {
        if (_replanOps.TryGetValue(operationId, out var op))
        {
            op.CurrentStep    = ReplanStep.Completed;
            op.LastGoodStep   = ReplanStep.Completed;
            op.CompletedAtUtc = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<ZfReplanOperationRecord?> FindActiveReplanOperationAsync(
        int soDocEntry, CancellationToken ct = default)
    {
        var active = _replanOps.Values
            .Where(o => o.SoDocEntry == soDocEntry
                     && o.CurrentStep != ReplanStep.Completed
                     && o.CurrentStep != ReplanStep.FailedBeforeMutation)
            .OrderByDescending(o => o.StartedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(active);
    }

    public Task<ZfReplanOperationRecord?> FindReplanByOperationIdAsync(
        Guid operationId, CancellationToken ct = default)
        => Task.FromResult(_replanOps.TryGetValue(operationId, out var op) ? op : (ZfReplanOperationRecord?)null);
}

public sealed class FakeSapForOrderEdit
{
    public bool UpdateOrderResult { get; set; } = true;
    public List<int> ClosedAbsEntries { get; } = [];
    public (bool Success, string? Error) CloseResult { get; set; } = (true, null);
    public int UpdateOrderCallCount { get; private set; }
    /// <summary>AbsEntries that should fail to cancel (simulates partial cancel for RF02).</summary>
    public HashSet<int> FailCancelOnAbsEntries { get; } = [];
    /// <summary>RDR1 lines returned by GetOpenSoLines (keyed by DocEntry).</summary>
    public List<OpenSoLineDto> OpenSoLinesResult { get; set; } = [];

    public bool UpdateOrder(UpdateOrderDto dto) { UpdateOrderCallCount++; return UpdateOrderResult; }

    public (bool Success, string? Error) CloseZoneFulfillmentPickList(int absEntry)
    {
        if (FailCancelOnAbsEntries.Contains(absEntry))
            return (false, $"Simulated cancel failure for AbsEntry={absEntry}");
        if (CloseResult.Success) ClosedAbsEntries.Add(absEntry);
        return CloseResult;
    }

    public List<OpenSoLineDto> GetOpenSoLines(int docEntry)
        => OpenSoLinesResult.Where(l => l.DocEntry == docEntry).ToList();
}

public sealed class FakePickListServiceForOrderEdit
{
    public int CreateCallCount { get; private set; }
    public List<int> CreatedAbsEntries { get; set; } = [99001];
    /// <summary>When true, CreatePickListsAsync throws to simulate Rule E failure (RF05).</summary>
    public bool ThrowOnCreate { get; set; }

    public Task<PickListCreateResult> CreatePickListsAsync(Guid requestId, CancellationToken ct = default)
    {
        CreateCallCount++;
        if (ThrowOnCreate) throw new InvalidOperationException("Simulated OPKL creation failure.");
        return Task.FromResult(new PickListCreateResult
        {
            RequestId = requestId,
            PickLists = CreatedAbsEntries.Select(a => new PickListInfo
            {
                PickListAbsEntry = a,
                WhsCode          = "002",
                SoDocEntry       = 0,
                SoLineNum        = 0,
                ReleasedQty      = 5m,
                Status           = PickListStatus.Created,
                IsNew            = true
            }).ToList(),
            IsNew = true
        });
    }
}

public sealed class FakeRefreshForOrderEdit
    : SapReplitAPI.Services.PickList.IPickListEventRefreshService
{
    public Task<(bool ok, string? error)> RefreshAsync(int absEntry, CancellationToken ct = default)
        => Task.FromResult((true, (string?)null));
}

/// <summary>
/// Testable coordinator — mirrors ZoneFulfillmentOrderEditCoordinator using fake dependencies.
/// Uses durable ZfReplanOperation state (via FakeZfOrderEditRepo) for AMBER path.
/// </summary>
public sealed class TestableZfOrderEditCoordinator
{
    private readonly FakeZfOrderEditRepo             _repo;
    private readonly FakeSapForOrderEdit             _sap;
    private readonly FakeZfWhsChangeSapReader        _sapReader;
    private readonly FakePickListServiceForOrderEdit _plService;

    public TestableZfOrderEditCoordinator(
        FakeZfOrderEditRepo             repo,
        FakeSapForOrderEdit             sap,
        FakeZfWhsChangeSapReader        sapReader,
        FakePickListServiceForOrderEdit plService)
    {
        _repo      = repo;
        _sap       = sap;
        _sapReader = sapReader;
        _plService = plService;
    }

    public async Task<OrderEditResult> ExecuteEditAsync(
        int soDocEntry, UpdateOrderDto dto, string changedBy, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null) return OrderEditResult.NotZf();

        if (orch.State != OrchestrationState.Accepted)
            return OrderEditResult.Blocked("ZF_INVALID_STATE", $"State={orch.State}");

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0)
            return OrderEditResult.Blocked("ZF_NO_FRAGMENTS", "No fragments.");

        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
            return OrderEditResult.Blocked("ZF_ACTIVE_DELIVERY", "Active delivery.");

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

        if (changedFragments.Count == 0) return OrderEditResult.NotZf();

        // Section 1: classify ALL lines before any mutation
        var amberLines = new List<(SoLineFragmentRecord frag, PickListRecordModel activePlr)>();
        foreach (var (frag, _) in changedFragments)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, frag.SoLineNum, ct);

            if (plrs.Any(p => p.PickedQty > 0))
                return OrderEditResult.Blocked("ZF_LINE_PICKED",
                    $"Line {frag.SoLineNum} has PLR.PickedQty > 0.", frag.SoLineNum);

            var activePlr = plrs.FirstOrDefault(p => p.PickListAbsEntry > 0 && p.Status != PickListStatus.Closed);
            if (activePlr == null) continue;

            decimal pkl1q = _sapReader.ReadPickListLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum)?.PickQtty ?? 0m;
            decimal pkl2q = _sapReader.GetPkl2PickQttyForLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum);

            if (pkl1q > 0 || pkl2q > 0)
                return OrderEditResult.Blocked("ZF_PHYSICAL_PICK_STARTED",
                    $"Line {frag.SoLineNum} PKL1={pkl1q} PKL2={pkl2q}.", frag.SoLineNum);

            amberLines.Add((frag, activePlr));
        }

        bool isAmber = amberLines.Count > 0;

        if (!isAmber)
        {
            // GREEN path — no durable record
            bool sapOk = _sap.UpdateOrder(dto);
            if (!sapOk) return OrderEditResult.Blocked("ZF_SAP_UPDATE_FAILED", "UpdateOrder returned false.");
            foreach (var (frag, dtoLine) in changedFragments)
            {
                if (dtoLine == null) continue;
                string  nWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag.WhsCode  : dtoLine.WhsCode;
                string  nItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag.ItemCode : dtoLine.ItemCode;
                decimal nQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag.SoLineQty;
                await _repo.UpdateSoLineFragmentEditAsync(frag.Id, nWhs, nItem, nQty, changedBy, ct);
            }
            return OrderEditResult.Success(wasAmber: false);
        }

        // ── AMBER path ────────────────────────────────────────────────────────

        // Section 6: concurrency guard
        var existing = await _repo.FindActiveReplanOperationAsync(soDocEntry, ct);
        if (existing != null)
            return OrderEditResult.Blocked("ZF_REPLAN_IN_PROGRESS",
                $"Replan {existing.OperationId} already active (step={existing.CurrentStep}).");

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

        // Rules A + B: cancel OPKLs
        bool anyOPKLCancelled = false;
        foreach (var (frag, activePlr) in amberLines)
        {
            var (closed, closeErr) = _sap.CloseZoneFulfillmentPickList(activePlr.PickListAbsEntry);
            if (!closed)
            {
                if (!anyOPKLCancelled)
                {
                    await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FailedBeforeMutation, ct);
                    return OrderEditResult.Blocked("ZF_OPKL_CLOSE_FAILED",
                        $"AbsEntry={activePlr.PickListAbsEntry}: {closeErr}", frag.SoLineNum);
                }
                await _repo.SetReplanRecoveryAsync(operationId,
                    $"Partial cancel: AbsEntry={activePlr.PickListAbsEntry} failed after earlier OPKL(s) retired: {closeErr}", ct);
                return OrderEditResult.RecoveryRequired("ZF_PARTIAL_CANCEL_FAILED",
                    $"Partial cancel at AbsEntry={activePlr.PickListAbsEntry}.", operationId);
            }
            await _repo.UpdatePickListPickedQtyAsync(activePlr.Id, 0m, PickListStatus.Closed, ct);
            anyOPKLCancelled = true;
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.OldPickListsRetired, ct);

        // Rule C: UpdateOrder
        bool sapOk2 = _sap.UpdateOrder(dto);
        if (!sapOk2)
        {
            await _repo.SetReplanRecoveryAsync(operationId, "UpdateOrder returned false.", ct);
            return OrderEditResult.RecoveryRequired("ZF_SAP_UPDATE_FAILED",
                $"SO update failed for DocEntry={soDocEntry}.", operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.SalesOrderUpdated, ct);

        // Rule D: fragment sync
        bool syncFailed  = false;
        string? syncError = null;
        foreach (var (frag, dtoLine) in changedFragments)
        {
            if (dtoLine == null) continue;
            string  nWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag.WhsCode  : dtoLine.WhsCode;
            string  nItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag.ItemCode : dtoLine.ItemCode;
            decimal nQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag.SoLineQty;
            try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, nWhs, nItem, nQty, changedBy, ct); }
            catch (Exception ex) { syncFailed = true; syncError = ex.Message; break; }
        }
        if (syncFailed)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"Fragment sync failed: {syncError}", ct);
            return OrderEditResult.RecoveryRequired("ZF_FRAGMENT_SYNC_FAILED",
                $"SO updated but fragment sync failed: {syncError}", operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FragmentsSynchronized, ct);

        // Rule E: create replacement OPKLs
        List<int> newAbsEntries;
        try
        {
            var plResult = await _plService.CreatePickListsAsync(orch.RequestId, ct);
            newAbsEntries = plResult.PickLists.Select(p => p.PickListAbsEntry).ToList();
        }
        catch (Exception ex)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"OPKL creation failed: {ex.Message}", ct);
            return OrderEditResult.RecoveryRequired("ZF_REPLAN_OPKL_CREATE_FAILED",
                $"SO updated but OPKL creation failed: {ex.Message}", operationId);
        }

        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ReplacementPickListsCreated, ct);
        await _repo.SetReplanNewAbsEntriesAsync(operationId,
            System.Text.Json.JsonSerializer.Serialize(newAbsEntries), ct);
        await _repo.CompleteReplanAsync(operationId, ct);

        return OrderEditResult.Success(wasAmber: true, plAbsEntries: newAbsEntries, operationId: operationId);
    }

    public async Task<OrderEditResult> ResumeReplanAsync(Guid operationId, CancellationToken ct = default)
    {
        var op = await _repo.FindReplanByOperationIdAsync(operationId, ct);
        if (op == null)
            return OrderEditResult.Blocked("ZF_REPLAN_NOT_FOUND", $"Replan {operationId} not found.");

        if (op.CurrentStep == ReplanStep.Completed)
        {
            var existing = op.NewAbsEntriesJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.NewAbsEntriesJson) ?? []
                : (List<int>)[];
            return OrderEditResult.Success(wasAmber: true, plAbsEntries: existing, operationId: operationId);
        }

        if (op.CurrentStep == ReplanStep.FailedBeforeMutation)
            return OrderEditResult.Blocked("ZF_REPLAN_FAILED_BEFORE_MUTATION",
                $"Replan {operationId} failed before any mutation. Submit a new edit request.");

        int resumeOrd = ReplanStep.Ordinal(op.LastGoodStep);

        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(op.SoDocEntry, ct);
        if (orch == null)
            return OrderEditResult.Blocked("ZF_NO_ORCHESTRATION", $"No orchestration for SoDocEntry={op.SoDocEntry}.");

        var dto       = System.Text.Json.JsonSerializer.Deserialize<UpdateOrderDto>(op.DtoJson)!;
        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(op.SoDocEntry, ct);
        var oldAbs    = System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.OldAbsEntriesJson) ?? [];

        // Step 1: retire OPKLs (idempotent — skip already-closed PLRs)
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.OldPickListsRetired))
        {
            foreach (var absEntry in oldAbs)
            {
                bool alreadyClosed = false;
                foreach (var frag2 in fragments)
                {
                    var plrs2 = await _repo.GetPickListRecordsBySoLineAsync(op.SoDocEntry, frag2.SoLineNum, ct);
                    var plr2  = plrs2.FirstOrDefault(p => p.PickListAbsEntry == absEntry);
                    if (plr2?.Status == PickListStatus.Closed) { alreadyClosed = true; break; }
                }
                if (alreadyClosed) continue;

                var (closed, closeErr) = _sap.CloseZoneFulfillmentPickList(absEntry);
                if (!closed)
                {
                    await _repo.SetReplanRecoveryAsync(operationId, $"Resume cancel failed AbsEntry={absEntry}: {closeErr}", ct);
                    return OrderEditResult.RecoveryRequired("ZF_OPKL_CLOSE_FAILED",
                        $"Resume: cancel AbsEntry={absEntry} failed.", operationId);
                }
                foreach (var frag2 in fragments)
                {
                    var plrs2 = await _repo.GetPickListRecordsBySoLineAsync(op.SoDocEntry, frag2.SoLineNum, ct);
                    var plr2  = plrs2.FirstOrDefault(p => p.PickListAbsEntry == absEntry && p.Status != PickListStatus.Closed);
                    if (plr2 != null) await _repo.UpdatePickListPickedQtyAsync(plr2.Id, 0m, PickListStatus.Closed, ct);
                }
            }
            await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.OldPickListsRetired, ct);
            resumeOrd = ReplanStep.Ordinal(ReplanStep.OldPickListsRetired);
        }

        // Step 2: UpdateOrder
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.SalesOrderUpdated))
        {
            bool sapOk = _sap.UpdateOrder(dto);
            if (!sapOk)
            {
                await _repo.SetReplanRecoveryAsync(operationId, "Resume: UpdateOrder returned false.", ct);
                return OrderEditResult.RecoveryRequired("ZF_SAP_UPDATE_FAILED",
                    $"Resume: SO update failed.", operationId);
            }
            await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.SalesOrderUpdated, ct);
            resumeOrd = ReplanStep.Ordinal(ReplanStep.SalesOrderUpdated);
        }

        // Step 3: Fragment sync
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.FragmentsSynchronized))
        {
            var dtoMap = dto.UpdatedLines.ToDictionary(l => l.LineNum);
            foreach (var frag2 in fragments)
            {
                if (!dtoMap.TryGetValue(frag2.SoLineNum, out var dtoLine)) continue;
                string  nWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag2.WhsCode  : dtoLine.WhsCode;
                string  nItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag2.ItemCode : dtoLine.ItemCode;
                decimal nQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag2.SoLineQty;
                try { await _repo.UpdateSoLineFragmentEditAsync(frag2.Id, nWhs, nItem, nQty, op.ChangedBy, ct); }
                catch (Exception ex)
                {
                    await _repo.SetReplanRecoveryAsync(operationId, $"Resume fragment sync failed: {ex.Message}", ct);
                    return OrderEditResult.RecoveryRequired("ZF_FRAGMENT_SYNC_FAILED",
                        $"Resume: fragment sync failed.", operationId);
                }
            }
            await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FragmentsSynchronized, ct);
            resumeOrd = ReplanStep.Ordinal(ReplanStep.FragmentsSynchronized);
        }

        // Step 4: Create replacement OPKLs
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
                await _repo.SetReplanRecoveryAsync(operationId, $"Resume OPKL creation failed: {ex.Message}", ct);
                return OrderEditResult.RecoveryRequired("ZF_REPLAN_OPKL_CREATE_FAILED",
                    $"Resume: OPKL creation failed.", operationId);
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
        return OrderEditResult.Success(wasAmber: true, plAbsEntries: newAbsEntries, operationId: operationId);
    }
}

// ─── Shared test data ─────────────────────────────────────────────────────────

public static class OeTestData
{
    public const int    DocEntry  = 28879;
    public const int    LineNum0  = 0;
    public const int    LineNum1  = 1;
    public const long   OrchId    = 50001L;
    public const long   FragId0   = 50010L;
    public const long   FragId1   = 50011L;
    public const long   PlrId0    = 60001L;
    public const long   PlrId1    = 60002L;
    public const int    AbsEntry0 = 77;
    public const int    AbsEntry1 = 88;
    public static readonly Guid RequestId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static FulfillmentOrchestrationRecord MakeOrch(string state = OrchestrationState.Accepted)
        => new()
        {
            Id = OrchId, RequestId = RequestId, State = state,
            SoDocEntry = DocEntry, SoDocNum = DocEntry,
            DeliveryLocation = "TestLoc", AllocationVersion = 1,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };

    public static SoLineFragmentRecord MakeFrag(int lineNum = LineNum0, string whs = "003",
        string item = "ITEM-A", decimal qty = 5m, long fragId = FragId0)
        => new()
        {
            Id = fragId, OrchestrationId = OrchId, SoDocEntry = DocEntry,
            SoLineNum = lineNum, ItemCode = item, WhsCode = whs,
            SoLineQty = qty, AllocatedQty = qty
        };

    public static PickListRecordModel MakePlr(int lineNum = LineNum0,
        decimal pickedQty = 0m, string whs = "003", string status = PickListStatus.Released,
        int absEntry = AbsEntry0, long plrId = PlrId0)
        => new()
        {
            Id = plrId, OrchestrationId = OrchId, SoLineFragmentId = FragId0 + lineNum,
            SoDocEntry = DocEntry, SoLineNum = lineNum, WhsCode = whs,
            PickListAbsEntry = absEntry, ReleasedQty = 5m, PickedQty = pickedQty,
            Status = status, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };

    public static UpdateOrderDto MakeDto(int lineNum = LineNum0, string whs = "003",
        string item = "ITEM-A", int qty = 5)
        => new()
        {
            DocEntry     = DocEntry,
            UpdatedLines = [new OrderLineDto
            {
                LineNum  = lineNum, ItemCode = item,
                WhsCode  = whs, Quantity = qty, Price = 100m
            }]
        };

    public static (FakeZfOrderEditRepo repo, FakeSapForOrderEdit sap,
                   FakeZfWhsChangeSapReader sapReader, FakePickListServiceForOrderEdit plService,
                   TestableZfOrderEditCoordinator coord) MakeHarness()
    {
        var repo      = new FakeZfOrderEditRepo();
        var sap       = new FakeSapForOrderEdit();
        var sapReader = new FakeZfWhsChangeSapReader();
        var plService = new FakePickListServiceForOrderEdit();
        var coord     = new TestableZfOrderEditCoordinator(repo, sap, sapReader, plService);
        return (repo, sap, sapReader, plService, coord);
    }
}

// ─── OE01–OE17 Tests ─────────────────────────────────────────────────────────

/// <summary>
/// OE01-OE17: ZF Released-Order Edit state machine tests.
/// All scenarios are pure in-memory (no SQL, no COM, no SAP mutations).
/// </summary>
public sealed class ZfOrderEditTests
{
    // ── OE01: No OPKL → WHS change allowed (GREEN) ───────────────────────────

    [Fact]
    public async Task OE01_NoOpkl_WhsChange_Allowed_Green()
    {
        var (repo, sap, _, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        // No PLR — GREEN

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsSuccess);
        Assert.False(result.WasAmber);
        Assert.Equal(1, sap.UpdateOrderCallCount);
        Assert.Empty(sap.ClosedAbsEntries);
    }

    // ── OE02: Released OPKL + PickQtty=0 → WHS change allowed (AMBER replan) ──

    [Fact]
    public async Task OE02_ReleasedOpkl_ZeroPick_WhsChange_AmberReplan()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);
        plService.CreatedAbsEntries = [99002];

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        Assert.Contains(OeTestData.AbsEntry0, sap.ClosedAbsEntries);
        Assert.Equal(1, sap.UpdateOrderCallCount);
        Assert.Equal(1, plService.CreateCallCount);
        Assert.Contains(99002, result.NewPickListAbsEntries);
        // PLR should be retired
        Assert.Single(repo.PlrRetirements);
        Assert.Equal(PickListStatus.Closed, repo.PlrRetirements[0].Status);
    }

    // ── OE03: Released OPKL + PickQtty=0 → Item change allowed (AMBER replan) ─

    [Fact]
    public async Task OE03_ReleasedOpkl_ZeroPick_ItemChange_AmberReplan()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(item: "ITEM-A", whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        var dto = OeTestData.MakeDto(item: "ITEM-B", whs: "003"); // same WHS, different item

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        Assert.Contains(OeTestData.AbsEntry0, sap.ClosedAbsEntries);
        // Fragment should be updated with new item
        var fragEdit = Assert.Single(repo.FragmentEdits);
        Assert.Equal("ITEM-B", fragEdit.Item);
    }

    // ── OE04: Released OPKL + PickQtty=0 → Qty decrease allowed (AMBER replan) ─

    [Fact]
    public async Task OE04_ReleasedOpkl_ZeroPick_QtyDecrease_AmberReplan()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(qty: 5m, whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        var dto = OeTestData.MakeDto(whs: "003", qty: 3); // decrease 5→3

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        var fragEdit = Assert.Single(repo.FragmentEdits);
        Assert.Equal(3m, fragEdit.Qty);
    }

    // ── OE05: Released OPKL + PickQtty=0 → Qty increase allowed (AMBER replan) ─

    [Fact]
    public async Task OE05_ReleasedOpkl_ZeroPick_QtyIncrease_AmberReplan()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(qty: 3m, whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 3m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        var dto = OeTestData.MakeDto(whs: "003", qty: 5); // increase 3→5

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        var fragEdit = Assert.Single(repo.FragmentEdits);
        Assert.Equal(5m, fragEdit.Qty);
    }

    // ── OE06: Released OPKL + PickQtty=0 → line removal retires OPKL ────────

    [Fact]
    public async Task OE06_ReleasedOpkl_ZeroPick_LineRemoval_RetiresOpkl()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum0)); // LineNum0 has active OPKL
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum1,  // LineNum1 kept
            fragId: OeTestData.FragId1, whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(lineNum: OeTestData.LineNum0, pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        // DTO only contains LineNum1 — LineNum0 is being removed
        var dto = new UpdateOrderDto
        {
            DocEntry     = OeTestData.DocEntry,
            UpdatedLines = [new OrderLineDto
            {
                LineNum  = OeTestData.LineNum1, ItemCode = "ITEM-1",
                WhsCode  = "003", Quantity = 5, Price = 100m
            }]
        };

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        Assert.Contains(OeTestData.AbsEntry0, sap.ClosedAbsEntries);
        Assert.Single(repo.PlrRetirements);
    }

    // ── OE07: No OPKL, only line addition → no frag change detected → NotZf ──

    [Fact]
    public async Task OE07_NoOpkl_LineAddition_NoFragChange_ReturnNotZf()
    {
        var (repo, sap, _, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum0, whs: "003", qty: 5m));
        // No PLR for existing line

        // DTO contains LineNum0 (unchanged) and a brand-new LineNum=99 (no fragment)
        var dto = new UpdateOrderDto
        {
            DocEntry = OeTestData.DocEntry,
            UpdatedLines =
            [
                new OrderLineDto { LineNum = OeTestData.LineNum0, ItemCode = "ITEM-A", WhsCode = "003", Quantity = 5, Price = 100m },
                new OrderLineDto { LineNum = 99, ItemCode = "ITEM-NEW", WhsCode = "003", Quantity = 2, Price = 50m }
            ]
        };

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        // Existing line is unchanged, new line has no fragment → nothing changes in ZF
        Assert.True(result.IsNotZf);
        Assert.Equal(0, sap.UpdateOrderCallCount);
    }

    // ── OE08: PKL1.PickQtty > 0 → WHS change blocked ────────────────────────

    [Fact]
    public async Task OE08_Pkl1Picked_WhsChange_Blocked()
    {
        var (repo, _, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(pickedQty: 0)); // PLR cache says 0 — live SAP wins
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 3m, "Y")); // 3 physically picked
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_PHYSICAL_PICK_STARTED", result.BlockCode);
    }

    // ── OE09: PKL1.PickQtty > 0 → Item change blocked ────────────────────────

    [Fact]
    public async Task OE09_Pkl1Picked_ItemChange_Blocked()
    {
        var (repo, _, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(item: "ITEM-A", whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 2m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(item: "ITEM-B", whs: "003"), "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_PHYSICAL_PICK_STARTED", result.BlockCode);
    }

    // ── OE10: PLR.PickedQty > 0 → blocked by PLR cache ───────────────────────

    [Fact]
    public async Task OE10_PlrPickedQtyGtZero_QtyChange_Blocked()
    {
        var (repo, _, _, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(qty: 5m));
        repo.AddPlr(OeTestData.MakePlr(pickedQty: 4m, status: PickListStatus.Picked)); // already partially picked

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(qty: 3), "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_LINE_PICKED", result.BlockCode);
    }

    // ── OE11: PKL2.PickQtty > 0 → blocked even if PLR cache says zero ────────

    [Fact]
    public async Task OE11_Pkl2Picked_CacheSaysZero_Blocked()
    {
        var (repo, _, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(pickedQty: 0)); // PLR cache: 0 picks
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y")); // PKL1: 0 picks
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 1.5m); // PKL2: 1.5 physically picked in bin

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_PHYSICAL_PICK_STARTED", result.BlockCode);
    }

    // ── OE12: Active delivery → edit blocked ─────────────────────────────────

    [Fact]
    public async Task OE12_ActiveDelivery_EditBlocked()
    {
        var (repo, _, _, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag());
        repo.SetActiveDelivery(true);

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_ACTIVE_DELIVERY", result.BlockCode);
    }

    // ── OE13: Delivery gate — ZF_ORDER_STATE_MISMATCH when RDR1 WHS differs ──

    [Fact]
    public async Task OE13_DeliveryGate_Rdr1WhsMismatch_BlocksDelivery()
    {
        var repo      = new FakeZfWhsChangeRepo();
        var sapReader = new FakeZfWhsChangeSapReader();
        var svc       = new TestableZoneFulfillmentWarehouseChangeService(repo, sapReader);

        var orch = ZfWhsChangeTestData.MakeOrch();
        repo.SetOrchestration(orch);
        repo.AddFragment(ZfWhsChangeTestData.MakeFrag(whs: "003")); // fragment says WHS=003
        repo.SetRdr1Whs(ZfWhsChangeTestData.DocEntry, ZfWhsChangeTestData.LineNum0, "002"); // RDR1 says WHS=002 (SAP-GUI edit)

        var gate = await svc.ValidateDeliveryWhsConsistencyAsync(orch.RequestId);

        Assert.False(gate.Pass);
        Assert.Contains("MISMATCH", gate.FailCode!, StringComparison.OrdinalIgnoreCase);
    }

    // ── OE14: Delivery gate — passes when RDR1 and fragment agree ────────────

    [Fact]
    public async Task OE14_DeliveryGate_Rdr1Matches_Passes()
    {
        var repo      = new FakeZfWhsChangeRepo();
        var sapReader = new FakeZfWhsChangeSapReader();
        var svc       = new TestableZoneFulfillmentWarehouseChangeService(repo, sapReader);

        var orch = ZfWhsChangeTestData.MakeOrch();
        repo.SetOrchestration(orch);
        repo.AddFragment(ZfWhsChangeTestData.MakeFrag(whs: "003"));
        repo.SetRdr1Whs(ZfWhsChangeTestData.DocEntry, ZfWhsChangeTestData.LineNum0, "003"); // agrees

        var gate = await svc.ValidateDeliveryWhsConsistencyAsync(orch.RequestId);

        Assert.True(gate.Pass);
    }

    // ── OE15: Multi-line — only affected lines changed, others untouched ─────

    [Fact]
    public async Task OE15_MultiLine_OnlyAffectedLineReplanned()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());

        // LineNum0: has released OPKL with zero picks → AMBER
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum0, whs: "003", fragId: OeTestData.FragId0));
        repo.AddPlr(OeTestData.MakePlr(lineNum: OeTestData.LineNum0, pickedQty: 0, absEntry: OeTestData.AbsEntry0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        // LineNum1: no OPKL → GREEN (should be unaffected)
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum1, whs: "002", item: "ITEM-1", fragId: OeTestData.FragId1));

        // Only LineNum0 WHS changes
        var dto = new UpdateOrderDto
        {
            DocEntry = OeTestData.DocEntry,
            UpdatedLines =
            [
                new OrderLineDto { LineNum = OeTestData.LineNum0, ItemCode = "ITEM-A", WhsCode = "002", Quantity = 5, Price = 100m },
                new OrderLineDto { LineNum = OeTestData.LineNum1, ItemCode = "ITEM-1", WhsCode = "002", Quantity = 5, Price = 100m } // unchanged
            ]
        };

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        // Only LineNum0's OPKL was closed
        Assert.Equal([OeTestData.AbsEntry0], sap.ClosedAbsEntries);
        // Only LineNum0's fragment was edited
        Assert.Single(repo.FragmentEdits);
        Assert.Equal(OeTestData.FragId0, repo.FragmentEdits[0].Id);
    }

    // ── OE16: Idempotency — after AMBER replan PLR is Closed → next call is GREEN ─

    [Fact]
    public async Task OE16_AfterAmberReplan_PlrClosed_NextCallIsGreen()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0, status: PickListStatus.Closed)); // PLR already Closed
        // No PKL1/PKL2 registered — simulates state after first AMBER replan

        // Fragment was already updated by first replan (simulated: frag already at target whs=003, same as dto)
        // Now the user edits again — same whs, same item, same qty → no change detected
        var dto = OeTestData.MakeDto(whs: "003");

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        // No fulfillment-affecting change → NotZf (no changes detected)
        Assert.True(result.IsNotZf);
        Assert.Empty(sap.ClosedAbsEntries);
        Assert.Equal(0, sap.UpdateOrderCallCount);
        Assert.Equal(0, plService.CreateCallCount);
    }

    // ── OE17: Delivery defense — PLR.WhsCode != Fragment.WhsCode → blocked ───

    [Fact]
    public async Task OE17_DeliveryDefense_PlrWhsMismatch_BlocksDelivery()
    {
        var repo      = new FakeZfWhsChangeRepo();
        var sapReader = new FakeZfWhsChangeSapReader();
        var svc       = new TestableZoneFulfillmentWarehouseChangeService(repo, sapReader);

        var orch = ZfWhsChangeTestData.MakeOrch();
        repo.SetOrchestration(orch);
        repo.AddFragment(ZfWhsChangeTestData.MakeFrag(whs: "002")); // fragment says 002
        repo.AddPlr(ZfWhsChangeTestData.MakePlr(whs: "003")); // PLR still says 003 (stale after WHS change)
        repo.SetRdr1Whs(ZfWhsChangeTestData.DocEntry, ZfWhsChangeTestData.LineNum0, "002"); // RDR1 agrees with fragment

        var gate = await svc.ValidateDeliveryWhsConsistencyAsync(orch.RequestId);

        Assert.False(gate.Pass);
        Assert.Equal("WAREHOUSE_BIN_MISMATCH", gate.FailCode);
    }
}

// ─── RF01-RF10: Partial-failure / recovery tests ──────────────────────────────

/// <summary>
/// RF01-RF10: AMBER replan partial-failure and recovery tests.
/// Verifies durable operation state and failure rules A-E from the spec.
/// </summary>
public sealed class ZfOrderEditRecoveryTests
{
    // ── RF01: Second OPKL preflight fails → zero SAP mutations ───────────────

    [Fact]
    public async Task RF01_SecondLinePrefailFails_ZeroMutations()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());

        // LineNum0: AMBER — zero picks
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum0, whs: "003", fragId: OeTestData.FragId0));
        repo.AddPlr(OeTestData.MakePlr(lineNum: OeTestData.LineNum0, pickedQty: 0, absEntry: OeTestData.AbsEntry0, plrId: OeTestData.PlrId0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        // LineNum1: PLR.PickedQty=2 → G5 preflight fails
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum1, whs: "003", fragId: OeTestData.FragId1));
        repo.AddPlr(OeTestData.MakePlr(lineNum: OeTestData.LineNum1, pickedQty: 2m, absEntry: OeTestData.AbsEntry1, plrId: OeTestData.PlrId1));

        var dto = new UpdateOrderDto
        {
            DocEntry     = OeTestData.DocEntry,
            UpdatedLines =
            [
                new OrderLineDto { LineNum = OeTestData.LineNum0, ItemCode = "ITEM-A", WhsCode = "002", Quantity = 5, Price = 100m },
                new OrderLineDto { LineNum = OeTestData.LineNum1, ItemCode = "ITEM-A", WhsCode = "002", Quantity = 5, Price = 100m }
            ]
        };

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_LINE_PICKED", result.BlockCode);
        Assert.Empty(sap.ClosedAbsEntries);      // zero SAP mutations
        Assert.Equal(0, sap.UpdateOrderCallCount);
    }

    // ── RF02: First OPKL cancel succeeds, second fails → RecoveryRequired ────

    [Fact]
    public async Task RF02_PartialCancel_RecoveryRequired()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());

        // LineNum0: AMBER, AbsEntry0 — cancel will succeed
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum0, whs: "003", fragId: OeTestData.FragId0));
        repo.AddPlr(OeTestData.MakePlr(lineNum: OeTestData.LineNum0, pickedQty: 0, absEntry: OeTestData.AbsEntry0, plrId: OeTestData.PlrId0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        // LineNum1: AMBER, AbsEntry1 — cancel will FAIL
        repo.AddFragment(OeTestData.MakeFrag(lineNum: OeTestData.LineNum1, whs: "003", fragId: OeTestData.FragId1));
        repo.AddPlr(OeTestData.MakePlr(lineNum: OeTestData.LineNum1, pickedQty: 0, absEntry: OeTestData.AbsEntry1, plrId: OeTestData.PlrId1));
        sapReader.RegisterPkl1(OeTestData.AbsEntry1, OeTestData.DocEntry, OeTestData.LineNum1,
            new Pkl1LineState(OeTestData.AbsEntry1, OeTestData.DocEntry, OeTestData.LineNum1, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry1, OeTestData.DocEntry, OeTestData.LineNum1, 0m);
        sap.FailCancelOnAbsEntries.Add(OeTestData.AbsEntry1);

        var dto = new UpdateOrderDto
        {
            DocEntry     = OeTestData.DocEntry,
            UpdatedLines =
            [
                new OrderLineDto { LineNum = OeTestData.LineNum0, ItemCode = "ITEM-A", WhsCode = "002", Quantity = 5, Price = 100m },
                new OrderLineDto { LineNum = OeTestData.LineNum1, ItemCode = "ITEM-A", WhsCode = "002", Quantity = 5, Price = 100m }
            ]
        };

        var result = await coord.ExecuteEditAsync(OeTestData.DocEntry, dto, "test");

        Assert.True(result.IsRecoveryRequired);
        Assert.Equal("ZF_PARTIAL_CANCEL_FAILED", result.BlockCode);
        Assert.NotNull(result.ReplanOperationId);
        // First OPKL was closed; SO was NOT updated
        Assert.Contains(OeTestData.AbsEntry0, sap.ClosedAbsEntries);
        Assert.Equal(0, sap.UpdateOrderCallCount);
        // Replan op is in RecoveryRequired state
        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.RecoveryRequired, op!.CurrentStep);
        Assert.Null(op.LastGoodStep); // no forward-progress step completed before the partial cancel
    }

    // ── RF03: All OPKLs retired, UpdateOrder fails → RecoveryRequired ────────

    [Fact]
    public async Task RF03_UpdateOrderFails_RecoveryRequired()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);
        sap.UpdateOrderResult = false; // UpdateOrder will fail

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsRecoveryRequired);
        Assert.Equal("ZF_SAP_UPDATE_FAILED", result.BlockCode);
        Assert.NotNull(result.ReplanOperationId);
        // Old OPKL was cancelled
        Assert.Contains(OeTestData.AbsEntry0, sap.ClosedAbsEntries);
        Assert.Equal(1, sap.UpdateOrderCallCount);
        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.RecoveryRequired, op!.CurrentStep);
        Assert.Equal(ReplanStep.OldPickListsRetired, op.LastGoodStep);
    }

    // ── RF04: SO updated, fragment sync fails → delivery blocked ─────────────

    [Fact]
    public async Task RF04_FragmentSyncFails_RecoveryRequired()
    {
        var (repo, sap, sapReader, _, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);
        repo.SimulateFragmentSyncFailure = true;

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsRecoveryRequired);
        Assert.Equal("ZF_FRAGMENT_SYNC_FAILED", result.BlockCode);
        Assert.NotNull(result.ReplanOperationId);
        Assert.Equal(1, sap.UpdateOrderCallCount); // SO was updated
        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.RecoveryRequired, op!.CurrentStep);
        Assert.Equal(ReplanStep.SalesOrderUpdated, op.LastGoodStep);
    }

    // ── RF05: Replacement PL creation fails → RecoveryRequired → no delivery ─

    [Fact]
    public async Task RF05_PlCreateFails_RecoveryRequired()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);
        plService.ThrowOnCreate = true;

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsRecoveryRequired);
        Assert.Equal("ZF_REPLAN_OPKL_CREATE_FAILED", result.BlockCode);
        Assert.NotNull(result.ReplanOperationId);
        Assert.Equal(1, sap.UpdateOrderCallCount); // SO updated
        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.RecoveryRequired, op!.CurrentStep);
        Assert.Equal(ReplanStep.FragmentsSynchronized, op.LastGoodStep);
    }

    // ── RF06: Resume skips already-retired OPKLs (no duplicate cancel) ───────

    [Fact]
    public async Task RF06_Resume_NoDuplicateOpklCancel()
    {
        var (repo, sap, _, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        // PLR already closed (retired by prior run)
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0,
            status: PickListStatus.Closed, absEntry: OeTestData.AbsEntry0));

        var dtoToResume = OeTestData.MakeDto(whs: "002");
        var opId = Guid.NewGuid();
        repo.SeedReplanOp(new ZfReplanOperationRecord
        {
            OperationId       = opId,
            SoDocEntry        = OeTestData.DocEntry,
            RequestId         = OeTestData.RequestId,
            ChangedBy         = "test",
            CurrentStep       = ReplanStep.OldPickListsRetired,
            LastGoodStep      = ReplanStep.OldPickListsRetired,
            DtoJson           = System.Text.Json.JsonSerializer.Serialize(dtoToResume),
            OldAbsEntriesJson = System.Text.Json.JsonSerializer.Serialize(new[] { OeTestData.AbsEntry0 }),
            StartedAtUtc      = DateTime.UtcNow
        });

        var result = await coord.ResumeReplanAsync(opId);

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        Assert.Empty(sap.ClosedAbsEntries);        // no new OPKL cancel
        Assert.Equal(1, sap.UpdateOrderCallCount); // UpdateOrder called exactly once
        Assert.Equal(1, plService.CreateCallCount);
    }

    // ── RF07: Repeated resume is idempotent ───────────────────────────────────

    [Fact]
    public async Task RF07_RepeatedResume_Idempotent()
    {
        var (repo, sap, _, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0,
            status: PickListStatus.Closed, absEntry: OeTestData.AbsEntry0));

        var dtoToResume = OeTestData.MakeDto(whs: "002");
        var opId = Guid.NewGuid();
        repo.SeedReplanOp(new ZfReplanOperationRecord
        {
            OperationId       = opId,
            SoDocEntry        = OeTestData.DocEntry,
            RequestId         = OeTestData.RequestId,
            ChangedBy         = "test",
            CurrentStep       = ReplanStep.OldPickListsRetired,
            LastGoodStep      = ReplanStep.OldPickListsRetired,
            DtoJson           = System.Text.Json.JsonSerializer.Serialize(dtoToResume),
            OldAbsEntriesJson = System.Text.Json.JsonSerializer.Serialize(new[] { OeTestData.AbsEntry0 }),
            StartedAtUtc      = DateTime.UtcNow
        });

        var result1 = await coord.ResumeReplanAsync(opId);
        var result2 = await coord.ResumeReplanAsync(opId); // second call finds Completed

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        // Verify idempotency: each step called exactly once
        Assert.Equal(1, sap.UpdateOrderCallCount);
        Assert.Equal(1, plService.CreateCallCount);
        Assert.Equal(ReplanStep.Completed, repo.LastReplanOp!.CurrentStep);
    }

    // ── RF08: Completed replan clears recovery state (FindActive returns null) ─

    [Fact]
    public async Task RF08_CompletedReplan_NoLongerActiveBlock()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);
        plService.CreatedAbsEntries = [99010];

        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);

        // After completion, no active replan exists for this SO
        var active = await repo.FindActiveReplanOperationAsync(OeTestData.DocEntry);
        Assert.Null(active);
        Assert.Equal(ReplanStep.Completed, repo.LastReplanOp!.CurrentStep);
    }

    // ── RF09: Coordinator's own 17/U does not start a second replan ───────────

    [Fact]
    public async Task RF09_CompletedReplan_ConcurrentEditBlocked()
    {
        var (repo, sap, sapReader, plService, coord) = OeTestData.MakeHarness();
        repo.SetOrchestration(OeTestData.MakeOrch());
        repo.AddFragment(OeTestData.MakeFrag(whs: "003"));
        repo.AddPlr(OeTestData.MakePlr(whs: "003", pickedQty: 0));
        sapReader.RegisterPkl1(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0,
            new Pkl1LineState(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(OeTestData.AbsEntry0, OeTestData.DocEntry, OeTestData.LineNum0, 0m);

        // Seed an in-progress replan (simulates first execution in flight)
        var inProgressOpId = Guid.NewGuid();
        repo.SeedReplanOp(new ZfReplanOperationRecord
        {
            OperationId       = inProgressOpId,
            SoDocEntry        = OeTestData.DocEntry,
            RequestId         = OeTestData.RequestId,
            ChangedBy         = "test",
            CurrentStep       = ReplanStep.SalesOrderUpdated,
            LastGoodStep      = ReplanStep.SalesOrderUpdated,
            DtoJson           = System.Text.Json.JsonSerializer.Serialize(OeTestData.MakeDto(whs: "002")),
            OldAbsEntriesJson = System.Text.Json.JsonSerializer.Serialize(new[] { OeTestData.AbsEntry0 }),
            StartedAtUtc      = DateTime.UtcNow
        });

        // Second call while first is still active → blocked (Section 6)
        var result = await coord.ExecuteEditAsync(
            OeTestData.DocEntry, OeTestData.MakeDto(whs: "002"), "test");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_REPLAN_IN_PROGRESS", result.BlockCode);
        // No new SAP cancel or UpdateOrder
        Assert.Empty(sap.ClosedAbsEntries);
        Assert.Equal(0, sap.UpdateOrderCallCount);
    }

    // ── RF10: Delivery blocked while replan is not Completed ─────────────────

    [Fact]
    public async Task RF10_DeliveryBlockedWhileReplanIncomplete()
    {
        var repo      = new FakeZfWhsChangeRepo();
        var sapReader = new FakeZfWhsChangeSapReader();
        var svc       = new TestableZoneFulfillmentWarehouseChangeService(repo, sapReader);

        var orch = ZfWhsChangeTestData.MakeOrch();
        repo.SetOrchestration(orch);
        repo.AddFragment(ZfWhsChangeTestData.MakeFrag(whs: "002"));
        repo.SetRdr1Whs(ZfWhsChangeTestData.DocEntry, ZfWhsChangeTestData.LineNum0, "002");

        // Active replan in RecoveryRequired state
        repo.SetActiveReplan(new ZfReplanOperationRecord
        {
            OperationId  = Guid.NewGuid(),
            SoDocEntry   = ZfWhsChangeTestData.DocEntry,
            CurrentStep  = ReplanStep.RecoveryRequired,
            LastGoodStep = ReplanStep.SalesOrderUpdated,
            StartedAtUtc = DateTime.UtcNow
        });

        var gate = await svc.ValidateDeliveryWhsConsistencyAsync(orch.RequestId);

        Assert.False(gate.Pass);
        Assert.Equal("ZF_ORDER_REPLAN_RECOVERY_REQUIRED", gate.FailCode);
    }
}
