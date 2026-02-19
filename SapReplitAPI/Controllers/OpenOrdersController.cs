using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Queue;

[ApiController]
[Route("api/open-orders")]
public class OpenOrdersController : ControllerBase
{
    private readonly CacheDbContext _db;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<OpenOrdersController> _logger;

    public OpenOrdersController(CacheDbContext db, IBackgroundTaskQueue taskQueue, ILogger<OpenOrdersController> logger)
    {
        _db = db;
        _taskQueue = taskQueue;
        _logger = logger;
    }

    [HttpGet("headers")]
    public async Task<IActionResult> GetOpenOrderHeaders([FromQuery] string? keyword, [FromQuery] int? slpCode)
    {
        var query = _db.OpenOrderHeaders.AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            keyword = keyword.Trim().ToLower();
            query = query.Where(h =>
                h.CardName.ToLower().Contains(keyword) ||
                h.DocEntry.ToString().Contains(keyword));
        }

        if (slpCode.HasValue)
        {
            query = query.Where(h => h.SlpCode == slpCode.Value);
        }

        var headers = await query
            .OrderByDescending(h => h.DocDate)
            .ToListAsync();

        return Ok(headers);
    }

    [HttpGet("lines/{docEntry}")]
    public async Task<IActionResult> GetLines(int docEntry)
    {
        var lines = await _db.OpenOrderLines
            .Where(l => l.DocEntry == docEntry)
            .ToListAsync();

        return Ok(lines);
    }

    [HttpPost("sync-manual")]
    public IActionResult TriggerManualSync()
    {
        _taskQueue.Enqueue(async (sp, token) =>
        {
            var logger = sp.GetRequiredService<ILogger<OpenOrdersController>>();
            logger.LogInformation("🌀 Queued Open Order sync started manually...");

            try
            {
                // ✅ Resolve OpenOrderCacheService in this background scope
                var openOrderCacheService = sp.GetRequiredService<OpenOrderCacheService>();
                await openOrderCacheService.SyncOpenOrdersAsync();

                logger.LogInformation("✅ Manual Open Order sync complete.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Manual Open Order sync failed.");
            }
        });

        return Ok(new { Message = "✅ Open Orders sync job has been queued." });
    }
}