using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Pure classification logic — no I/O, no DI.
/// Given a ZfClassifierInput snapshot, returns the consistency verdict.
///
/// Fragment mismatch rules (Correction 1 from Phase 1A approval):
///
/// A. ZF_REPLAN_AVAILABLE (AMBER pre-pick)
///    Fragment WHS ≠ RDR1 WHS
///    AND SapPkl1PickQtty = 0
///    AND no active ODLN
///    Physical picking has NOT started. Safe to replan.
///
/// B. ZF_STALE_FRAGMENT_WHS_AFTER_VALID_PICK (post-pick metadata mismatch)
///    Fragment WHS ≠ RDR1 WHS
///    AND SapPkl1PickQtty > 0
///    AND PickListRecordWhsCode == SapRdr1WhsCode  (PLR matches SAP truth)
///    AND SapPkl2BinWhsCode     == SapRdr1WhsCode  (physical bin is in the correct warehouse)
///    Fragment metadata is the sole inconsistency. NOT a replan. SAP pick state is valid.
///
/// C. ZF_FRAG_WHS_MISMATCH (BLOCKED)
///    Fragment WHS ≠ RDR1 WHS
///    AND SapPkl1PickQtty > 0
///    AND evidence is conflicting (PLR or bin WHS doesn't match RDR1)
///    Requires manual investigation.
///
/// DELIVERED vs COMPLETED (Correction 2):
///    DELIVERED  = successful ODLN exists, no successful invoice yet
///    COMPLETED  = successful ODLN + successful invoice both exist
/// </summary>
public static class ZfConsistencyClassifier
{
    public static ZfConsistencyVerdict Classify(ZfClassifierInput input)
    {
        var state   = input.OrchState;
        var replan  = input.ActiveReplan;

        // ── 1. Terminal states ──────────────────────────────────────────────
        if (state == OrchestrationState.Canceled)
            return V(ZfConsistencyStatus.Canceled);

        if (state == OrchestrationState.Failed)
            return V(ZfConsistencyStatus.Failed,
                input.FailureKind is not null ? $"FailureKind={input.FailureKind}" : null);

        if (state == OrchestrationState.UnknownOutcome)
            return V(ZfConsistencyStatus.OrderStateMismatch, "State=UnknownOutcome");

        // ── 2. Pre-accepted: waiting for SO creation ─────────────────────
        if (state is OrchestrationState.Received
                  or OrchestrationState.Validating
                  or OrchestrationState.Allocating
                  or OrchestrationState.CreatingSalesOrder
                  or OrchestrationState.SalesOrderCreated)
            return V(ZfConsistencyStatus.Waiting, $"State={state}");

        // ── 3. Active replan ──────────────────────────────────────────────
        if (replan is not null)
        {
            if (replan.CurrentStep == ReplanStep.RecoveryRequired)
                return V(ZfConsistencyStatus.ReplanRecoveryRequired,
                    $"OperationId={replan.OperationId} LastError={replan.LastError}",
                    hasActiveReplan: true);

            if (replan.CurrentStep != ReplanStep.Completed
             && replan.CurrentStep != ReplanStep.FailedBeforeMutation)
                return V(ZfConsistencyStatus.ReplanInProgress,
                    $"OperationId={replan.OperationId} Step={replan.CurrentStep}",
                    hasActiveReplan: true);
        }

        // ── 4. Completeness check (for Accepted / Delivered) ─────────────
        bool hasSuccessfulDelivery =
            input.Deliveries.Any(d => d.Status == DeliveryRecordStatus.Created);

        bool hasDeliveryFailure =
            input.Deliveries.Any(d => d.Status == DeliveryRecordStatus.Failed);

        if (hasSuccessfulDelivery && input.HasSuccessfulInvoice)
            return V(ZfConsistencyStatus.Completed);

        if (hasSuccessfulDelivery && !input.HasSuccessfulInvoice)
            return V(ZfConsistencyStatus.Delivered, "ODLN created; invoice pending",
                invoicePending: true);

        // ── 5. Fragment WHS analysis (Accepted state, no delivery yet) ────
        if (state == OrchestrationState.Accepted)
        {
            var mismatch = FindFirstMismatch(input.Fragments);
            if (mismatch is not null)
            {
                var (frag, verdict) = mismatch.Value;
                return V(verdict,
                    $"LineNum={frag.SoLineNum} " +
                    $"FragWhs={frag.FragmentWhsCode} RDR1Whs={frag.SapRdr1WhsCode} " +
                    $"PlrWhs={frag.PickListRecordWhsCode} Pkl1PickQtty={frag.SapPkl1PickQtty} " +
                    $"Pkl2BinWhs={frag.SapPkl2BinWhsCode}",
                    hasFragmentMismatch: true,
                    hasDeliveryFailure: hasDeliveryFailure);
            }

            // ── 6. All fragments consistent with SAP; check pick progress ──
            bool anyPicked   = input.Fragments.Any(f => f.SapPkl1PickQtty > 0);
            bool allNoPickList = input.Fragments.All(f => f.PickListRecordWhsCode is null);

            if (allNoPickList)
                return V(ZfConsistencyStatus.AwaitingPick, "No pick lists assigned");

            if (anyPicked)
                return V(ZfConsistencyStatus.PhysicalPickStarted,
                    null, hasDeliveryFailure: hasDeliveryFailure);

            if (hasDeliveryFailure)
                return V(ZfConsistencyStatus.DeliveryFailedRetry,
                    "All pick lists at PickQtty=0 yet delivery failed",
                    hasDeliveryFailure: true);

            return V(ZfConsistencyStatus.AwaitingPick);
        }

        // ── 7. Delivered orchestration state (SAP-side) ───────────────────
        if (state == OrchestrationState.Delivered)
        {
            if (input.HasSuccessfulInvoice)
                return V(ZfConsistencyStatus.Completed);

            return V(ZfConsistencyStatus.Delivered, "State=Delivered; invoice pending",
                invoicePending: true);
        }

        return V(ZfConsistencyStatus.Unknown, $"Unhandled state={state}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (ZfFragmentClassInput Frag, string Verdict)?
        FindFirstMismatch(IReadOnlyList<ZfFragmentClassInput> fragments)
    {
        foreach (var f in fragments)
        {
            if (f.SapRdr1WhsCode is null) continue;
            if (string.Equals(f.FragmentWhsCode, f.SapRdr1WhsCode, StringComparison.OrdinalIgnoreCase))
                continue;

            // WHS mismatch found
            if (f.SapPkl1PickQtty == 0)
            {
                // No physical pick started → AMBER REPLAN
                return (f, ZfConsistencyStatus.ReplanAvailable);
            }
            else
            {
                // Physical pick exists — check evidence for valid vs. conflicting
                bool plrMatchesRdr1 =
                    f.PickListRecordWhsCode is not null &&
                    string.Equals(f.PickListRecordWhsCode, f.SapRdr1WhsCode,
                        StringComparison.OrdinalIgnoreCase);

                bool binMatchesRdr1 =
                    f.SapPkl2BinWhsCode is not null &&
                    string.Equals(f.SapPkl2BinWhsCode, f.SapRdr1WhsCode,
                        StringComparison.OrdinalIgnoreCase);

                if (plrMatchesRdr1 && binMatchesRdr1)
                {
                    // All three live sources (RDR1, PLR, physical bin) agree.
                    // Only fragment metadata is wrong → stale fragment, not a replan.
                    return (f, ZfConsistencyStatus.StaleFragmentAfterValidPick);
                }
                else
                {
                    // Conflicting evidence → cannot safely auto-repair
                    return (f, ZfConsistencyStatus.FragWhsMismatch);
                }
            }
        }

        return null;
    }

    private static ZfConsistencyVerdict V(
        string  status,
        string? detail            = null,
        bool    hasFragmentMismatch = false,
        bool    hasActiveReplan   = false,
        bool    hasDeliveryFailure = false,
        bool    invoicePending    = false)
        => new(status, detail, hasFragmentMismatch, hasActiveReplan, hasDeliveryFailure, invoicePending);

    // ── Available action recommendations ─────────────────────────────────────

    private const string Phase1BAvailable = "Mutation available via Phase 1B admin endpoint.";
    private const string Phase2Available  = "Mutation available via Phase 2 admin endpoint.";

    public static IReadOnlyList<ZfAvailableAction> RecommendActions(
        ZfConsistencyVerdict verdict)
    {
        var list = new List<ZfAvailableAction>();

        switch (verdict.Status)
        {
            case ZfConsistencyStatus.ReplanAvailable:
                list.Add(new ZfAvailableAction(
                    "REPLAN_RELEASED_ORDER",
                    Enabled: true,
                    MutationAvailable: true,
                    Phase1BAvailable));
                break;

            case ZfConsistencyStatus.StaleFragmentAfterValidPick:
                list.Add(new ZfAvailableAction(
                    "RECONCILE_STALE_FRAGMENT_AFTER_VALID_PICK",
                    Enabled: true,
                    MutationAvailable: true,
                    Phase2Available));
                break;

            case ZfConsistencyStatus.ReplanRecoveryRequired:
                list.Add(new ZfAvailableAction(
                    "RESUME_REPLAN",
                    Enabled: true,
                    MutationAvailable: true,
                    Phase1BAvailable));
                break;

            case ZfConsistencyStatus.DeliveryFailedRetry:
                list.Add(new ZfAvailableAction(
                    "RETRY_DELIVERY",
                    Enabled: true,
                    MutationAvailable: true,
                    Phase1BAvailable));
                break;

            case ZfConsistencyStatus.Delivered:
                list.Add(new ZfAvailableAction(
                    "RETRY_INVOICE",
                    Enabled: true,
                    MutationAvailable: true,
                    Phase1BAvailable));
                break;

            case ZfConsistencyStatus.FragWhsMismatch:
                list.Add(new ZfAvailableAction(
                    "MANUAL_INVESTIGATION_REQUIRED",
                    Enabled: false,
                    MutationAvailable: false,
                    "Conflicting SAP evidence — PLR or bin warehouse does not match RDR1. " +
                    "Manual investigation required before any automated action."));
                break;
        }

        return list;
    }
}
