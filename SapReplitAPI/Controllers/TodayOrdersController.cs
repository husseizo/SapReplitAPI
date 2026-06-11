using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/today-orders")]
    public class TodayOrdersController : ControllerBase
    {
        private readonly CacheDbContext _db;
        private readonly TodayOrderCacheService _todayOrderCacheService;

        public TodayOrdersController(CacheDbContext db, TodayOrderCacheService todayOrderCacheService)
        {
            _db = db;
            _todayOrderCacheService = todayOrderCacheService;
        }

        [HttpGet("{docEntry}/lines")]
        public async Task<IActionResult> GetTodayOrderLines(int docEntry)
        {
            var lines = await _db.TodayOrderLines
                .Where(l => l.DocEntry == docEntry)
                .ToListAsync();

            return Ok(lines);
        }

        [HttpGet("headers/today")]
        public IActionResult GetTodaysOrderHeaders(
            [FromQuery] string? query = null,
            [FromQuery] string? keyword = null)
        {
            try
            {
                var today = DateTime.Today;
                var searchTerm = !string.IsNullOrWhiteSpace(query) ? query : keyword;
                var lowered = searchTerm?.ToLower();
                int? parsedSlp = null;

                if (!string.IsNullOrWhiteSpace(lowered) && int.TryParse(lowered, out var slp))
                    parsedSlp = slp;

                var headers = _db.TodayOrderHeaders
                    .Where(h => h.DocDate.Date == today);

                if (!string.IsNullOrWhiteSpace(lowered))
                {
                    headers = headers.Where(h =>
                        (!string.IsNullOrEmpty(h.CardName) && h.CardName.ToLower().Contains(lowered)) ||
                        h.DocNum.ToString().Contains(lowered) ||
                        h.DocEntry.ToString().Contains(lowered) ||
                        (!string.IsNullOrEmpty(h.SlpName) && h.SlpName.ToLower().Contains(lowered)) ||
                        (parsedSlp.HasValue && h.SlpCode == parsedSlp.Value));
                }

                var headerList = headers
                    .OrderByDescending(h => h.DocDate)
                    .ToList();

                var totalOrderValue = headerList
                    .Where(h => !h.Cancelled)
                    .Sum(h => h.OrderValue);

                var result = headerList
                    .Select(h => new
                    {
                        h.DocEntry,
                        h.DocNum,
                        h.DocDate,
                        h.CardName,
                        h.OrderValue,
                        h.Status,
                        h.SlpCode,
                        h.SlpName,
                        h.Cancelled
                    })
                    .ToList();

                return Ok(new
                {
                    TotalOrderValue = totalOrderValue,
                    Count = result.Count,
                    Results = result
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Failed to fetch today's order headers",
                    Error = ex.Message
                });
            }
        }

        [HttpPost("sync")]
        public async Task<IActionResult> TriggerSyncNow()
        {
            await _todayOrderCacheService.RefreshTodayOrdersFromSAP();
            return Ok(new { Message = "\u2705 Sync triggered successfully for today's orders." });
        }
    }
}
