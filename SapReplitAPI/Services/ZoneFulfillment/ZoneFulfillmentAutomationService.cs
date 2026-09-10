using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Post-pick automation: evaluates all-picks-complete after each Confirm Pick,
/// then automatically triggers delivery and invoice when the final pick is confirmed.
///
/// Contract:
///   1. Check all SoLineFragments for this orchestration.
///   2. If any fragment's latest PickListRecord is not Status=Picked → wait.
///   3. If all complete → ZoneFulfillmentDeliveryService.ExecuteDeliveryAsync().
///   4. On delivery success → ZoneFulfillmentInvoiceService.ExecuteInvoiceAsync().
///   Delivery failure is never caused by invoice failure (separate milestones).
/// </summary>
public sealed class ZoneFulfillmentAutomationService : IZfAutomation
{
    private readonly ZoneFulfillmentRepository                 _repo;
    private readonly ZoneFulfillmentDeliveryService            _delivery;
    private readonly ZoneFulfillmentInvoiceService             _invoice;
    private readonly ILogger<ZoneFulfillmentAutomationService> _log;

    public ZoneFulfillmentAutomationService(
        ZoneFulfillmentRepository                  repo,
        ZoneFulfillmentDeliveryService             delivery,
        ZoneFulfillmentInvoiceService              invoice,
        ILogger<ZoneFulfillmentAutomationService>  log)
    {
        _repo     = repo;
        _delivery = delivery;
        _invoice  = invoice;
        _log      = log;
    }

    /// <summary>
    /// Called immediately after a successful Confirm Pick.
    /// Returns without triggering delivery if other warehouses are still pending.
    /// Triggers delivery and returns the result when all picks are confirmed.
    /// Never throws — failures are returned in AutomationStatus.
    /// </summary>
    public async Task<PostPickAutomationResult> EvaluateAndTriggerDeliveryAsync(
        Guid requestId, CancellationToken ct = default)
    {
        try
        {
            return await EvaluateAsync(requestId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[ZF-AUTO] Unhandled error in post-pick automation RequestId={Rid}", requestId);
            return new PostPickAutomationResult
            {
                AutomationStatus = "AutomationError",
                ErrorMessage     = ex.Message
            };
        }
    }

    private async Task<PostPickAutomationResult> EvaluateAsync(
        Guid requestId, CancellationToken ct)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
        {
            _log.LogWarning("[ZF-AUTO] RequestId={Rid} — orchestration not found.", requestId);
            return new PostPickAutomationResult { AutomationStatus = "OrchestrationNotFound" };
        }

        // Load current state from MolasIntegration
        var fragments = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        var pickLists = await _repo.GetPickListRecordsAsync(orch.Id, ct);

        // Latest PLR per fragment (GetPickListRecordsAsync already returns latest-by-Id per fragment)
        var plrByFragId = pickLists.ToDictionary(p => p.SoLineFragmentId);

        var pendingWarehouses = new List<string>();
        foreach (var frag in fragments)
        {
            if (!plrByFragId.TryGetValue(frag.Id, out var plr) ||
                plr.Status != PickListStatus.Picked)
            {
                pendingWarehouses.Add(frag.WhsCode);
            }
        }

        if (pendingWarehouses.Count > 0)
        {
            var distinct    = pendingWarehouses.Distinct().ToList();
            var completedWhs = fragments
                .Where(f => plrByFragId.TryGetValue(f.Id, out var p2) && p2.Status == PickListStatus.Picked)
                .Select(f => f.WhsCode).Distinct().ToList();
            _log.LogInformation(
                "[ZF-AUTO] WaitingForOtherPicks RequestId={Rid} completed=[{Done}] pending=[{Whs}]",
                requestId,
                string.Join(",", completedWhs),
                string.Join(",", distinct));
            return new PostPickAutomationResult
            {
                AllRequiredPicksComplete = false,
                DeliveryTriggered        = false,
                AutomationStatus         = "WaitingForOtherPicks",
                PendingWarehouses        = distinct
            };
        }

        _log.LogInformation(
            "[ZF-AUTO] AllPicksComplete RequestId={Rid} fragmentCount={N} — triggering automatic delivery.",
            requestId, fragments.Count);

        ZfDeliveryResult deliveryResult;
        try
        {
            _log.LogInformation("[ZF-AUTO] DeliveryPreflightStarted RequestId={Rid}", requestId);
            deliveryResult = await _delivery.ExecuteDeliveryAsync(requestId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[ZF-AUTO] AutomationError RequestId={Rid} — delivery execution threw unexpectedly", requestId);
            return new PostPickAutomationResult
            {
                AllRequiredPicksComplete = true,
                DeliveryTriggered        = true,
                AutomationStatus         = "AutomationError",
                ErrorMessage             = ex.Message
            };
        }

        bool isDeliveryCreated = deliveryResult.GateVerdict == "DELIVERY_CREATED" ||
                                 deliveryResult.GateVerdict == "ALL_FRAGMENTS_FULLY_DELIVERED" ||
                                 deliveryResult.GateVerdict == "CRASH_RECOVERY_RECONCILED_FROM_SAP";

        bool isDeliveryBlocked = deliveryResult.GateVerdict == "GATE_ERRORS_BLOCK_MUTATION" ||
                                 deliveryResult.GateVerdict == "LIVE_RECHECK_FAILED";

        string?      invoiceStatus   = null;
        int?         invoiceDocEntry = null;
        int?         invoiceDocNum   = null;
        List<string> invoiceErrors   = [];

        if (isDeliveryCreated)
        {
            await _repo.UpdateStateAsync(orch.Id, OrchestrationState.Delivered, ct);
            _log.LogInformation(
                "[ZF-AUTO] DeliveryCreated RequestId={Rid} soDocEntry={De} dlnDocEntry={Dln} dlnDocNum={Dn} lineCount={N}",
                requestId,
                orch.SoDocEntry,
                deliveryResult.Record?.SapDocEntry,
                deliveryResult.Record?.SapDocNum,
                deliveryResult.Preflight.Fragments.Count(f => f.EligibleForDelivery));

            // ── Automatic invoice — Delivery is a committed milestone; invoice failure
            //    must never affect the delivery result returned to the caller. ──
            try
            {
                _log.LogInformation(
                    "[ZF-AUTO] InvoiceAutomationTriggered RequestId={Rid} DeliveryDocEntry={De}",
                    requestId, deliveryResult.Record?.SapDocEntry);

                var invoiceResult    = await _invoice.ExecuteInvoiceAsync(requestId, ct);
                invoiceStatus        = invoiceResult.Verdict;
                invoiceDocEntry      = invoiceResult.InvoiceDocEntry;
                invoiceDocNum        = invoiceResult.InvoiceDocNum;
                invoiceErrors        = invoiceResult.GateErrors;

                _log.LogInformation(
                    "[ZF-AUTO] InvoiceAutomationResult RequestId={Rid} Verdict={V} " +
                    "InvoiceDocEntry={Ie} InvoiceDocNum={In}",
                    requestId, invoiceResult.Verdict,
                    invoiceResult.InvoiceDocEntry, invoiceResult.InvoiceDocNum);
            }
            catch (Exception ex)
            {
                invoiceStatus = "InvoiceAutomationException";
                _log.LogError(ex,
                    "[ZF-AUTO] InvoiceAutomation threw for RequestId={Rid} — " +
                    "Delivery remains Created. Finance/IT must investigate.",
                    requestId);
            }
        }
        else if (isDeliveryBlocked)
        {
            _log.LogError(
                "[ZF-AUTO] DeliveryBlocked RequestId={Rid} soDocEntry={De} verdict={V} gateErrors=[{Errs}]",
                requestId,
                orch.SoDocEntry,
                deliveryResult.GateVerdict,
                string.Join("; ", deliveryResult.Preflight.GateErrors));
        }
        else
        {
            _log.LogInformation(
                "[ZF-AUTO] RequestId={Rid} — delivery verdict: {Verdict} DocEntry={De}",
                requestId, deliveryResult.GateVerdict, deliveryResult.Record?.SapDocEntry);
        }

        string automationStatus = isDeliveryCreated ? "DeliveryCreated"
                                : isDeliveryBlocked ? "DeliveryBlocked"
                                : deliveryResult.GateVerdict ?? "UnknownVerdict";

        return new PostPickAutomationResult
        {
            AllRequiredPicksComplete = true,
            DeliveryTriggered        = true,
            AutomationStatus         = automationStatus,
            DeliveryDocEntry         = deliveryResult.Record?.SapDocEntry,
            DeliveryDocNum           = deliveryResult.Record?.SapDocNum,
            GateVerdict              = deliveryResult.GateVerdict,
            GateErrors               = isDeliveryBlocked ? deliveryResult.Preflight.GateErrors : [],
            InvoiceAutomationStatus  = invoiceStatus,
            InvoiceDocEntry          = invoiceDocEntry,
            InvoiceDocNum            = invoiceDocNum,
            InvoiceGateErrors        = invoiceErrors
        };
    }
}
