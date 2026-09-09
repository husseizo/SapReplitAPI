using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Repository contract used by ZoneFulfillmentPickReconciliationService.
/// Extracted so the reconciliation service can be unit-tested without SQL.
/// </summary>
public interface IZfReconciliationRepo
{
    Task<List<FulfillmentOrchestrationRecord>> GetStuckAcceptedOrchestrationsAsync(
        int thresholdSeconds, CancellationToken ct = default);

    Task<FulfillmentOrchestrationRecord?> FindOrchestrationAsync(
        Guid requestId, CancellationToken ct = default);

    Task<List<PickListRecordModel>> GetPickListRecordsAsync(
        long orchestrationId, CancellationToken ct = default);

    Task UpdatePickListPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string status, CancellationToken ct = default);

    Task<int> UpdatePickListFragmentPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string pickStatus, CancellationToken ct = default);
}
