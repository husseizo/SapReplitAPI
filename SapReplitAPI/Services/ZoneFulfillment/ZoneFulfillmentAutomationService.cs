using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Post-pick automation: evaluates all-picks-complete after each Confirm Pick,
/// then automatically triggers delivery when the final required pick is confirmed.
///
/// Contract:
///   1. Check all SoLineFragments for this orchestration.
///   2. If any fragment's latest PickListRecord is not Status=Picked → wait.
///   3. If all complete → delegate to ZoneFulfillmentDeliveryService.ExecuteDeliveryAsync().
///      The delivery service holds its own per-RequestId semaphore lock, preventing
///      duplicate ODLN creation from concurrent final-pick races.
/// </summary>
public sealed class ZoneFulfillmentAutomationService
{
    private readonly ZoneFulfillmentRepository                _repo;
    private readonly ZoneFulfillmentDeliveryService           _delivery;
    private readonly ILogger<ZoneFulfillmentAutomationService> _log;

    public ZoneFulfillmentAutomationService(
        ZoneFulfillmentRepository                 repo,
        ZoneFulfillmentDeliveryService            delivery,
        ILogger<ZoneFulfillmentAutomationService> log)
    {
        _repo     = repo;
        _delivery = delivery;
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
            var distinct = pendingWarehouses.Distinct().ToList();
            _log.LogInformation(
                "[ZF-AUTO] RequestId={Rid} — {N} warehouse(s) still pending: [{Whs}]. Waiting.",
                requestId, distinct.Count, string.Join(", ", distinct));
            return new PostPickAutomationResult
            {
                AllRequiredPicksComplete = false,
                DeliveryTriggered        = false,
                AutomationStatus         = "WaitingForOtherPicks",
                PendingWarehouses        = distinct
            };
        }

        _log.LogInformation(
            "[ZF-AUTO] RequestId={Rid} — ALL {N} pick(s) confirmed. Triggering automatic delivery.",
            requestId, fragments.Count);

        ZfDeliveryResult deliveryResult;
        try
        {
            deliveryResult = await _delivery.ExecuteDeliveryAsync(requestId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[ZF-AUTO] Delivery execution threw for RequestId={Rid}", requestId);
            return new PostPickAutomationResult
            {
                AllRequiredPicksComplete = true,
                DeliveryTriggered        = true,
                AutomationStatus         = "DeliveryFailed",
                ErrorMessage             = ex.Message
            };
        }

        bool delivered = deliveryResult.GateVerdict == "DELIVERY_CREATED" ||
                         deliveryResult.GateVerdict == "ALL_FRAGMENTS_FULLY_DELIVERED" ||
                         deliveryResult.GateVerdict == "CRASH_RECOVERY_RECONCILED_FROM_SAP";

        if (delivered)
            await _repo.UpdateStateAsync(orch.Id, OrchestrationState.Delivered, ct);

        _log.LogInformation(
            "[ZF-AUTO] RequestId={Rid} — delivery result: {Verdict} DocEntry={De}",
            requestId, deliveryResult.GateVerdict, deliveryResult.Record?.SapDocEntry);

        return new PostPickAutomationResult
        {
            AllRequiredPicksComplete = true,
            DeliveryTriggered        = true,
            AutomationStatus         = deliveryResult.GateVerdict ?? "UnknownVerdict",
            DeliveryDocEntry         = deliveryResult.Record?.SapDocEntry,
            DeliveryDocNum           = deliveryResult.Record?.SapDocNum,
            GateVerdict              = deliveryResult.GateVerdict
        };
    }
}
