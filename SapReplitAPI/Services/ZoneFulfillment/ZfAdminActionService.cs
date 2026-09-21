using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Phase 1B: ZF Admin controlled-recovery actions.
///
/// Every mutation:
///   1. Re-reads live diagnostic (no stale state).
///   2. Checks expected orchestration state → 409 ZF_STATE_CHANGED if changed.
///   3. Checks action is available and preconditions still hold → 409 ZF_PRECONDITION_FAILED.
///   4. Writes audit record (Result=Pending) BEFORE the mutation executes.
///   5. Executes via existing production service (no duplicate SAP logic here).
///   6. Updates audit record with terminal result and post-state snapshot.
///
/// Not sealed — virtual executor methods allow test subclasses to inject
/// fake mutation results without modifying production services.
/// </summary>
public class ZfAdminActionService
{
    private readonly ZfAdminDiagnosticService            _diagnostic;
    private readonly ZfAdminAuditRepository              _audit;
    private readonly IZoneFulfillmentOrderEditCoordinator _coordinator;
    private readonly ZoneFulfillmentDeliveryService      _deliveryService;
    private readonly ZoneFulfillmentInvoiceService       _invoiceService;
    private readonly ILogger<ZfAdminActionService>       _log;

    public ZfAdminActionService(
        ZfAdminDiagnosticService             diagnostic,
        ZfAdminAuditRepository               audit,
        IZoneFulfillmentOrderEditCoordinator  coordinator,
        ZoneFulfillmentDeliveryService        deliveryService,
        ZoneFulfillmentInvoiceService         invoiceService,
        ILogger<ZfAdminActionService>         log)
    {
        _diagnostic      = diagnostic;
        _audit           = audit;
        _coordinator     = coordinator;
        _deliveryService = deliveryService;
        _invoiceService  = invoiceService;
        _log             = log;
    }

    // ── REFRESH_STATE (read-only) ─────────────────────────────────────────────

    public async Task<ZfAdminActionResult> RefreshStateAsync(
        int soDocEntry, string requestedBy, CancellationToken ct)
    {
        var diag = await GetDiagnosticAsync(soDocEntry, ct);
        if (diag is null)
            return ErrorResult(ZfAdminActionType.RefreshState, Guid.NewGuid(),
                ZfAdminActionError.OrchestrationNotFound,
                $"No ZF orchestration found for SoDocEntry={soDocEntry}.");

        return new ZfAdminActionResult
        {
            IsSuccess  = true,
            ActionType = ZfAdminActionType.RefreshState,
            ActionId   = Guid.NewGuid(),
            AfterState = diag,
        };
    }

    // ── REPLAN_RELEASED ───────────────────────────────────────────────────────

    public async Task<ZfAdminActionResult> ReplanReleasedAsync(
        int soDocEntry, string requestedBy, string expectedOrchState, CancellationToken ct)
    {
        var actionId = Guid.NewGuid();
        var now      = DateTime.UtcNow;

        var before = await GetDiagnosticAsync(soDocEntry, ct);
        if (before is null)
            return ErrorResult(ZfAdminActionType.ReplanReleased, actionId,
                ZfAdminActionError.OrchestrationNotFound,
                $"No ZF orchestration for SoDocEntry={soDocEntry}.");

        // State-change guard
        if (!string.Equals(before.State, expectedOrchState, StringComparison.OrdinalIgnoreCase))
            return ErrorResult(ZfAdminActionType.ReplanReleased, actionId,
                ZfAdminActionError.StateChanged,
                $"Orchestration state changed: expected={expectedOrchState} live={before.State}.",
                before);

        // Action availability + precondition check
        if (!IsActionEnabled(before, "REPLAN_RELEASED_ORDER"))
            return ErrorResult(ZfAdminActionType.ReplanReleased, actionId,
                ZfAdminActionError.ActionNotAvailable,
                "Action REPLAN_RELEASED_ORDER is not available in current state: " +
                $"{before.Consistency.Status}.", before);

        // Hard precondition: zero physical picks and no delivery (Rule: Section REPLAN_RELEASED)
        bool anyPicked = before.Fragments.Any(f => f.SapPkl1PickQtty > 0);
        bool hasDelivery = before.Deliveries.Any(d => d.Status == DeliveryRecordStatus.Created);
        if (anyPicked || hasDelivery)
            return ErrorResult(ZfAdminActionType.ReplanReleased, actionId,
                ZfAdminActionError.PreconditionFailed,
                "REPLAN_RELEASED requires zero physical picks and no active delivery. " +
                $"AnyPicked={anyPicked} HasDelivery={hasDelivery}.", before);

        // Write audit BEFORE mutation
        var entry = new ZfAdminAuditEntry
        {
            ActionId        = actionId,
            SoDocEntry      = soDocEntry,
            RequestId       = before.RequestId,
            ActionType      = ZfAdminActionType.ReplanReleased,
            RequestedBy     = requestedBy,
            RequestedAtUtc  = now,
            Result          = ZfAdminAuditResult.Pending,
            BeforeStateJson = ZfAdminAuditRepository.SerializeSnapshot(before),
        };
        var auditId = await _audit.InsertAsync(entry, ct);

        // Execute via coordinator (same path as 17/U AMBER handler)
        string operationLabel = $"zf-admin:{actionId}";
        ZfAdminActionResult result;
        try
        {
            var editResult = await ExecuteReplan(soDocEntry, operationLabel, $"zf-admin:{requestedBy}", ct);

            if (editResult.IsNotZf || editResult.IsBlocked)
            {
                result = ErrorResult(ZfAdminActionType.ReplanReleased, actionId,
                    ZfAdminActionError.PreconditionFailed,
                    $"Coordinator blocked: Code={editResult.BlockCode} {editResult.BlockReason}", before);
            }
            else if (editResult.IsRecoveryRequired)
            {
                result = new ZfAdminActionResult
                {
                    IsSuccess         = false,
                    ActionType        = ZfAdminActionType.ReplanReleased,
                    ActionId          = actionId,
                    ErrorCode         = ZfAdminActionError.RecoveryRequired,
                    ErrorMessage      = $"Partial mutation — RecoveryRequired. OperationId={editResult.ReplanOperationId}",
                    BeforeState       = before,
                    ReplanOperationId = editResult.ReplanOperationId,
                };
            }
            else
            {
                var after = await GetDiagnosticAsync(soDocEntry, ct);
                result = new ZfAdminActionResult
                {
                    IsSuccess         = true,
                    ActionType        = ZfAdminActionType.ReplanReleased,
                    ActionId          = actionId,
                    BeforeState       = before,
                    AfterState        = after,
                    ReplanOperationId = editResult.ReplanOperationId,
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZfAdmin] REPLAN_RELEASED execution error SoDocEntry={So}", soDocEntry);
            result = ErrorResult(ZfAdminActionType.ReplanReleased, actionId,
                ZfAdminActionError.ExecutionFailed, ex.Message, before);
        }

        await FinalizeAudit(auditId, result, ct);
        return result;
    }

    // ── RESUME_REPLAN ─────────────────────────────────────────────────────────

    public async Task<ZfAdminActionResult> ResumeReplanAsync(
        int soDocEntry, Guid operationId, string requestedBy,
        string expectedOrchState, CancellationToken ct)
    {
        var actionId = Guid.NewGuid();
        var now      = DateTime.UtcNow;

        var before = await GetDiagnosticAsync(soDocEntry, ct);
        if (before is null)
            return ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                ZfAdminActionError.OrchestrationNotFound,
                $"No ZF orchestration for SoDocEntry={soDocEntry}.");

        if (!string.Equals(before.State, expectedOrchState, StringComparison.OrdinalIgnoreCase))
            return ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                ZfAdminActionError.StateChanged,
                $"Orchestration state changed: expected={expectedOrchState} live={before.State}.", before);

        if (!IsActionEnabled(before, "RESUME_REPLAN"))
            return ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                ZfAdminActionError.ActionNotAvailable,
                "Action RESUME_REPLAN is not available in current state: " +
                $"{before.Consistency.Status}.", before);

        // Hard precondition: the specific operationId must be in RecoveryRequired
        var activeReplan = before.ActiveReplan;
        if (activeReplan is null || activeReplan.OperationId != operationId)
            return ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                ZfAdminActionError.PreconditionFailed,
                $"No active RecoveryRequired replan with OperationId={operationId}.", before);

        if (activeReplan.CurrentStep != ReplanStep.RecoveryRequired)
            return ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                ZfAdminActionError.PreconditionFailed,
                $"Replan OperationId={operationId} is in step={activeReplan.CurrentStep}, " +
                "not RecoveryRequired.", before);

        var entry = new ZfAdminAuditEntry
        {
            ActionId        = actionId,
            SoDocEntry      = soDocEntry,
            RequestId       = before.RequestId,
            ActionType      = ZfAdminActionType.ResumeReplan,
            ReasonCode      = operationId.ToString(),
            RequestedBy     = requestedBy,
            RequestedAtUtc  = now,
            Result          = ZfAdminAuditResult.Pending,
            BeforeStateJson = ZfAdminAuditRepository.SerializeSnapshot(before),
        };
        var auditId = await _audit.InsertAsync(entry, ct);

        ZfAdminActionResult result;
        try
        {
            var editResult = await ExecuteResume(operationId, ct);

            if (editResult.IsBlocked || editResult.IsNotZf)
            {
                result = ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                    ZfAdminActionError.PreconditionFailed,
                    $"Coordinator blocked on resume: Code={editResult.BlockCode} {editResult.BlockReason}", before);
            }
            else if (editResult.IsRecoveryRequired)
            {
                result = new ZfAdminActionResult
                {
                    IsSuccess         = false,
                    ActionType        = ZfAdminActionType.ResumeReplan,
                    ActionId          = actionId,
                    ErrorCode         = ZfAdminActionError.RecoveryRequired,
                    ErrorMessage      = $"Partial mutation — still RecoveryRequired. OperationId={operationId}",
                    BeforeState       = before,
                    ReplanOperationId = operationId,
                };
            }
            else
            {
                var after = await GetDiagnosticAsync(soDocEntry, ct);
                result = new ZfAdminActionResult
                {
                    IsSuccess         = true,
                    ActionType        = ZfAdminActionType.ResumeReplan,
                    ActionId          = actionId,
                    BeforeState       = before,
                    AfterState        = after,
                    ReplanOperationId = operationId,
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZfAdmin] RESUME_REPLAN error SoDocEntry={So} OpId={Op}", soDocEntry, operationId);
            result = ErrorResult(ZfAdminActionType.ResumeReplan, actionId,
                ZfAdminActionError.ExecutionFailed, ex.Message, before);
        }

        await FinalizeAudit(auditId, result, ct);
        return result;
    }

    // ── RETRY_DELIVERY ────────────────────────────────────────────────────────

    public async Task<ZfAdminActionResult> RetryDeliveryAsync(
        int soDocEntry, string requestedBy, string expectedOrchState, CancellationToken ct)
    {
        var actionId = Guid.NewGuid();
        var now      = DateTime.UtcNow;

        var before = await GetDiagnosticAsync(soDocEntry, ct);
        if (before is null)
            return ErrorResult(ZfAdminActionType.RetryDelivery, actionId,
                ZfAdminActionError.OrchestrationNotFound,
                $"No ZF orchestration for SoDocEntry={soDocEntry}.");

        if (!string.Equals(before.State, expectedOrchState, StringComparison.OrdinalIgnoreCase))
            return ErrorResult(ZfAdminActionType.RetryDelivery, actionId,
                ZfAdminActionError.StateChanged,
                $"Orchestration state changed: expected={expectedOrchState} live={before.State}.", before);

        if (!IsActionEnabled(before, "RETRY_DELIVERY"))
            return ErrorResult(ZfAdminActionType.RetryDelivery, actionId,
                ZfAdminActionError.ActionNotAvailable,
                "Action RETRY_DELIVERY is not available in current state: " +
                $"{before.Consistency.Status}.", before);

        // Idempotency: no successful delivery must already exist
        if (before.Deliveries.Any(d => d.Status == DeliveryRecordStatus.Created))
            return ErrorResult(ZfAdminActionType.RetryDelivery, actionId,
                ZfAdminActionError.PreconditionFailed,
                "A successful delivery (Status=Created) already exists — no retry needed.", before);

        var entry = new ZfAdminAuditEntry
        {
            ActionId        = actionId,
            SoDocEntry      = soDocEntry,
            RequestId       = before.RequestId,
            ActionType      = ZfAdminActionType.RetryDelivery,
            RequestedBy     = requestedBy,
            RequestedAtUtc  = now,
            Result          = ZfAdminAuditResult.Pending,
            BeforeStateJson = ZfAdminAuditRepository.SerializeSnapshot(before),
        };
        var auditId = await _audit.InsertAsync(entry, ct);

        ZfAdminActionResult result;
        try
        {
            var delivResult = await ExecuteDelivery(before.RequestId, ct);
            var after       = await GetDiagnosticAsync(soDocEntry, ct);

            // Gate verdict: GATE_ERRORS = blocked
            bool gateBlocked = delivResult.Preflight.GateErrors.Count > 0;
            bool hasNewOdln  = delivResult.Odln is not null;

            if (gateBlocked && !hasNewOdln)
            {
                result = ErrorResult(ZfAdminActionType.RetryDelivery, actionId,
                    ZfAdminActionError.PreconditionFailed,
                    $"Delivery gate blocked: {string.Join("; ", delivResult.Preflight.GateErrors)}", before);
            }
            else
            {
                result = new ZfAdminActionResult
                {
                    IsSuccess      = hasNewOdln,
                    ActionType     = ZfAdminActionType.RetryDelivery,
                    ActionId       = actionId,
                    BeforeState    = before,
                    AfterState     = after,
                    NewOdlnDocEntry = delivResult.Odln?.DocEntry,
                    ErrorCode      = hasNewOdln ? null : ZfAdminActionError.ExecutionFailed,
                    ErrorMessage   = hasNewOdln ? null :
                        $"Gate={delivResult.GateVerdict} " +
                        string.Join("; ", delivResult.Preflight.GateErrors),
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZfAdmin] RETRY_DELIVERY error SoDocEntry={So}", soDocEntry);
            result = ErrorResult(ZfAdminActionType.RetryDelivery, actionId,
                ZfAdminActionError.ExecutionFailed, ex.Message, before);
        }

        await FinalizeAudit(auditId, result, ct);
        return result;
    }

    // ── RETRY_INVOICE ─────────────────────────────────────────────────────────

    public async Task<ZfAdminActionResult> RetryInvoiceAsync(
        int soDocEntry, int deliveryDocEntry, string requestedBy,
        string expectedOrchState, CancellationToken ct)
    {
        var actionId = Guid.NewGuid();
        var now      = DateTime.UtcNow;

        var before = await GetDiagnosticAsync(soDocEntry, ct);
        if (before is null)
            return ErrorResult(ZfAdminActionType.RetryInvoice, actionId,
                ZfAdminActionError.OrchestrationNotFound,
                $"No ZF orchestration for SoDocEntry={soDocEntry}.");

        if (!string.Equals(before.State, expectedOrchState, StringComparison.OrdinalIgnoreCase))
            return ErrorResult(ZfAdminActionType.RetryInvoice, actionId,
                ZfAdminActionError.StateChanged,
                $"Orchestration state changed: expected={expectedOrchState} live={before.State}.", before);

        if (!IsActionEnabled(before, "RETRY_INVOICE"))
            return ErrorResult(ZfAdminActionType.RetryInvoice, actionId,
                ZfAdminActionError.ActionNotAvailable,
                "Action RETRY_INVOICE is not available in current state: " +
                $"{before.Consistency.Status}.", before);

        // Hard precondition: delivery exists and invoice is genuinely absent
        var delivRecord = before.Deliveries.FirstOrDefault(d =>
            d.Status == DeliveryRecordStatus.Created && d.SapDocEntry == deliveryDocEntry);
        if (delivRecord is null)
            return ErrorResult(ZfAdminActionType.RetryInvoice, actionId,
                ZfAdminActionError.PreconditionFailed,
                $"No successful DeliveryRecord for DocEntry={deliveryDocEntry}.", before);

        bool invoiceAlreadyExists = before.Invoices.Any(i =>
            i.Status == InvoiceRecordStatus.Created && i.DeliveryDocEntry == deliveryDocEntry);
        if (invoiceAlreadyExists)
            return ErrorResult(ZfAdminActionType.RetryInvoice, actionId,
                ZfAdminActionError.PreconditionFailed,
                $"Invoice already exists for DeliveryDocEntry={deliveryDocEntry}.", before);

        var entry = new ZfAdminAuditEntry
        {
            ActionId        = actionId,
            SoDocEntry      = soDocEntry,
            RequestId       = before.RequestId,
            ActionType      = ZfAdminActionType.RetryInvoice,
            ReasonCode      = $"DeliveryDocEntry={deliveryDocEntry}",
            RequestedBy     = requestedBy,
            RequestedAtUtc  = now,
            Result          = ZfAdminAuditResult.Pending,
            BeforeStateJson = ZfAdminAuditRepository.SerializeSnapshot(before),
        };
        var auditId = await _audit.InsertAsync(entry, ct);

        ZfAdminActionResult result;
        try
        {
            var invResult = await ExecuteInvoice(before.RequestId, ct);
            var after     = await GetDiagnosticAsync(soDocEntry, ct);

            bool success = invResult.Verdict == ZfInvoiceExecuteResult.V_OinvCreated
                        || invResult.Verdict == ZfInvoiceExecuteResult.V_AlreadyCreated
                        || invResult.Verdict == ZfInvoiceExecuteResult.V_RecoveredFromSap;

            result = new ZfAdminActionResult
            {
                IsSuccess      = success,
                ActionType     = ZfAdminActionType.RetryInvoice,
                ActionId       = actionId,
                BeforeState    = before,
                AfterState     = after,
                NewOinvDocEntry = invResult.InvoiceDocEntry,
                ErrorCode      = success ? null : ZfAdminActionError.ExecutionFailed,
                ErrorMessage   = success ? null :
                    $"Verdict={invResult.Verdict} " +
                    string.Join("; ", invResult.GateErrors),
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZfAdmin] RETRY_INVOICE error SoDocEntry={So}", soDocEntry);
            result = ErrorResult(ZfAdminActionType.RetryInvoice, actionId,
                ZfAdminActionError.ExecutionFailed, ex.Message, before);
        }

        await FinalizeAudit(auditId, result, ct);
        return result;
    }

    // ── Virtual diagnostic accessor (overridable in tests) ───────────────────

    protected virtual Task<ZfOrderDiagnosticResult?> GetDiagnosticAsync(
        int soDocEntry, CancellationToken ct)
        => _diagnostic!.GetOrderDiagnosticAsync(soDocEntry, ct);

    // ── Virtual executor methods (overridable in tests) ───────────────────────

    protected virtual Task<OrderEditResult> ExecuteReplan(
        int soDocEntry, string eventId, string changedBy, CancellationToken ct)
        => _coordinator.ReconcileExternalSapEditAsync(soDocEntry, eventId, changedBy, ct);

    protected virtual Task<OrderEditResult> ExecuteResume(
        Guid operationId, CancellationToken ct)
        => _coordinator.ResumeReplanAsync(operationId, ct);

    protected virtual Task<ZfDeliveryResult> ExecuteDelivery(
        Guid requestId, CancellationToken ct)
        => _deliveryService.ExecuteDeliveryAsync(requestId, ct);

    protected virtual Task<ZfInvoiceExecuteResult> ExecuteInvoice(
        Guid requestId, CancellationToken ct)
        => _invoiceService.ExecuteInvoiceAsync(requestId, ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsActionEnabled(ZfOrderDiagnosticResult diag, string code)
        => diag.AvailableActions.Any(a =>
            string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase) && a.Enabled);

    private static ZfAdminActionResult ErrorResult(
        string actionType, Guid actionId,
        string errorCode, string errorMessage,
        ZfOrderDiagnosticResult? before = null)
        => new()
        {
            IsSuccess    = false,
            ActionType   = actionType,
            ActionId     = actionId,
            ErrorCode    = errorCode,
            ErrorMessage = errorMessage,
            BeforeState  = before,
        };

    private async Task FinalizeAudit(
        long              auditId,
        ZfAdminActionResult result,
        CancellationToken ct)
    {
        var auditResult = result.IsSuccess
            ? ZfAdminAuditResult.Success
            : result.ErrorCode == ZfAdminActionError.RecoveryRequired
                ? ZfAdminAuditResult.RecoveryRequired
                : result.ErrorCode is ZfAdminActionError.StateChanged
                               or ZfAdminActionError.PreconditionFailed
                               or ZfAdminActionError.ActionNotAvailable
                  ? ZfAdminAuditResult.PreconditionFailed
                  : ZfAdminAuditResult.Failed;

        try
        {
            await _audit.SetPostStateAsync(
                id:             auditId,
                result:         auditResult,
                afterStateJson: ZfAdminAuditRepository.SerializeSnapshot(result.AfterState),
                errorCode:      result.ErrorCode,
                errorMessage:   result.ErrorMessage,
                executedAtUtc:  DateTime.UtcNow,
                ct:             ct);
        }
        catch (Exception ex)
        {
            // Audit finalization is non-fatal — the business result stands
            _log.LogWarning(ex, "[ZfAdmin] Audit finalization failed AuditId={Id}", auditId);
        }
    }
}
