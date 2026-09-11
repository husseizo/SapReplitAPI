using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Minimal read interface for the two priority-lookup methods the
/// TieredWarehousePriorityResolver needs. Extracted so the resolver can be
/// unit-tested in isolation with a fake/stub.
/// </summary>
public interface IZfPriorityRepo
{
    Task<List<OriginWarehousePriorityRow>> GetOriginWarehousePriorityAsync(
        string zoneName, string originWhsCode, CancellationToken ct = default);

    Task<List<ZoneWarehouse>> GetZoneWarehousesAsync(
        string zoneName, CancellationToken ct = default);
}
