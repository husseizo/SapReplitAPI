using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

// DomainRequestLine and Rdr1Line live in Models.ZoneFulfillment (same namespace)

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZD01-ZD25: ZF Diagnosis Console — Phase 3 tests.
/// All tests are pure in-memory.
///
/// Coverage:
///   ZD01-ZD06  Missing RDR1 incident generation
///   ZD07-ZD10  Action blocking (RETRY_DELIVERY / RETRY_INVOICE blocked)
///   ZD11-ZD14  Incident lifecycle (active / historical / resolved)
///   ZD15-ZD18  ReconcileRdr1 hardening (valid / count-mismatch / whs-mismatch)
///   ZD19-ZD22  ReconcileExternalSapEdit RDR1_LINE_MISSING classifier path
///   ZD23-ZD25  Reference incident regression (SO 28879 / 28890 / 28917 class)
/// </summary>
public sealed class ZfDiagnosticConsoleTests
{
    // ── ZD01: Single fragment, RDR1 row absent → one ACTIVE incident ─────────

    [Fact]
    public void ZD01_SingleFragment_Rdr1Missing_GeneratesActiveIncident()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "VAG13782", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, incidents[0].Code);
        Assert.Equal(ZfDiagnosticIncidentStatus.Active, incidents[0].IncidentStatus);
        Assert.Equal("VAG13782", incidents[0].AffectedItemCode);
        Assert.Equal(0, incidents[0].SoLineNum);
        Assert.False(incidents[0].IsResolved);
    }

    // ── ZD02: All fragments missing RDR1 → one incident per fragment ─────────

    [Fact]
    public void ZD02_AllFragmentsRdr1Missing_GeneratesOneIncidentPerFragment()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [
                Frag(id: 1, lineNum: 0, itemCode: "ITEM-A", rdr1ItemCode: null),
                Frag(id: 2, lineNum: 1, itemCode: "ITEM-B", rdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Equal(2, incidents.Count);
        Assert.All(incidents, i => Assert.Equal(ZfDiagnosticIncidentStatus.Active, i.IncidentStatus));
        Assert.All(incidents, i => Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, i.Code));
    }

    // ── ZD03: Fragment with valid RDR1 → no incident generated ───────────────

    [Fact]
    public void ZD03_FragmentHasValidRdr1_NoIncidentGenerated()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.Completed,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "BM12441", rdr1ItemCode: "BM12441")]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Empty(incidents);
    }

    // ── ZD04: Mixed fragments (one missing, one present) → one incident ───────

    [Fact]
    public void ZD04_MixedFragments_OneMissing_OneIncident()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [
                Frag(id: 1, lineNum: 0, itemCode: "BM12441", rdr1ItemCode: "BM12441"),   // present
                Frag(id: 2, lineNum: 1, itemCode: "VAG13782", rdr1ItemCode: null),        // missing
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.Equal("VAG13782", incidents[0].AffectedItemCode);
        Assert.Equal(1, incidents[0].SoLineNum);
    }

    // ── ZD05: Incident evidence contains expected keys ────────────────────────

    [Fact]
    public void ZD05_Incident_Evidence_ContainsFragmentAndDocEntry()
    {
        var diag = MakeDiag(
            soDocEntry:  28879,
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:   [Frag(id: 99, lineNum: 1, itemCode: "VAG13782", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        var evidence = incidents[0].Evidence;
        Assert.Contains(evidence, e => e.Contains("DocEntry=28879"));
        Assert.Contains(evidence, e => e.Contains("VAG13782"));
        Assert.Contains(evidence, e => e.Contains("LineNum=1"));
    }

    // ── ZD06: Incident severity is HIGH for missing RDR1 ─────────────────────

    [Fact]
    public void ZD06_FragmentRdr1Missing_Incident_SeverityHigh()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "ITEM001", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Equal(ZfDiagnosticSeverity.High, incidents[0].Severity);
        Assert.Equal(ZfDiagnosticCategory.SapZfIntegrityDivergence, incidents[0].Category);
    }

    // ── ZD07: Active incident blocks RETRY_DELIVERY and RETRY_INVOICE ─────────

    [Fact]
    public void ZD07_ActiveIncident_BlockedActions_IncludeRetryDeliveryAndRetryInvoice()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "VAG13782", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        var blocked = incidents[0].BlockedActions;
        Assert.Contains("RETRY_DELIVERY", blocked);
        Assert.Contains("RETRY_INVOICE", blocked);
    }

    // ── ZD08: Active incident safe action = MANUAL_RESOLUTION_REQUIRED disabled

    [Fact]
    public void ZD08_ActiveIncident_SafeAction_IsManualResolutionDisabled()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "VAG13782", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        var action = Assert.Single(incidents[0].SafeActions);
        Assert.Equal("MANUAL_RESOLUTION_REQUIRED", action.Code);
        Assert.False(action.Enabled);
        Assert.False(action.MutationAvailable);
    }

    // ── ZD09: Classifier agrees — FragmentRdr1Missing → MANUAL_RESOLUTION_REQUIRED

    [Fact]
    public void ZD09_Classifier_FragmentRdr1Missing_Actions_OnlyManualResolution()
    {
        var input = new ZfClassifierInput(
            OrchState:            OrchestrationState.Accepted,
            FailureKind:          null,
            ActiveReplan:         null,
            Fragments:            [new ZfFragmentClassInput(0, "002", null, null, null, null, 0m, "N", null, false)],
            Deliveries:           [],
            HasSuccessfulInvoice: false);

        var verdict = ZfConsistencyClassifier.Classify(input);
        var actions = ZfConsistencyClassifier.RecommendActions(verdict);

        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, verdict.Status);
        Assert.Single(actions);
        Assert.False(actions[0].Enabled);
        Assert.False(actions[0].MutationAvailable);
    }

    // ── ZD10: SAP lookup failure suppresses false FragmentRdr1Missing in classifier

    [Fact]
    public void ZD10_SapLookupFailed_DoesNotEmitFragmentRdr1MissingAction()
    {
        var input = new ZfClassifierInput(
            OrchState:            OrchestrationState.Accepted,
            FailureKind:          null,
            ActiveReplan:         null,
            Fragments:            [new ZfFragmentClassInput(0, "002", null, null, null, null, 0m, "N", null, false)],
            Deliveries:           [],
            HasSuccessfulInvoice: false,
            SapRdr1LookupFailed:  true);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.NotEqual(ZfConsistencyStatus.FragmentRdr1Missing, verdict.Status);
    }

    // ── ZD11: Terminal (Completed) order → incidents have Historical status ────

    [Fact]
    public void ZD11_CompletedOrder_WithPreviouslyMissingRdr1_IncidentIsHistorical()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.Completed,   // order completed
            orchState:   OrchestrationState.Accepted,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "ITEM001", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.Equal(ZfDiagnosticIncidentStatus.Historical, incidents[0].IncidentStatus);
        Assert.True(incidents[0].IsResolved);
        Assert.NotNull(incidents[0].RecoveryEvidence);
    }

    // ── ZD12: Terminal (Canceled) order → incidents Historical ───────────────

    [Fact]
    public void ZD12_CanceledOrder_MissingRdr1_IncidentIsHistorical()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.Canceled,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "ITEM001", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.Equal(ZfDiagnosticIncidentStatus.Historical, incidents[0].IncidentStatus);
        Assert.True(incidents[0].IsResolved);
    }

    // ── ZD13: Historical incident has no blocked actions ─────────────────────

    [Fact]
    public void ZD13_HistoricalIncident_BlockedActions_Empty()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.Completed,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "ITEM001", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Empty(incidents[0].BlockedActions);
    }

    // ── ZD14: Historical incident Impact is null (order already resolved) ─────

    [Fact]
    public void ZD14_HistoricalIncident_Impact_IsNull()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.Completed,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "ITEM001", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Null(incidents[0].Impact);
    }

    // ── ZD15: ReconcileRdr1 — valid positional match succeeds ────────────────

    [Fact]
    public void ZD15_ReconcileRdr1_ValidMatch_ReturnsFragments()
    {
        var frags = new[]
        {
            new AllocationFragment(Guid.NewGuid(), "002", 1m, 0m),
        };
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM001", "002", 1m, 1m),
        };

        var result = ZoneFulfillmentSapOrderService.ReconcileRdr1(1, 1, 100, frags, rdr1);

        Assert.Single(result);
        Assert.Equal(0,       result[0].SoLineNum);
        Assert.Equal("ITEM001", result[0].ItemCode);
        Assert.Equal("002",   result[0].WhsCode);
    }

    // ── ZD16: ReconcileRdr1 — count mismatch throws ───────────────────────────

    [Fact]
    public void ZD16_ReconcileRdr1_CountMismatch_Throws()
    {
        var frags = new[]
        {
            new AllocationFragment(Guid.NewGuid(), "002", 1m, 0m),
            new AllocationFragment(Guid.NewGuid(), "004", 1m, 0m),
        };
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM001", "002", 1m, 1m),   // only 1 instead of 2
        };

        Assert.Throws<InvalidOperationException>(
            () => ZoneFulfillmentSapOrderService.ReconcileRdr1(1, 1, 100, frags, rdr1));
    }

    // ── ZD17: ReconcileRdr1 — warehouse mismatch throws ──────────────────────

    [Fact]
    public void ZD17_ReconcileRdr1_WhsCodeMismatch_Throws()
    {
        // Fragment expects WHS=002 but SAP returned WHS=004 at position 0
        var frags = new[]
        {
            new AllocationFragment(Guid.NewGuid(), "002", 1m, 0m),
        };
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM001", "004", 1m, 1m),   // wrong WHS
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ZoneFulfillmentSapOrderService.ReconcileRdr1(1, 1, 100, frags, rdr1));

        Assert.Contains("002", ex.Message);
        Assert.Contains("004", ex.Message);
    }

    // ── ZD17b: ReconcileRdr1 — ItemCode mismatch with matching WHS throws ───────

    [Fact]
    public void ZD17b_ReconcileRdr1_ItemCodeMismatch_SameWhs_Throws()
    {
        // Fragment expects ITEM-AAA at WHS=003; SAP returned ITEM-BBB at WHS=003
        // Warehouse match alone is insufficient — must validate ItemCode too
        var reqLineId = Guid.NewGuid();
        var domainLines = new[]
        {
            new DomainRequestLine(reqLineId, 0, "ITEM-AAA", 1m, null, null, null, null),
        };
        var frags = new[]
        {
            new AllocationFragment(reqLineId, "003", 1m, 0m),
        };
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM-BBB", "003", 1m, 1m),   // same WHS but wrong item
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ZoneFulfillmentSapOrderService.ReconcileRdr1(1, 1, 100, frags, rdr1, domainLines));

        Assert.Contains("ITEM-AAA", ex.Message);
        Assert.Contains("ITEM-BBB", ex.Message);
    }

    // ── ZD18: ReconcileRdr1 — two fragments, same WHS, correct order succeeds ─

    [Fact]
    public void ZD18_ReconcileRdr1_TwoFragmentsSameWhs_CorrectOrder_Succeeds()
    {
        var frags = new[]
        {
            new AllocationFragment(Guid.NewGuid(), "002", 1m, 0m),
            new AllocationFragment(Guid.NewGuid(), "002", 2m, 0m),
        };
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM-A", "002", 1m, 1m),
            new Rdr1Line(1, "ITEM-B", "002", 2m, 2m),
        };

        var result = ZoneFulfillmentSapOrderService.ReconcileRdr1(1, 1, 100, frags, rdr1);

        Assert.Equal(2, result.Count);
        Assert.Equal("ITEM-A", result[0].ItemCode);
        Assert.Equal("ITEM-B", result[1].ItemCode);
    }

    // ── ZD19: Classifier — Accepted with null ItemCode → FragmentRdr1Missing ──

    [Fact]
    public void ZD19_Classifier_Accepted_NullRdr1ItemCode_FragmentRdr1Missing()
    {
        var input = new ZfClassifierInput(
            OrchState:            OrchestrationState.Accepted,
            FailureKind:          null,
            ActiveReplan:         null,
            Fragments:            [new ZfFragmentClassInput(1, "002", null, null, null, null, 0m, "N", null, false)],
            Deliveries:           [],
            HasSuccessfulInvoice: false);

        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing,
            ZfConsistencyClassifier.Classify(input).Status);
    }

    // ── ZD20: Classifier — SapRdr1LookupFailed=true suppresses FragmentRdr1Missing

    [Fact]
    public void ZD20_Classifier_SapLookupFailed_SkipsMissingRdr1Check()
    {
        var input = new ZfClassifierInput(
            OrchState:            OrchestrationState.Accepted,
            FailureKind:          null,
            ActiveReplan:         null,
            Fragments:            [new ZfFragmentClassInput(1, "002", null, null, null, null, 0m, "N", null, false)],
            Deliveries:           [],
            HasSuccessfulInvoice: false,
            SapRdr1LookupFailed:  true);

        // Should fall through to AwaitingPick (no PLR assigned, no pick started)
        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.NotEqual(ZfConsistencyStatus.FragmentRdr1Missing, verdict.Status);
    }

    // ── ZD21: HasActiveIncidents true when ACTIVE incidents present ───────────

    [Fact]
    public void ZD21_HasActiveIncidents_TrueWhenActiveIncidentExists()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "VAG13782", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        bool hasActive = incidents.Any(i => i.IncidentStatus == ZfDiagnosticIncidentStatus.Active);

        Assert.True(hasActive);
    }

    // ── ZD22: HasActiveIncidents false when only Historical incidents ──────────

    [Fact]
    public void ZD22_HasActiveIncidents_FalseWhenOnlyHistoricalIncidents()
    {
        var diag = MakeDiag(
            consistency: ZfConsistencyStatus.Completed,
            fragments:   [Frag(id: 1, lineNum: 0, itemCode: "ITEM001", rdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        bool hasActive = incidents.Any(i => i.IncidentStatus == ZfDiagnosticIncidentStatus.Active);

        Assert.False(hasActive);
    }

    // ── ZD23: SO 28879 class — Accepted, ghost fragment → ACTIVE HIGH incident ─

    [Fact]
    public void ZD23_SO28879_Class_GhostFragment_ActiveHighIncident()
    {
        // Two-line order: BM12441 has RDR1; VAG13782 was externally deleted
        var diag = MakeDiag(
            soDocEntry:  28879,
            orchState:   OrchestrationState.Accepted,
            consistency: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [
                Frag(id: 20115, lineNum: 0, itemCode: "BM12441",  rdr1ItemCode: "BM12441"),
                Frag(id: 20116, lineNum: 1, itemCode: "VAG13782", rdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        var inc = incidents[0];
        Assert.Equal(ZfDiagnosticIncidentStatus.Active, inc.IncidentStatus);
        Assert.Equal(ZfDiagnosticSeverity.High, inc.Severity);
        Assert.Equal("VAG13782", inc.AffectedItemCode);
        Assert.Equal(28879, inc.SapDocEntry);
        Assert.Contains("RETRY_DELIVERY", inc.BlockedActions);
        Assert.Contains("RETRY_INVOICE", inc.BlockedActions);
    }

    // ── ZD24: SO 28890 class — Completed order → no active incidents ──────────

    [Fact]
    public void ZD24_SO28890_Class_CompletedOrder_NoActiveIncidents()
    {
        // SO 28890 is COMPLETED — stale WHS was repaired, ODLN+OINV created
        var diag = MakeDiag(
            soDocEntry:  28890,
            orchState:   OrchestrationState.Accepted,
            consistency: ZfConsistencyStatus.Completed,
            fragments:   [Frag(id: 20117, lineNum: 0, itemCode: "VAG10604", rdr1ItemCode: "VAG10604")]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Empty(incidents);
    }

    // ── ZD25: SO 28917 class — Completed (ODLN+OINV) → no active incidents ─────

    [Fact]
    public void ZD25_SO28917_Class_DeliveredAndInvoiced_NoActiveIncidents()
    {
        // SO 28917 fragment WHS=004, RDR1=004 (repair done), ODLN+OINV present
        var diag = MakeDiag(
            soDocEntry:  28917,
            orchState:   OrchestrationState.Accepted,
            consistency: ZfConsistencyStatus.Completed,
            fragments:   [Frag(id: 20155, lineNum: 0, itemCode: "VAG13782", rdr1ItemCode: "VAG13782")]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Empty(incidents);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ZfOrderDiagnosticResult MakeDiag(
        string                        consistency,
        IReadOnlyList<ZfFragmentDiagnostic>? fragments = null,
        int                           soDocEntry  = 99999,
        string                        orchState   = OrchestrationState.Accepted)
    {
        var verdict = new ZfConsistencyVerdict(
            Status:              consistency,
            Detail:              null,
            HasFragmentMismatch: consistency == ZfConsistencyStatus.FragmentRdr1Missing,
            HasActiveReplan:     false,
            HasDeliveryFailure:  false,
            InvoicePending:      false);

        return new ZfOrderDiagnosticResult
        {
            SoDocEntry       = soDocEntry,
            OrchestrationId  = 10000L,
            RequestId        = Guid.NewGuid(),
            State            = orchState,
            DeliveryLocation = "TEST",
            CreatedAtUtc     = DateTime.UtcNow.AddDays(-1),
            UpdatedAtUtc     = DateTime.UtcNow,
            Fragments        = fragments ?? [],
            Deliveries       = [],
            Invoices         = [],
            Consistency      = verdict,
            AvailableActions = [],
        };
    }

    private static ZfFragmentDiagnostic Frag(
        long    id,
        int     lineNum,
        string  itemCode,
        string? rdr1ItemCode)
        => new(
            Id:                id,
            SoLineNum:         lineNum,
            ItemCode:          itemCode,
            FragmentWhsCode:   "002",
            SoLineQty:         1m,
            AllocatedQty:      1m,
            DeliveredQty:      0m,
            OriginalWhsCode:   null,
            WhsChangedAtUtc:   null,
            WhsChangedBy:      null,
            SapRdr1ItemCode:   rdr1ItemCode,
            SapRdr1WhsCode:    rdr1ItemCode is null ? null : "002",
            SapRdr1LineStatus: rdr1ItemCode is null ? null : "O",
            SapRdr1OpenQty:    rdr1ItemCode is null ? 0m : 1m,
            WhsMatchesSap:     rdr1ItemCode is not null,
            PickListWhsCode:   null,
            PickListAbsEntry:  null,
            PickListPickedQty: 0m,
            PickListStatus:    "Released",
            SapPkl1PickQtty:   0m,
            SapPkl1PickStatus: null,
            SapPkl2BinCode:    null,
            SapPkl2BinWhsCode: null
        );
}
