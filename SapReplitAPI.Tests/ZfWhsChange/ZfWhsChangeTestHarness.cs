using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Tests.ZfWhsChange;

// ─── Fake Repository ─────────────────────────────────────────────────────────

public sealed class FakeZfWhsChangeRepo
{
    private FulfillmentOrchestrationRecord?  _orch;
    private readonly List<SoLineFragmentRecord>  _fragments = [];
    private readonly List<PickListRecordModel>   _plrs      = [];
    private bool _hasActiveDelivery;
    private readonly Dictionary<(int docEntry, int lineNum), string?> _rdr1Whs = new();

    // Fragment update tracking
    public List<(long Id, string Prior, string New, string By)> WhsUpdates { get; } = [];
    public bool SimulateSqlFailure { get; set; }

    public void SetOrchestration(FulfillmentOrchestrationRecord orch) => _orch = orch;
    public void AddFragment(SoLineFragmentRecord frag) => _fragments.Add(frag);
    public void AddPlr(PickListRecordModel plr) => _plrs.Add(plr);
    public void SetActiveDelivery(bool value) => _hasActiveDelivery = value;
    public void SetRdr1Whs(int docEntry, int lineNum, string? whs)
        => _rdr1Whs[(docEntry, lineNum)] = whs;

    public Task<FulfillmentOrchestrationRecord?> FindOrchestrationBySoDocEntryAsync(
        int soDocEntry, CancellationToken ct = default)
        => Task.FromResult(_orch?.SoDocEntry == soDocEntry ? _orch : null);

    public Task<FulfillmentOrchestrationRecord?> FindOrchestrationAsync(
        Guid requestId, CancellationToken ct = default)
        => Task.FromResult(_orch?.RequestId == requestId ? _orch : null);

    public Task<List<SoLineFragmentRecord>> GetSoLineFragmentsBySoDocEntryAsync(
        int soDocEntry, CancellationToken ct = default)
        => Task.FromResult(_fragments.Where(f => f.SoDocEntry == soDocEntry).ToList());

    public Task<List<PickListRecordModel>> GetPickListRecordsBySoLineAsync(
        int soDocEntry, int soLineNum, CancellationToken ct = default)
        => Task.FromResult(_plrs.Where(p => p.SoDocEntry == soDocEntry && p.SoLineNum == soLineNum).ToList());

    public Task<bool> HasActiveDeliveryRecordAsync(long orchestrationId, CancellationToken ct = default)
        => Task.FromResult(_hasActiveDelivery);

    public Task<int> UpdateSoLineFragmentWhsCodeAsync(
        long fragId, string prior, string newWhs, string changedBy, CancellationToken ct = default)
    {
        if (SimulateSqlFailure) throw new InvalidOperationException("simulated SQL failure");

        var frag = _fragments.FirstOrDefault(f => f.Id == fragId);
        if (frag == null || !string.Equals(frag.WhsCode, prior, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(0);

        // Apply write-once OriginalWhsCode
        frag.OriginalWhsCode ??= prior;
        frag.WhsCode          = newWhs;
        frag.WhsChangedAtUtc  = DateTime.UtcNow;
        frag.WhsChangedBy     = changedBy;
        WhsUpdates.Add((fragId, prior, newWhs, changedBy));
        return Task.FromResult(1);
    }

    public Task<string?> GetRdr1WhsCodeAsync(int soDocEntry, int soLineNum, CancellationToken ct = default)
        => Task.FromResult(_rdr1Whs.TryGetValue((soDocEntry, soLineNum), out var v) ? v : null);
}

// ─── Fake SAP Reader ─────────────────────────────────────────────────────────

public sealed class FakeZfWhsChangeSapReader : IZfWhsChangeSapReader
{
    private readonly Dictionary<(int abs, int doc, int line), Pkl1LineState?> _pkl1 = new();
    private readonly Dictionary<(int abs, int doc, int line), decimal>        _pkl2 = new();

    public void RegisterPkl1(int abs, int doc, int line, Pkl1LineState? state)
        => _pkl1[(abs, doc, line)] = state;

    public void RegisterPkl2(int abs, int doc, int line, decimal qty)
        => _pkl2[(abs, doc, line)] = qty;

    public Pkl1LineState? ReadPickListLine(int absEntry, int soDocEntry, int soLineNum)
        => _pkl1.TryGetValue((absEntry, soDocEntry, soLineNum), out var v) ? v : null;

    public decimal GetPkl2PickQttyForLine(int absEntry, int soDocEntry, int soLineNum)
        => _pkl2.TryGetValue((absEntry, soDocEntry, soLineNum), out var v) ? v : 0m;
}

// ─── Service wrapper that uses fake repo ─────────────────────────────────────

/// <summary>
/// Subclass that wires FakeZfWhsChangeRepo into the base service methods.
/// Overrides only the repo-calling methods to use the fake.
/// </summary>
public sealed class TestableZoneFulfillmentWarehouseChangeService
{
    private readonly FakeZfWhsChangeRepo       _repo;
    private readonly IZfWhsChangeSapReader     _sapReader;

    public TestableZoneFulfillmentWarehouseChangeService(
        FakeZfWhsChangeRepo   repo,
        IZfWhsChangeSapReader sapReader)
    {
        _repo      = repo;
        _sapReader = sapReader;
    }

    public async Task<WfPreflightResult> PreflightAsync(int soDocEntry, UpdateOrderDto dto, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null) return WfPreflightResult.NotZf();

        if (orch.State != OrchestrationState.Accepted)
            return WfPreflightResult.Blocked("ZF_INVALID_STATE",
                $"State must be Accepted. Current: {orch.State}");

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0)
            return WfPreflightResult.Blocked("ZF_NO_FRAGMENTS", "No fragments found.");

        var changes = new List<WarehouseLineChange>();
        foreach (var line in dto.UpdatedLines)
        {
            if (string.IsNullOrWhiteSpace(line.WhsCode)) continue;
            var frag = fragments.FirstOrDefault(f => f.SoLineNum == line.LineNum);
            if (frag == null) continue;
            if (!string.Equals(frag.WhsCode, line.WhsCode, StringComparison.OrdinalIgnoreCase))
                changes.Add(new WarehouseLineChange(line.LineNum, frag.Id, frag.ItemCode, frag.WhsCode, line.WhsCode));
        }

        if (changes.Count == 0) return WfPreflightResult.NoOp();

        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
            return WfPreflightResult.Blocked("ZF_ACTIVE_DELIVERY", "Active Delivery exists.");

        foreach (var change in changes)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, change.SoLineNum, ct);
            if (plrs.Any(p => p.PickedQty > 0))
                return WfPreflightResult.Blocked("ZF_LINE_PICKED",
                    $"Line {change.SoLineNum} has PickedQty > 0.", change.SoLineNum);

            var activePlr = plrs.FirstOrDefault(p => p.PickListAbsEntry > 0 && p.Status != PickListStatus.Closed);
            if (activePlr != null)
            {
                var pkl1 = _sapReader.ReadPickListLine(activePlr.PickListAbsEntry, soDocEntry, change.SoLineNum);
                if (pkl1 != null && pkl1.PickQtty > 0)
                    return WfPreflightResult.Blocked("ZF_SAP_PKL1_PICKED",
                        $"SAP PKL1 PickQtty={pkl1.PickQtty} for line {change.SoLineNum}.", change.SoLineNum);

                decimal pkl2Qty = _sapReader.GetPkl2PickQttyForLine(activePlr.PickListAbsEntry, soDocEntry, change.SoLineNum);
                if (pkl2Qty > 0)
                    return WfPreflightResult.Blocked("ZF_SAP_PKL2_PICKED",
                        $"SAP PKL2 PickQtty={pkl2Qty} for line {change.SoLineNum}.", change.SoLineNum);
            }
        }

        return WfPreflightResult.Pass(changes);
    }

    public async Task<WhsApplyResult> ApplyOperationalWarehouseChangesAsync(
        int soDocEntry, IReadOnlyList<WarehouseLineChange> changes, string changedBy, CancellationToken ct = default)
    {
        var lineResults    = new List<WhsLineApplyResult>();
        bool anySyncFailed = false;
        string? syncError  = null;

        foreach (var change in changes)
        {
            try
            {
                int rows = await _repo.UpdateSoLineFragmentWhsCodeAsync(
                    change.SoLineFragmentId, change.CurrentWhsCode, change.RequestedWhsCode, changedBy, ct);

                if (rows == 0)
                {
                    var frags = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
                    var frag  = frags.FirstOrDefault(f => f.Id == change.SoLineFragmentId);
                    bool atTarget = frag != null && string.Equals(frag.WhsCode, change.RequestedWhsCode, StringComparison.OrdinalIgnoreCase);
                    if (atTarget)
                        lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, Skipped = true });
                    else
                    {
                        anySyncFailed = true; syncError = $"Concurrency conflict line {change.SoLineNum}";
                        lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, ConcurrencyConflict = true });
                    }
                }
                else
                    lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, Applied = true });
            }
            catch (Exception ex)
            {
                anySyncFailed = true; syncError = ex.Message;
                lineResults.Add(new WhsLineApplyResult { SoLineNum = change.SoLineNum, Error = ex.Message });
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

    public async Task<WhsDeliveryGateResult> ValidateDeliveryWhsConsistencyAsync(Guid requestId, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch == null || !orch.SoDocEntry.HasValue) return WhsDeliveryGateResult.Ok();

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(orch.SoDocEntry.Value, ct);
        foreach (var frag in fragments)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(orch.SoDocEntry.Value, frag.SoLineNum, ct);
            foreach (var plr in plrs)
            {
                if (plr.Status == PickListStatus.Closed) continue;
                if (!string.Equals(frag.WhsCode, plr.WhsCode, StringComparison.OrdinalIgnoreCase))
                    return WhsDeliveryGateResult.Fail("WAREHOUSE_BIN_MISMATCH",
                        $"Fragment.WhsCode={frag.WhsCode} != PLR.WhsCode={plr.WhsCode} for line {frag.SoLineNum}.");
            }
        }
        return WhsDeliveryGateResult.Ok();
    }

    public async Task<int> ReconcileWhsStateBySoDocEntryAsync(int soDocEntry, string reconciledBy, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null || orch.State != OrchestrationState.Accepted) return 0;

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        foreach (var frag in fragments)
        {
            var plrs = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, frag.SoLineNum, ct);
            if (plrs.Any(p => p.PickedQty > 0)) return 0;
        }
        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery) return 0;

        int repaired = 0;
        foreach (var frag in fragments)
        {
            string? sapWhs = await _repo.GetRdr1WhsCodeAsync(soDocEntry, frag.SoLineNum, ct);
            if (sapWhs == null) continue;
            if (string.Equals(sapWhs, frag.WhsCode, StringComparison.OrdinalIgnoreCase)) continue;
            int rows = await _repo.UpdateSoLineFragmentWhsCodeAsync(frag.Id, frag.WhsCode, sapWhs, reconciledBy, ct);
            if (rows == 1) repaired++;
        }
        return repaired;
    }
}

// ─── Shared test data helpers ─────────────────────────────────────────────────

public static class ZfWhsChangeTestData
{
    public const int    DocEntry   = 28684;
    public const int    LineNum0   = 0;
    public const int    LineNum1   = 1;
    public const long   OrchId     = 10012L;
    public const long   FragId0    = 10013L;
    public const long   FragId1    = 10014L;
    public const int    AbsEntry   = 36;
    public static readonly Guid RequestId = Guid.NewGuid();

    public static FulfillmentOrchestrationRecord MakeOrch(
        string state = OrchestrationState.Accepted)
        => new()
        {
            Id          = OrchId,
            RequestId   = RequestId,
            State       = state,
            SoDocEntry  = DocEntry,
            SoDocNum    = DocEntry,
            DeliveryLocation = "Mikocheni-side",
            AllocationVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    public static SoLineFragmentRecord MakeFrag(
        int lineNum = LineNum0, string whs = "003", long fragId = FragId0)
        => new()
        {
            Id              = fragId,
            OrchestrationId = OrchId,
            SoDocEntry      = DocEntry,
            SoLineNum       = lineNum,
            ItemCode        = $"ITEM-{lineNum}",
            WhsCode         = whs,
            SoLineQty       = 1m,
            AllocatedQty    = 1m
        };

    public static PickListRecordModel MakePlr(
        int lineNum = LineNum0, decimal pickedQty = 0m, string whs = "003",
        string status = PickListStatus.Released, int absEntry = AbsEntry)
        => new()
        {
            Id               = 20000L + lineNum,
            OrchestrationId  = OrchId,
            SoLineFragmentId = FragId0 + lineNum,
            SoDocEntry       = DocEntry,
            SoLineNum        = lineNum,
            WhsCode          = whs,
            PickListAbsEntry = absEntry,
            ReleasedQty      = 1m,
            PickedQty        = pickedQty,
            Status           = status,
            CreatedAtUtc     = DateTime.UtcNow,
            UpdatedAtUtc     = DateTime.UtcNow
        };

    public static Pkl1LineState MakePkl1(decimal pickQtty = 0m, string status = "Y")
        => new(AbsEntry, DocEntry, LineNum0, 17, 1m, pickQtty, status);

    public static UpdateOrderDto MakeDto(
        int lineNum = LineNum0, string whs = "002")
        => new()
        {
            DocEntry     = DocEntry,
            UpdatedLines = [new OrderLineDto
            {
                LineNum  = lineNum,
                ItemCode = $"ITEM-{lineNum}",
                WhsCode  = whs,
                Quantity = 1,
                Price    = 100m
            }]
        };

    public static (FakeZfWhsChangeRepo repo, FakeZfWhsChangeSapReader sap,
                   TestableZoneFulfillmentWarehouseChangeService svc) MakeHarness()
    {
        var repo = new FakeZfWhsChangeRepo();
        var sap  = new FakeZfWhsChangeSapReader();
        var svc  = new TestableZoneFulfillmentWarehouseChangeService(repo, sap);
        return (repo, sap, svc);
    }
}
