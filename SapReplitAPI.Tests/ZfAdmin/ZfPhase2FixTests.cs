using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZF Phase 2 fix tests — T1-T5, R1-R7, S1-S4.
/// All tests are in-memory (no SQL, no COM, no SAP mutations).
/// </summary>
public sealed class ZfPhase2FixTests
{
    // ── T1-T5: ValidatePickListRecordForDelivery ─────────────────────────────

    [Fact]
    public void T1_Validate_ThrowsWhen_PickListRecordFragmentId_Mismatch()
    {
        var fd = MakeGateData(
            frag: MakeFragment(id: 10),
            plr:  MakePickRecord(fragmentId: 99));   // mismatched Id

        var ex = Assert.Throws<InvalidOperationException>(
            () => ZoneFulfillmentDeliveryService.ValidatePickListRecordForDelivery(fd));

        Assert.Contains("pick evidence mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T2_Validate_ThrowsWhen_StatusNotPicked()
    {
        var fd = MakeGateData(
            plr: MakePickRecord(fragmentId: 1, status: PickListStatus.Released));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ZoneFulfillmentDeliveryService.ValidatePickListRecordForDelivery(fd));

        Assert.Contains("not Picked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T3_Validate_ThrowsWhen_WhsCodeEmpty()
    {
        var fd = MakeGateData(
            plr: MakePickRecord(fragmentId: 1, whsCode: ""));   // empty WHS

        var ex = Assert.Throws<InvalidOperationException>(
            () => ZoneFulfillmentDeliveryService.ValidatePickListRecordForDelivery(fd));

        Assert.Contains("WhsCode empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T4_Validate_PassesWhen_AllConditionsMet()
    {
        var fd = MakeGateData(
            frag: MakeFragment(id: 1, whsCode: "WHS-OLD"),
            plr:  MakePickRecord(fragmentId: 1, whsCode: "WHS-NEW", status: PickListStatus.Picked));

        // Must not throw — PLR WhsCode differs from Fragment.WhsCode; that is expected
        ZoneFulfillmentDeliveryService.ValidatePickListRecordForDelivery(fd);
    }

    [Fact]
    public void T5_ModelContract_PickListRecord_IsAuthoritativeWarehouse()
    {
        // Verifies that DeliveryFragmentGateData exposes PickListRecord.WhsCode as a
        // distinct field from Fragment.WhsCode, enabling the delivery path to use the
        // correct pick warehouse rather than the stale allocation warehouse.
        var fd = MakeGateData(
            frag: MakeFragment(id: 1, whsCode: "WHS-STALE"),
            plr:  MakePickRecord(fragmentId: 1, whsCode: "WHS-ACTUAL"));

        Assert.Equal("WHS-STALE",  fd.Fragment.WhsCode);
        Assert.Equal("WHS-ACTUAL", fd.PickListRecord.WhsCode);
        Assert.NotEqual(fd.Fragment.WhsCode, fd.PickListRecord.WhsCode);
    }

    // ── R1-R7: ReconcileStaleFragmentAsync ───────────────────────────────────

    [Fact]
    public async Task R1_Reconcile_StateGuard_Fires_WhenExpectedStateDiffers()
    {
        var svc = MakeR(diagnostic: DiagStaleFragment(800));

        var result = await svc.ReconcileStaleFragmentAsync(
            800, "admin", "Delivered",   // expected="Delivered", live=Accepted
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.StateChanged, result.ErrorCode);
    }

    [Fact]
    public async Task R2_Reconcile_ActionNotAvailable_WhenConsistencyStatusWrong()
    {
        // Completed state → no RECONCILE action in AvailableActions
        var svc = MakeR(diagnostic: DiagCompleted(810));

        var result = await svc.ReconcileStaleFragmentAsync(
            810, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.ActionNotAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task R3_Reconcile_PC5_Blocked_DeliveryAlreadyExists()
    {
        var diag = DiagStaleFragment(820, deliveries: [new ZfDeliveryDiagnostic(
            1, DeliveryRecordStatus.Created, 55, 100, null,
            DateTime.UtcNow, DateTime.UtcNow, "O", "N", "BP01")]);
        var svc = MakeR(diagnostic: diag);

        var result = await svc.ReconcileStaleFragmentAsync(
            820, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.PreconditionFailed, result.ErrorCode);
        Assert.Contains("successful delivery already exists", result.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R4_Reconcile_PC6_Blocked_ActiveReplanInProgress()
    {
        var diag = DiagStaleFragment(830, activeReplan: new ZfReplanDiagnostic(
            1, Guid.NewGuid(), "Executing", null, null,
            "zf-admin", DateTime.UtcNow, null));
        var svc = MakeR(diagnostic: diag);

        var result = await svc.ReconcileStaleFragmentAsync(
            830, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.PreconditionFailed, result.ErrorCode);
        Assert.Contains("Active replan", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R5_Reconcile_PC9_Blocked_FragmentAlreadyDelivered()
    {
        // Fragment has DeliveredQty > 0 — positional arg 7 (index 6)
        var deliveredFrag = new ZfFragmentDiagnostic(
            10, 1, "ITEM-A", "WHS-OLD",
            5m, 5m, 3m,          // DeliveredQty=3 → PC9 fires
            null, null, null,
            "WHS-NEW", "O", 5m, false,
            "WHS-NEW", 77, 5m, "Picked",
            5m, null, "BIN-01", "WHS-NEW");

        var diag = DiagStaleFragment(840, fragments: [deliveredFrag]);
        var svc  = MakeR(diagnostic: diag);

        var result = await svc.ReconcileStaleFragmentAsync(
            840, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.PreconditionFailed, result.ErrorCode);
        Assert.Contains("already delivered", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R6_Reconcile_ConcurrencyConflict_ReturnsPreconditionFailed()
    {
        // reconcileRows=0 → optimistic concurrency conflict
        var svc = MakeR(diagnostic: DiagStaleFragment(850), reconcileRows: 0);
        var store = new InMemoryAuditStore();
        svc = MakeR(DiagStaleFragment(850), store, reconcileRows: 0);

        var result = await svc.ReconcileStaleFragmentAsync(
            850, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.PreconditionFailed, result.ErrorCode);
        Assert.Contains("concurrency conflict", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R7_Reconcile_SuccessPath_AuditWritten_AfterStateReturned()
    {
        var store = new InMemoryAuditStore();
        var svc   = MakeR(DiagStaleFragment(860), store, reconcileRows: 1);

        var result = await svc.ReconcileStaleFragmentAsync(
            860, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ZfAdminActionType.ReconcileFragment, result.ActionType);
        Assert.NotNull(result.AfterState);
        Assert.Single(store.Entries);
        Assert.Equal(ZfAdminAuditResult.Success, store.Entries[0].Result);
    }

    // ── S1-S4: RetryDeliveryAsync state transition (FIX 3) ───────────────────

    [Fact]
    public async Task S1_Retry_StateUpdateNotCalled_WhenGateBlocked()
    {
        var svc = MakeS(
            delivResult: BlockedDelivery());

        await svc.RetryDeliveryAsync(
            900, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(svc.StateUpdatedToDelivered);
    }

    [Fact]
    public async Task S2_Retry_StateUpdateCalled_WhenNewOdlnCreated()
    {
        var svc = MakeS(
            delivResult: SuccessfulDelivery(odlnDocEntry: 9001));

        await svc.RetryDeliveryAsync(
            910, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.True(svc.StateUpdatedToDelivered);
    }

    [Fact]
    public async Task S3_Retry_StateUpdateCalledBeforeAfterDiagnosticRead()
    {
        // Uses a specialized call-sequence tracker to verify ordering.
        var diagRetry = DiagRetryDeliveryAvailable(920);
        var svc = new CallSequenceRetryService(diagRetry, SuccessfulDelivery(odlnDocEntry: 9002));

        await svc.RetryDeliveryAsync(
            920, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        // Expected sequence: GetDiagnostic(before), ExecuteDelivery, UpdateState, GetDiagnostic(after)
        int updateIdx  = svc.Sequence.IndexOf("UpdateState");
        int afterDiagIdx = svc.Sequence.LastIndexOf("GetDiagnostic");
        Assert.True(updateIdx >= 0,   "UpdateState must have been called");
        Assert.True(afterDiagIdx >= 0, "GetDiagnostic(after) must have been called");
        Assert.True(updateIdx < afterDiagIdx,
            $"UpdateState (pos {updateIdx}) must precede GetDiagnostic(after) (pos {afterDiagIdx})");
    }

    [Fact]
    public async Task S4_Retry_Idempotent_AlreadyDelivered_ReturnsActionNotAvailable()
    {
        // Diagnostic is in Delivered state with RETRY_INVOICE available but NOT RETRY_DELIVERY
        var svc = MakeS(DiagDeliveredNoRetry(930));

        var result = await svc.RetryDeliveryAsync(
            930, "admin", OrchestrationState.Accepted,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ZfAdminActionError.ActionNotAvailable, result.ErrorCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // T1-T5 helpers
    private static SoLineFragmentRecord MakeFragment(long id, string whsCode = "WHS-A") =>
        new() { Id = id, WhsCode = whsCode, ItemCode = "ITEM-1" };

    private static PickListRecordModel MakePickRecord(
        long fragmentId, string whsCode = "WHS-A", string status = PickListStatus.Picked) =>
        new()
        {
            SoLineFragmentId = fragmentId,
            WhsCode          = whsCode,
            Status           = status,
        };

    private static DeliveryFragmentGateData MakeGateData(
        SoLineFragmentRecord? frag = null,
        PickListRecordModel?  plr  = null)
    {
        var f = frag ?? MakeFragment(id: 1);
        var p = plr  ?? MakePickRecord(fragmentId: 1);
        return new DeliveryFragmentGateData
        {
            Fragment       = f,
            PickListRecord = p,
        };
    }

    // R tests — stale-fragment diagnostic (fragments/deliveries/activeReplan overridable)
    private static ZfOrderDiagnosticResult DiagStaleFragment(
        int soDocEntry,
        IReadOnlyList<ZfDeliveryDiagnostic>? deliveries   = null,
        ZfReplanDiagnostic?                  activeReplan = null,
        IReadOnlyList<ZfFragmentDiagnostic>? fragments    = null)
    {
        fragments ??= [new ZfFragmentDiagnostic(
            10, 1, "ITEM-A", "WHS-OLD",
            5m, 5m, 0m, null, null, null,
            "WHS-NEW", "O", 5m, false,
            "WHS-NEW", 77, 5m, "Picked",
            5m, null, "BIN-01", "WHS-NEW")];

        return new ZfOrderDiagnosticResult
        {
            OrchestrationId  = 5,
            RequestId        = Guid.NewGuid(),
            SoDocEntry       = soDocEntry,
            State            = OrchestrationState.Accepted,
            Fragments        = fragments,
            Deliveries       = deliveries ?? [],
            Invoices         = [],
            ActiveReplan     = activeReplan,
            Consistency      = new ZfConsistencyVerdict(
                ZfConsistencyStatus.StaleFragmentAfterValidPick, null, true, false, false, false),
            AvailableActions = [
                new ZfAvailableAction("RECONCILE_STALE_FRAGMENT_AFTER_VALID_PICK",
                    Enabled: true, MutationAvailable: true, Reason: "Phase 2")
            ],
        };
    }

    // Completed diagnostic (no RECONCILE action)
    private static ZfOrderDiagnosticResult DiagCompleted(int soDocEntry) =>
        new()
        {
            OrchestrationId  = 2,
            RequestId        = Guid.NewGuid(),
            SoDocEntry       = soDocEntry,
            State            = OrchestrationState.Accepted,
            Fragments        = [],
            Deliveries       = [new ZfDeliveryDiagnostic(1, DeliveryRecordStatus.Created,
                99, 1001, null, DateTime.UtcNow, DateTime.UtcNow, "O", "N", "BP01")],
            Invoices         = [new ZfInvoiceDiagnostic(1, InvoiceRecordStatus.Created,
                99, 200, 201, null, DateTime.UtcNow)],
            Consistency      = new ZfConsistencyVerdict(
                ZfConsistencyStatus.Completed, null, false, false, false, false),
            AvailableActions = [],
        };

    // S tests — retry delivery diagnostic
    private static ZfOrderDiagnosticResult DiagRetryDeliveryAvailable(int soDocEntry) =>
        new()
        {
            OrchestrationId  = 6,
            RequestId        = Guid.NewGuid(),
            SoDocEntry       = soDocEntry,
            State            = OrchestrationState.Accepted,
            Fragments        = [],
            Deliveries       = [],   // no successful delivery yet
            Invoices         = [],
            Consistency      = new ZfConsistencyVerdict(
                ZfConsistencyStatus.PhysicalPickStarted, null, false, false, true, false),
            AvailableActions = [
                new ZfAvailableAction("RETRY_DELIVERY", Enabled: true, MutationAvailable: true,
                    Reason: "Phase 1B")
            ],
        };

    private static ZfOrderDiagnosticResult DiagDeliveredNoRetry(int soDocEntry) =>
        new()
        {
            OrchestrationId  = 7,
            RequestId        = Guid.NewGuid(),
            SoDocEntry       = soDocEntry,
            State            = OrchestrationState.Accepted,
            Fragments        = [],
            Deliveries       = [new ZfDeliveryDiagnostic(1, DeliveryRecordStatus.Created,
                55, 100, null, DateTime.UtcNow, DateTime.UtcNow, "O", "N", "BP01")],
            Invoices         = [],
            Consistency      = new ZfConsistencyVerdict(
                ZfConsistencyStatus.Delivered, null, false, false, false, true),
            AvailableActions = [
                new ZfAvailableAction("RETRY_INVOICE", Enabled: true, MutationAvailable: true,
                    Reason: "Phase 1B")
            ],
        };

    private static ZfDeliveryResult BlockedDelivery() =>
        new()
        {
            Preflight = new DeliveryPreflightResult
            {
                RequestId           = Guid.Empty,
                Orchestration       = new FulfillmentOrchestrationRecord(),
                SoUdfs              = null,
                Fragments           = new List<DeliveryFragmentGateData>(),
                InvoiceFilterActive = false,
                GateErrors          = new List<string> { "GATE_BLOCKED" },
            },
            GateVerdict = "BLOCKED",
        };

    private static ZfDeliveryResult SuccessfulDelivery(int odlnDocEntry) =>
        new()
        {
            Preflight = new DeliveryPreflightResult
            {
                RequestId           = Guid.Empty,
                Orchestration       = new FulfillmentOrchestrationRecord(),
                SoUdfs              = null,
                Fragments           = new List<DeliveryFragmentGateData>(),
                InvoiceFilterActive = false,
                GateErrors          = new List<string>(),
            },
            GateVerdict = "OK",
            Odln = new OdlnReadback
            {
                DocEntry  = odlnDocEntry,
                DocNum    = odlnDocEntry,
                DocStatus = "O",
                Canceled  = "N",
                CardCode  = "BP01",
            },
        };

    // Factory helpers for R tests
    private static TestableZfAdminActionService MakeR(
        ZfOrderDiagnosticResult? diagnostic,
        InMemoryAuditStore?      auditStore    = null,
        int                      reconcileRows = 1)
        => new(
            diagnostic:      diagnostic,
            auditStore:      auditStore ?? new InMemoryAuditStore(),
            replanResult:    OrderEditResult.Success(),
            onExecuteReplan: null,
            delivResult:     BlockedDelivery(),
            invResult:       new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_PreflightBlocked,
                GateErrors = new List<string> { "NO_INVOICE" },
            },
            reconcileRows: reconcileRows);

    // Factory for S tests
    private static TestableZfAdminActionService MakeS(
        ZfOrderDiagnosticResult? diagnostic = null,
        ZfDeliveryResult?        delivResult = null)
        => new(
            diagnostic:      diagnostic ?? DiagRetryDeliveryAvailable(900),
            auditStore:      new InMemoryAuditStore(),
            replanResult:    OrderEditResult.Success(),
            onExecuteReplan: null,
            delivResult:     delivResult ?? BlockedDelivery(),
            invResult:       new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_PreflightBlocked,
                GateErrors = new List<string> { "NO_INVOICE" },
            });
}

// ── Call-sequence tracking service (for S3) ───────────────────────────────────

/// <summary>
/// Minimal ZfAdminActionService override that records the sequence of virtual
/// method calls so S3 can verify UpdateState happens before GetDiagnostic(after).
/// </summary>
internal sealed class CallSequenceRetryService : ZfAdminActionService
{
    private readonly ZfOrderDiagnosticResult? _diag;
    private readonly ZfDeliveryResult         _delivResult;

    public List<string> Sequence { get; } = new();

    public CallSequenceRetryService(
        ZfOrderDiagnosticResult? diag,
        ZfDeliveryResult         delivResult)
        : base(
            diagnostic:      null!,
            audit:           new InMemoryAuditStore(),
            coordinator:     null!,
            deliveryService: null!,
            invoiceService:  null!,
            repo:            null!,
            log:             NullLogger<ZfAdminActionService>.Instance)
    {
        _diag        = diag;
        _delivResult = delivResult;
    }

    protected override Task<ZfOrderDiagnosticResult?> GetDiagnosticAsync(
        int soDocEntry, CancellationToken ct)
    {
        Sequence.Add("GetDiagnostic");
        return Task.FromResult(_diag);
    }

    protected override Task<ZfDeliveryResult> ExecuteDelivery(
        Guid requestId, CancellationToken ct)
    {
        Sequence.Add("ExecuteDelivery");
        return Task.FromResult(_delivResult);
    }

    protected override Task ExecuteUpdateStateDeliveredAsync(
        long orchestrationId, CancellationToken ct)
    {
        Sequence.Add("UpdateState");
        return Task.CompletedTask;
    }

    protected override Task<int> ExecuteReconcileFragmentAsync(
        long fragmentId, string priorWhs, string newWhs, string changedBy, CancellationToken ct)
        => Task.FromResult(1);
}
