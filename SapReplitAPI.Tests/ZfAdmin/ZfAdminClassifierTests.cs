using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZA01-ZA12: ZF Operations Console — Phase 1A classifier tests.
/// All tests are pure in-memory (no SQL, no COM, no SAP mutations).
/// ZfConsistencyClassifier is a static pure function.
/// </summary>
public sealed class ZfAdminClassifierTests
{
    // ── ZA01: Healthy order ────────────────────────────────────────────────────

    [Fact]
    public void ZA01_HealthyOrder_Accepted_PickStarted_FragMatchesSap()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "001", rdr1Whs: "001", plrWhs: "001", pkl1Qty: 1m, pkl2BinWhs: "001")]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.PhysicalPickStarted, verdict.Status);
        Assert.False(verdict.HasFragmentMismatch);
    }

    // ── ZA02: Released, no pick → WAITING or ZF_AWAITING_PICK ────────────────

    [Fact]
    public void ZA02_Accepted_NoPick_NoMismatch_ReturnsAwaitingPick()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "001", rdr1Whs: "001", plrWhs: "001", pkl1Qty: 0m, pkl2BinWhs: null)]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.AwaitingPick, verdict.Status);
    }

    [Fact]
    public void ZA02b_Received_ReturnsWaiting()
    {
        var input = MakeInput(state: OrchestrationState.Received);
        var verdict = ZfConsistencyClassifier.Classify(input);
        Assert.Equal(ZfConsistencyStatus.Waiting, verdict.Status);
    }

    // ── ZA03: Zero-picked WHS divergence → ZF_REPLAN_AVAILABLE ───────────────

    [Fact]
    public void ZA03_ZeroPick_FragMismatch_ReturnsReplanAvailable()
    {
        // Fragment WHS=002, RDR1 WHS=004, but PickQtty=0 → AMBER pre-pick
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "002", pkl1Qty: 0m, pkl2BinWhs: null)]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.ReplanAvailable, verdict.Status);
        Assert.True(verdict.HasFragmentMismatch);
    }

    // ── ZA04: Physical pick + matching RDR1/PKL2/PLR + stale fragment ─────────
    // This is the SO 28917 scenario (pre-repair state).

    [Fact]
    public void ZA04_PhysicalPick_StrongEvidence_ReturnsStaleFragmentAfterValidPick()
    {
        // Fragment WHS=002 (stale), but:
        // RDR1=004, PLR=004, PKL2 bin WHS=004 — all three agree on 004
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "004")]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.StaleFragmentAfterValidPick, verdict.Status);
        Assert.True(verdict.HasFragmentMismatch);
    }

    [Fact]
    public void ZA04_Actions_IncludeReconcileAction()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "004")]);

        var verdict  = ZfConsistencyClassifier.Classify(input);
        var actions  = ZfConsistencyClassifier.RecommendActions(verdict);

        Assert.Single(actions);
        Assert.Equal("RECONCILE_STALE_FRAGMENT_AFTER_VALID_PICK", actions[0].Code);
        Assert.True(actions[0].Enabled);
        Assert.True(actions[0].MutationAvailable);   // Phase 2: mutation now available
    }

    // ── ZA05: Physical pick with conflicting SAP evidence → BLOCKED ────────────

    [Fact]
    public void ZA05_PhysicalPick_PlrDoesNotMatchRdr1_ReturnsFragMismatch()
    {
        // PLR says 002, RDR1 says 004 → conflicting → BLOCKED
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "002", pkl1Qty: 1m, pkl2BinWhs: "002")]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.FragWhsMismatch, verdict.Status);
        Assert.True(verdict.HasFragmentMismatch);
    }

    [Fact]
    public void ZA05b_PhysicalPick_Pkl2DoesNotMatchRdr1_ReturnsFragMismatch()
    {
        // PLR matches RDR1 but physical bin is in wrong WHS → conflicting
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "002")]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.FragWhsMismatch, verdict.Status);
    }

    [Fact]
    public void ZA05c_BLOCKED_NoRepairAction()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "002", pkl1Qty: 1m, pkl2BinWhs: "002")]);

        var verdict = ZfConsistencyClassifier.Classify(input);
        var actions = ZfConsistencyClassifier.RecommendActions(verdict);

        Assert.Single(actions);
        Assert.Equal("MANUAL_INVESTIGATION_REQUIRED", actions[0].Code);
        Assert.False(actions[0].Enabled);
    }

    // ── ZA06: Failed delivery shown with status ────────────────────────────────

    [Fact]
    public void ZA06_FailedDelivery_NoSuccessfulDelivery_ReturnsDeliveryFailed()
    {
        // All picks done (PickQtty>0, no mismatch), but delivery failed
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "004", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "004")],
            deliveries: [DelivFailed()]);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.PhysicalPickStarted, verdict.Status);
        Assert.True(verdict.HasDeliveryFailure);
    }

    // ── ZA07: Active replan → ZF_ORDER_REPLAN_IN_PROGRESS ────────────────────

    [Fact]
    public void ZA07_ActiveReplan_InProgress_ReturnsReplanInProgress()
    {
        var replan = new ZfReplanDiagnostic(1, Guid.NewGuid(),
            ReplanStep.OldPickListsRetired, ReplanStep.Prepared,
            null, "api-user", DateTime.UtcNow, null);

        var input = MakeInput(
            state: OrchestrationState.Accepted,
            activeReplan: replan);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.ReplanInProgress, verdict.Status);
        Assert.True(verdict.HasActiveReplan);
    }

    // ── ZA08: RecoveryRequired → ZF_ORDER_REPLAN_RECOVERY_REQUIRED ───────────

    [Fact]
    public void ZA08_ActiveReplan_RecoveryRequired_ReturnsRecoveryRequired()
    {
        var replan = new ZfReplanDiagnostic(1, Guid.NewGuid(),
            ReplanStep.RecoveryRequired, ReplanStep.OldPickListsRetired,
            "SAP timeout", "api-user", DateTime.UtcNow.AddMinutes(-5), null);

        var input = MakeInput(
            state: OrchestrationState.Accepted,
            activeReplan: replan);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.ReplanRecoveryRequired, verdict.Status);
        Assert.True(verdict.HasActiveReplan);
    }

    [Fact]
    public void ZA08_RecoveryRequired_Actions_IncludeResumeReplan()
    {
        var replan = new ZfReplanDiagnostic(1, Guid.NewGuid(),
            ReplanStep.RecoveryRequired, null, "error", "api-user", DateTime.UtcNow, null);

        var input   = MakeInput(state: OrchestrationState.Accepted, activeReplan: replan);
        var verdict = ZfConsistencyClassifier.Classify(input);
        var actions = ZfConsistencyClassifier.RecommendActions(verdict);

        Assert.Single(actions);
        Assert.Equal("RESUME_REPLAN", actions[0].Code);
    }

    // ── ZA09: ODLN exists, no OINV → DELIVERED / INVOICE_PENDING ─────────────

    [Fact]
    public void ZA09_SuccessfulDelivery_NoInvoice_ReturnsDelivered()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            deliveries: [DelivCreated()],
            hasSuccessfulInvoice: false);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.Delivered, verdict.Status);
        Assert.True(verdict.InvoicePending);
    }

    [Fact]
    public void ZA09_Delivered_Actions_IncludeRetryInvoice()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            deliveries: [DelivCreated()],
            hasSuccessfulInvoice: false);

        var verdict = ZfConsistencyClassifier.Classify(input);
        var actions = ZfConsistencyClassifier.RecommendActions(verdict);

        Assert.Single(actions);
        Assert.Equal("RETRY_INVOICE", actions[0].Code);
    }

    // ── ZA10: ODLN + OINV → COMPLETED ─────────────────────────────────────────

    [Fact]
    public void ZA10_SuccessfulDelivery_SuccessfulInvoice_ReturnsCompleted()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            deliveries: [DelivCreated()],
            hasSuccessfulInvoice: true);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.Completed, verdict.Status);
        Assert.False(verdict.InvoicePending);
    }

    // ── ZA11: SO 28917 current production state ───────────────────────────────
    // After repair: ODLN=30951 created, fragment 002→004.
    // Classifier should derive from actual delivery/invoice evidence, not hardcode.
    // Test simulates the post-repair state: ODLN=30951 exists, invoice absent.

    [Fact]
    public void ZA11_SO28917_PostRepair_DeliveryExistsNoInvoice_ReturnsDelivered()
    {
        // Post-repair: fragment WHS=004 now matches RDR1=004. Delivery created.
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "004", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "004")],
            deliveries: [DelivCreated(sapDocEntry: 30951)],
            hasSuccessfulInvoice: false);

        var verdict = ZfConsistencyClassifier.Classify(input);

        // Classify from evidence: ODLN exists, invoice not yet
        Assert.Equal(ZfConsistencyStatus.Delivered, verdict.Status);
        Assert.False(verdict.HasFragmentMismatch);
        Assert.True(verdict.InvoicePending);
    }

    [Fact]
    public void ZA11_SO28917_PostRepair_BothOdlnAndInvoice_ReturnsCompleted()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "004", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "004")],
            deliveries: [DelivCreated(sapDocEntry: 30951)],
            hasSuccessfulInvoice: true);

        var verdict = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(ZfConsistencyStatus.Completed, verdict.Status);
        Assert.False(verdict.InvoicePending);
    }

    // ── ZA12: Classifier is purely read — no side effects ────────────────────

    [Fact]
    public void ZA12_Classify_IsPureFunction_NoSideEffects()
    {
        var input = MakeInput(
            state: OrchestrationState.Accepted,
            fragments: [Frag(fragWhs: "002", rdr1Whs: "004", plrWhs: "004", pkl1Qty: 1m, pkl2BinWhs: "004")]);

        // Call twice — same input must yield same output, no exceptions
        var v1 = ZfConsistencyClassifier.Classify(input);
        var v2 = ZfConsistencyClassifier.Classify(input);

        Assert.Equal(v1.Status, v2.Status);
        Assert.Equal(v1.Detail, v2.Detail);
    }

    // ── Terminal state tests ─────────────────────────────────────────────────

    [Fact]
    public void Canceled_ReturnsCanceled()
    {
        var v = ZfConsistencyClassifier.Classify(MakeInput(OrchestrationState.Canceled));
        Assert.Equal(ZfConsistencyStatus.Canceled, v.Status);
    }

    [Fact]
    public void Failed_ReturnsFailed()
    {
        var v = ZfConsistencyClassifier.Classify(MakeInput(OrchestrationState.Failed));
        Assert.Equal(ZfConsistencyStatus.Failed, v.Status);
    }

    [Fact]
    public void UnknownOutcome_ReturnsOrderStateMismatch()
    {
        var v = ZfConsistencyClassifier.Classify(MakeInput(OrchestrationState.UnknownOutcome));
        Assert.Equal(ZfConsistencyStatus.OrderStateMismatch, v.Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ZfClassifierInput MakeInput(
        string                              state,
        IReadOnlyList<ZfFragmentClassInput>? fragments           = null,
        IReadOnlyList<ZfDeliveryClassInput>? deliveries          = null,
        bool                                hasSuccessfulInvoice = false,
        ZfReplanDiagnostic?                 activeReplan         = null)
        => new(
            OrchState:            state,
            FailureKind:          null,
            ActiveReplan:         activeReplan,
            Fragments:            fragments  ?? [],
            Deliveries:           deliveries ?? [],
            HasSuccessfulInvoice: hasSuccessfulInvoice
        );

    private static ZfFragmentClassInput Frag(
        string  fragWhs,
        string? rdr1Whs    = null,
        string? plrWhs     = null,
        decimal pkl1Qty    = 0m,
        string? pkl2BinWhs = null)
        => new(
            SoLineNum:              0,
            FragmentWhsCode:        fragWhs,
            SapRdr1WhsCode:         rdr1Whs,
            PickListRecordWhsCode:  plrWhs,
            SapPkl1PickQtty:        pkl1Qty,
            SapPkl1PickStatus:      pkl1Qty > 0 ? "Y" : "N",
            SapPkl2BinWhsCode:      pkl2BinWhs,
            HasActiveOdln:          false
        );

    private static ZfDeliveryClassInput DelivFailed()
        => new(DeliveryRecordStatus.Failed);

    private static ZfDeliveryClassInput DelivCreated(int sapDocEntry = 99999)
        => new(DeliveryRecordStatus.Created);
}
