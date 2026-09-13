using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Filters;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Controllers;

/// <summary>
/// Optional latency-optimization hook for the Warehouse Operations App.
/// Allows the external app to request an immediate per-AbsEntry cache refresh
/// after completing a pick, without waiting for the 30-second polling cycle.
///
/// The system remains correct if this endpoint is NEVER called:
/// PickListCacheFreshnessJob (30 s) is the correctness safety net.
///
/// Auth: X-API-Key header (same mechanism as all other controllers).
/// </summary>
[ApiController]
[Route("internal")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public sealed class PickListRefreshController : ControllerBase
{
    private readonly IPickListEventRefreshService       _refresh;
    private readonly ILogger<PickListRefreshController> _log;

    public PickListRefreshController(
        IPickListEventRefreshService       refresh,
        ILogger<PickListRefreshController> log)
    {
        _refresh = refresh;
        _log     = log;
    }

    /// <summary>
    /// POST /internal/refresh/pick-list/{absEntry}
    /// Triggers an immediate SQLite + Neon refresh for a single OPKL AbsEntry.
    /// Idempotent — safe to call multiple times for the same AbsEntry.
    /// </summary>
    [HttpPost("refresh/pick-list/{absEntry:int}")]
    public async Task<IActionResult> RefreshPickList(int absEntry, CancellationToken ct)
    {
        _log.LogInformation("[PickListRefresh] Hook triggered AbsEntry={Abs}", absEntry);
        var (ok, error) = await _refresh.RefreshAsync(absEntry, ct);
        if (ok)
            return Ok(new { absEntry, refreshed = true });

        _log.LogWarning("[PickListRefresh] Refresh failed AbsEntry={Abs} Error={Err}", absEntry, error);
        return StatusCode(500, new { absEntry, refreshed = false, error });
    }
}
