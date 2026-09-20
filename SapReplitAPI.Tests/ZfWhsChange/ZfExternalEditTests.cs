using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.SoDelivery;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfWhsChange;

// ─── Testable coordinator for external-edit path ──────────────────────────────

/// <summary>
/// Mirrors ZoneFulfillmentOrderEditCoordinator.ReconcileExternalSapEditAsync and ResumeReplanAsync
/// (external-aware version) using fake dependencies.
/// </summary>
public sealed class TestableZfExternalEditCoordinator
{
    private readonly FakeZfOrderEditRepo             _repo;
    private readonly FakeSapForOrderEdit             _sap;
    private readonly FakeZfWhsChangeSapReader        _sapReader;
    private readonly FakePickListServiceForOrderEdit _plService;

    public TestableZfExternalEditCoordinator(
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

    public async Task<OrderEditResult> ReconcileExternalSapEditAsync(
        int soDocEntry, string eventId, string changedBy, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch == null) return OrderEditResult.NotZf();

        if (orch.State != OrchestrationState.Accepted)
            return OrderEditResult.Blocked("ZF_INVALID_STATE", $"State={orch.State}");

        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        if (fragments.Count == 0) return OrderEditResult.NotZf();

        bool hasDelivery = await _repo.HasActiveDeliveryRecordAsync(orch.Id, ct);
        if (hasDelivery)
            return OrderEditResult.Blocked("ZF_ACTIVE_DELIVERY", "Active delivery.");

        var rdr1Lines = _sap.GetOpenSoLines(soDocEntry);
        var rdr1Map   = rdr1Lines.ToDictionary(l => l.LineNum);

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

        if (divergedPairs.Count == 0) return OrderEditResult.Success(wasAmber: false);

        var amberPairs = new List<(SoLineFragmentRecord frag, OpenSoLineDto rdr1, PickListRecordModel activePlr)>();
        foreach (var (frag, rdr1) in divergedPairs)
        {
            var plrs      = await _repo.GetPickListRecordsBySoLineAsync(soDocEntry, frag.SoLineNum, ct);
            var activePlr = plrs.FirstOrDefault(p => p.PickListAbsEntry > 0 && p.Status != PickListStatus.Closed);

            if (activePlr == null) continue; // GREEN-diverge

            decimal pkl1q = _sapReader.ReadPickListLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum)?.PickQtty ?? 0m;
            decimal pkl2q = _sapReader.GetPkl2PickQttyForLine(activePlr.PickListAbsEntry, soDocEntry, frag.SoLineNum);

            if (pkl1q > 0 || pkl2q > 0)
                return OrderEditResult.Blocked("ZF_PHYSICAL_PICK_STARTED",
                    $"Line {frag.SoLineNum} PKL1={pkl1q} PKL2={pkl2q}.", frag.SoLineNum);

            amberPairs.Add((frag, rdr1, activePlr));
        }

        if (amberPairs.Count == 0)
        {
            foreach (var (frag, rdr1) in divergedPairs)
                await _repo.UpdateSoLineFragmentEditAsync(frag.Id, rdr1.WhsCode, rdr1.ItemCode, rdr1.Quantity, changedBy, ct);
            return OrderEditResult.Success(wasAmber: false);
        }

        var existing = await _repo.FindActiveReplanOperationAsync(soDocEntry, ct);
        if (existing != null)
        {
            if (existing.ChangedBy == $"sap-gui:{eventId}")
                return await ResumeReplanAsync(existing.OperationId, ct);
            return OrderEditResult.Blocked("ZF_REPLAN_IN_PROGRESS",
                $"Replan {existing.OperationId} already active (step={existing.CurrentStep}).");
        }

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
                        $"AbsEntry={activePlr.PickListAbsEntry}: {closeErr}", frag.SoLineNum);
                }
                await _repo.SetReplanRecoveryAsync(operationId,
                    $"Partial cancel: AbsEntry={activePlr.PickListAbsEntry} failed: {closeErr}", ct);
                return OrderEditResult.RecoveryRequired("ZF_PARTIAL_CANCEL_FAILED",
                    $"Partial cancel at AbsEntry={activePlr.PickListAbsEntry}.", operationId);
            }
            await _repo.UpdatePickListPickedQtyAsync(activePlr.Id, 0m, PickListStatus.Closed, ct);
            anyOPKLCancelled = true;
        }
        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.OldPickListsRetired, ct);

        // Skip UpdateOrder — SAP GUI already updated the SO
        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.ExternalSalesOrderAccepted, ct);

        bool syncFailed  = false;
        string? syncError = null;
        foreach (var (frag, rdr1, _) in amberPairs)
        {
            try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, rdr1.WhsCode, rdr1.ItemCode, rdr1.Quantity, changedBy, ct); }
            catch (Exception ex) { syncFailed = true; syncError = ex.Message; break; }
        }
        if (!syncFailed)
        {
            var amberIds = amberPairs.Select(a => a.frag.Id).ToHashSet();
            foreach (var (frag, rdr1) in divergedPairs.Where(p => !amberIds.Contains(p.frag.Id)))
            {
                try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, rdr1.WhsCode, rdr1.ItemCode, rdr1.Quantity, changedBy, ct); }
                catch { /* non-fatal */ }
            }
        }
        if (syncFailed)
        {
            await _repo.SetReplanRecoveryAsync(operationId, $"Fragment sync failed: {syncError}", ct);
            return OrderEditResult.RecoveryRequired("ZF_FRAGMENT_SYNC_FAILED",
                $"External SO accepted but fragment sync failed: {syncError}.", operationId);
        }
        await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.FragmentsSynchronized, ct);

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
                $"OPKL creation failed: {ex.Message}.", operationId);
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
            var entries = op.NewAbsEntriesJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.NewAbsEntriesJson) ?? []
                : (List<int>)[];
            return OrderEditResult.Success(wasAmber: true, plAbsEntries: entries, operationId: operationId);
        }

        if (op.CurrentStep == ReplanStep.FailedBeforeMutation)
            return OrderEditResult.Blocked("ZF_REPLAN_FAILED_BEFORE_MUTATION",
                $"Replan {operationId} failed before mutation.");

        int resumeOrd = ReplanStep.Ordinal(op.LastGoodStep);

        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(op.SoDocEntry, ct);
        if (orch == null)
            return OrderEditResult.Blocked("ZF_NO_ORCHESTRATION", $"No orchestration for SoDocEntry={op.SoDocEntry}.");

        var dto       = System.Text.Json.JsonSerializer.Deserialize<UpdateOrderDto>(op.DtoJson)!;
        var fragments = await _repo.GetSoLineFragmentsBySoDocEntryAsync(op.SoDocEntry, ct);
        var oldAbs    = System.Text.Json.JsonSerializer.Deserialize<List<int>>(op.OldAbsEntriesJson) ?? [];

        // Step 1: retire OPKLs
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.OldPickListsRetired))
        {
            foreach (var absEntry in oldAbs)
            {
                bool alreadyClosed = false;
                foreach (var frag in fragments)
                {
                    var plrs = await _repo.GetPickListRecordsBySoLineAsync(op.SoDocEntry, frag.SoLineNum, ct);
                    if (plrs.Any(p => p.PickListAbsEntry == absEntry && p.Status == PickListStatus.Closed))
                    { alreadyClosed = true; break; }
                }
                if (alreadyClosed) continue;

                var (closed, closeErr) = _sap.CloseZoneFulfillmentPickList(absEntry);
                if (!closed)
                {
                    await _repo.SetReplanRecoveryAsync(operationId, $"Resume cancel failed AbsEntry={absEntry}: {closeErr}", ct);
                    return OrderEditResult.RecoveryRequired("ZF_OPKL_CLOSE_FAILED",
                        $"Resume: cancel AbsEntry={absEntry} failed.", operationId);
                }
                foreach (var frag in fragments)
                {
                    var plrs = await _repo.GetPickListRecordsBySoLineAsync(op.SoDocEntry, frag.SoLineNum, ct);
                    var plr  = plrs.FirstOrDefault(p => p.PickListAbsEntry == absEntry && p.Status != PickListStatus.Closed);
                    if (plr != null) await _repo.UpdatePickListPickedQtyAsync(plr.Id, 0m, PickListStatus.Closed, ct);
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
                        $"Resume: SO update failed.", operationId);
                }
                await _repo.AdvanceReplanStepAsync(operationId, ReplanStep.SalesOrderUpdated, ct);
                resumeOrd = ReplanStep.Ordinal(ReplanStep.SalesOrderUpdated);
            }
        }

        // Step 3: Fragment sync
        if (resumeOrd < ReplanStep.Ordinal(ReplanStep.FragmentsSynchronized))
        {
            var dtoMap = dto.UpdatedLines.ToDictionary(l => l.LineNum);
            foreach (var frag in fragments)
            {
                if (!dtoMap.TryGetValue(frag.SoLineNum, out var dtoLine)) continue;
                string  nWhs  = string.IsNullOrWhiteSpace(dtoLine.WhsCode)  ? frag.WhsCode  : dtoLine.WhsCode;
                string  nItem = string.IsNullOrWhiteSpace(dtoLine.ItemCode) ? frag.ItemCode : dtoLine.ItemCode;
                decimal nQty  = dtoLine.Quantity > 0 ? (decimal)dtoLine.Quantity : frag.SoLineQty;
                try { await _repo.UpdateSoLineFragmentEditAsync(frag.Id, nWhs, nItem, nQty, op.ChangedBy, ct); }
                catch (Exception ex)
                {
                    await _repo.SetReplanRecoveryAsync(operationId, $"Resume fragment sync: {ex.Message}", ct);
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

// ─── Shared data for EG tests ─────────────────────────────────────────────────

public static class EgTestData
{
    public const int    DocEntry  = 28917;
    public const int    LineNum0  = 0;
    public const long   OrchId    = 70001L;
    public const long   FragId0   = 70010L;
    public const long   PlrId0    = 80001L;
    public const int    AbsEntry0 = 240;
    public const int    NewAbs    = 99101;
    public static readonly Guid RequestId = Guid.Parse("CCCCCCCC-0000-0000-0000-000000000001");
    public const string EventId   = "093f745d-77b4-f111-b546-5ced8cec097d";

    public static FulfillmentOrchestrationRecord MakeOrch()
        => new()
        {
            Id = OrchId, RequestId = RequestId, State = OrchestrationState.Accepted,
            SoDocEntry = DocEntry, SoDocNum = DocEntry,
            DeliveryLocation = "Mikocheni", AllocationVersion = 1,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };

    public static SoLineFragmentRecord MakeFrag(string whs = "002", string item = "ITEM-A", decimal qty = 5m)
        => new()
        {
            Id = FragId0, OrchestrationId = OrchId, SoDocEntry = DocEntry,
            SoLineNum = LineNum0, ItemCode = item, WhsCode = whs,
            SoLineQty = qty, AllocatedQty = qty
        };

    public static PickListRecordModel MakePlr(decimal pickedQty = 0m, string status = PickListStatus.Released)
        => new()
        {
            Id = PlrId0, OrchestrationId = OrchId, SoLineFragmentId = FragId0,
            SoDocEntry = DocEntry, SoLineNum = LineNum0, WhsCode = "002",
            PickListAbsEntry = AbsEntry0, ReleasedQty = 5m, PickedQty = pickedQty,
            Status = status, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };

    /// <summary>RDR1 line reflecting SAP GUI WHS change 002→004.</summary>
    public static OpenSoLineDto MakeRdr1(string whs = "004", string item = "ITEM-A", decimal qty = 5m)
        => new()
        {
            DocEntry = DocEntry, LineNum = LineNum0,
            ItemCode = item, Quantity = qty, OpenQty = qty, WhsCode = whs
        };

    public static (FakeZfOrderEditRepo repo, FakeSapForOrderEdit sap,
                   FakeZfWhsChangeSapReader sapReader, FakePickListServiceForOrderEdit plService,
                   TestableZfExternalEditCoordinator coord) MakeHarness()
    {
        var repo      = new FakeZfOrderEditRepo();
        var sap       = new FakeSapForOrderEdit();
        var sapReader = new FakeZfWhsChangeSapReader();
        var plService = new FakePickListServiceForOrderEdit { CreatedAbsEntries = [NewAbs] };
        var coord     = new TestableZfExternalEditCoordinator(repo, sap, sapReader, plService);
        return (repo, sap, sapReader, plService, coord);
    }
}

// ─── EG01–EG10 Tests ─────────────────────────────────────────────────────────

/// <summary>
/// EG01-EG10: ZF external SAP GUI edit reconciliation tests.
/// Covers: AMBER replan from 17/U, no UpdateOrder call, OPKL retirement,
/// fragment sync to RDR1, replacement OPKL creation, no-loop idempotency,
/// PickList refresh semantics, idempotent resume, RED path block, RecoveryRequired semantics.
/// </summary>
public sealed class ZfExternalEditTests
{
    // ── EG01: SAP GUI WHS edit + Released zero-pick OPKL → external replan ───

    [Fact]
    public async Task EG01_SapGuiWhsEdit_ZeroPick_StartsExternalReplan()
    {
        var (repo, sap, sapReader, plService, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")]; // SAP GUI changed 002→004

        var result = await coord.ReconcileExternalSapEditAsync(
            EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        Assert.True(result.IsSuccess);
        Assert.True(result.WasAmber);
        Assert.NotNull(result.ReplanOperationId);
        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.Completed, op.CurrentStep);
    }

    // ── EG02: External replan does NOT call SapService.UpdateOrder ────────────

    [Fact]
    public async Task EG02_ExternalReplan_DoesNotCallUpdateOrder()
    {
        var (repo, sap, sapReader, _, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        Assert.Equal(0, sap.UpdateOrderCallCount); // critical: must not trigger another 17/U
    }

    // ── EG03: Old OPKL is retired (SAP close + PLR status = Closed) ──────────

    [Fact]
    public async Task EG03_ExternalReplan_OldOpklRetired()
    {
        var (repo, sap, sapReader, _, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        Assert.Contains(EgTestData.AbsEntry0, sap.ClosedAbsEntries);
        Assert.Single(repo.PlrRetirements);
        Assert.Equal(PickListStatus.Closed, repo.PlrRetirements[0].Status);
    }

    // ── EG04: Fragment is synced to live RDR1 state (new WHS from SAP GUI) ───

    [Fact]
    public async Task EG04_ExternalReplan_FragmentSyncedToRdr1State()
    {
        var (repo, sap, sapReader, _, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        Assert.Single(repo.FragmentEdits);
        Assert.Equal("004", repo.FragmentEdits[0].Whs); // synced to new WHS from RDR1, not old
    }

    // ── EG05: Replacement OPKL created in new WHS ────────────────────────────

    [Fact]
    public async Task EG05_ExternalReplan_ReplacementOpklCreated()
    {
        var (repo, sap, sapReader, plService, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        var result = await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        Assert.Equal(1, plService.CreateCallCount);
        Assert.Contains(EgTestData.NewAbs, result.NewPickListAbsEntries);
        Assert.DoesNotContain(EgTestData.AbsEntry0, result.NewPickListAbsEntries); // old OPKL not in result
    }

    // ── EG06: Second call finds no divergence (no rebuild loop) ──────────────

    [Fact]
    public async Task EG06_AfterExternalReplan_SecondCallFindsNoDivergence()
    {
        var (repo, sap, sapReader, _, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        // First call: replan executes, fragment now = 004
        await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        // Second call (same 17/U retry): fragment already matches RDR1 → no divergence
        var result2 = await coord.ReconcileExternalSapEditAsync(
            EgTestData.DocEntry, "another-event-id", "sap-gui");

        Assert.True(result2.IsSuccess);
        Assert.False(result2.WasAmber); // no AMBER replan triggered again
    }

    // ── EG07: New abs entries returned, not old ones ──────────────────────────

    [Fact]
    public async Task EG07_ExternalReplan_ResultContainsNewAbsEntries_NotOld()
    {
        var (repo, sap, sapReader, plService, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];
        plService.CreatedAbsEntries = [99200, 99201];

        var result = await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        // Handler should refresh these new entries, not the old AbsEntry0=240
        Assert.Contains(99200, result.NewPickListAbsEntries);
        Assert.Contains(99201, result.NewPickListAbsEntries);
        Assert.DoesNotContain(EgTestData.AbsEntry0, result.NewPickListAbsEntries);
    }

    // ── EG08: External edit recovery resumes idempotently ────────────────────

    [Fact]
    public async Task EG08_ExternalReplan_RecoveryResumesIdempotently()
    {
        var (repo, sap, sapReader, plService, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        var plr = EgTestData.MakePlr(pickedQty: 0);
        repo.AddPlr(plr);
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        // Simulate OPKL creation failure on first attempt
        plService.ThrowOnCreate = true;
        var result1 = await coord.ReconcileExternalSapEditAsync(
            EgTestData.DocEntry, EgTestData.EventId, "sap-gui");
        Assert.True(result1.IsRecoveryRequired);

        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.RecoveryRequired, op.CurrentStep);

        // Now fix the condition and resume — must complete without re-cancelling OPKLs
        plService.ThrowOnCreate = false;
        int cancelsBefore = sap.ClosedAbsEntries.Count;

        var result2 = await coord.ResumeReplanAsync(op.OperationId);

        Assert.True(result2.IsSuccess);
        Assert.True(result2.WasAmber);
        Assert.Equal(ReplanStep.Completed, repo.LastReplanOp!.CurrentStep);
        // OPKL should not have been closed again during resume
        Assert.Equal(cancelsBefore, sap.ClosedAbsEntries.Count);
    }

    // ── EG09: RED physical-pick edit returns Blocked, no mutation ─────────────

    [Fact]
    public async Task EG09_RedPhysicalPick_ExternalEdit_Blocked_NoMutation()
    {
        var (repo, sap, sapReader, _, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 1)); // physically picked
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 1m, "Y")); // PickQtty=1
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 1m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        var result = await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        Assert.True(result.IsBlocked);
        Assert.Equal("ZF_PHYSICAL_PICK_STARTED", result.BlockCode);
        Assert.Equal(0, sap.UpdateOrderCallCount); // no SAP mutation
        Assert.Empty(sap.ClosedAbsEntries);        // OPKL not retired
        Assert.Empty(repo.FragmentEdits);          // fragment not modified
        Assert.Null(repo.LastReplanOp);            // no replan operation created
    }

    // ── EG10: RecoveryRequired → event not marked Done (handler returns false) ─

    [Fact]
    public async Task EG10_RecoveryRequired_CoordinatorResult_IsRecoveryRequired()
    {
        var (repo, sap, sapReader, plService, coord) = EgTestData.MakeHarness();
        repo.SetOrchestration(EgTestData.MakeOrch());
        repo.AddFragment(EgTestData.MakeFrag(whs: "002"));
        repo.AddPlr(EgTestData.MakePlr(pickedQty: 0));
        sapReader.RegisterPkl1(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0,
            new Pkl1LineState(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 17, 5m, 0m, "Y"));
        sapReader.RegisterPkl2(EgTestData.AbsEntry0, EgTestData.DocEntry, EgTestData.LineNum0, 0m);
        sap.OpenSoLinesResult = [EgTestData.MakeRdr1(whs: "004")];

        // Simulate failure that requires recovery (OPKL creation fails)
        plService.ThrowOnCreate = true;
        var result = await coord.ReconcileExternalSapEditAsync(EgTestData.DocEntry, EgTestData.EventId, "sap-gui");

        // Handler must return (false, error) for this result — event is retryable
        Assert.True(result.IsRecoveryRequired);
        Assert.NotNull(result.BlockCode);
        Assert.NotNull(result.ReplanOperationId);
        // Verify the replan is persisted in RecoveryRequired state
        var op = repo.LastReplanOp;
        Assert.NotNull(op);
        Assert.Equal(ReplanStep.RecoveryRequired, op.CurrentStep);
    }
}
