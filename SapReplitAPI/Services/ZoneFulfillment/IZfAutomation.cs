using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Post-pick automation contract used by ZoneFulfillmentPickReconciliationService.
/// Extracted so the reconciliation service can be unit-tested without delivery/invoice dependencies.
/// </summary>
public interface IZfAutomation
{
    Task<PostPickAutomationResult> EvaluateAndTriggerDeliveryAsync(
        Guid requestId, CancellationToken ct = default);
}
