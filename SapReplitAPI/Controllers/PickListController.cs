using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/pick-lists")]
public class PickListController : ControllerBase
{
    private readonly PickListCacheService _cache;
    private readonly ILogger<PickListController> _log;

    public PickListController(PickListCacheService cache, ILogger<PickListController> log)
    {
        _cache = cache;
        _log   = log;
    }

    /// <summary>GET /api/pick-lists/{absEntry} — read pick list header from cache.</summary>
    [HttpGet("{absEntry:int}")]
    public async Task<IActionResult> GetPickList(int absEntry, CancellationToken ct)
    {
        var header = await _cache.ReadPickListAsync(absEntry, ct);
        if (header == null) return NotFound(new { absEntry, error = "Not found in cache" });
        return Ok(header);
    }

    /// <summary>GET /api/pick-lists/{absEntry}/lines — read pick list lines from cache.</summary>
    [HttpGet("{absEntry:int}/lines")]
    public async Task<IActionResult> GetPickListLines(int absEntry, CancellationToken ct)
    {
        var lines = await _cache.ReadPickListLinesAsync(absEntry, ct);
        return Ok(new { absEntry, count = lines.Count, lines });
    }

    /// <summary>GET /api/pick-lists/{absEntry}/bins — read bin allocations from cache.</summary>
    [HttpGet("{absEntry:int}/bins")]
    public async Task<IActionResult> GetPickListBins(int absEntry, CancellationToken ct)
    {
        var bins = await _cache.ReadPickListBinsAsync(absEntry, ct);
        return Ok(new { absEntry, count = bins.Count, bins });
    }

    /// <summary>GET /api/pick-lists/cache/counts — row counts in SQLite cache.</summary>
    [HttpGet("cache/counts")]
    public async Task<IActionResult> GetCacheCounts(CancellationToken ct)
    {
        var (headers, lines, bins) = await _cache.GetCacheCountsAsync(ct);
        return Ok(new { headers, lines, bins });
    }

    /// <summary>POST /api/pick-lists/{absEntry}/refresh — force re-fetch from SAP into cache.</summary>
    [HttpPost("{absEntry:int}/refresh")]
    public async Task<IActionResult> Refresh(int absEntry, CancellationToken ct)
    {
        await _cache.RefreshPickListAsync(absEntry, ct);
        var header = await _cache.ReadPickListAsync(absEntry, ct);
        return Ok(new { absEntry, refreshed = true, cached = header });
    }
}
