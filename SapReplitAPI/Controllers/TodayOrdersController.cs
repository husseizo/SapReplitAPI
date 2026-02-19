using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Jobs;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/today-orders")]
    public class TodayOrdersController : ControllerBase
    {
        private readonly CacheDbContext _db;
        private readonly SyncTodayOrdersJob _syncJob;

        public TodayOrdersController(CacheDbContext db, SyncTodayOrdersJob syncJob)
        {
            _db = db;
            _syncJob = syncJob;
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

                if (!string.IsNullOrWhiteSpace(lowered) && int.TryParse(lowered, out int slp))
                {
                    parsedSlp = slp;
                }

                // Base query for all today’s headers (including canceled)
                var headers = _db.TodayOrderHeaders
                    .Where(h => h.DocDate.Date == today);

                if (!string.IsNullOrWhiteSpace(lowered))
                {
                    headers = headers.Where(h =>
                        (!string.IsNullOrEmpty(h.CardName) && h.CardName.ToLower().Contains(lowered)) ||
                        h.DocNum.ToString().Contains(lowered) ||
                        h.DocEntry.ToString().Contains(lowered) ||
                        (!string.IsNullOrEmpty(h.SlpName) && h.SlpName.ToLower().Contains(lowered)) ||
                        (parsedSlp.HasValue && h.SlpCode == parsedSlp.Value)
                    );
                }

                var headerList = headers
                    .OrderByDescending(h => h.DocDate)
                    .ToList();

                // ✅ Total excluding canceled orders
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



        // ✅ Manual Sync Endpoint
        [HttpPost("sync")]
        public async Task<IActionResult> TriggerSyncNow()
        {
            await _syncJob.Execute(default!); // 👈 fixes CS8625 warning
            return Ok(new { Message = "✅ Sync triggered successfully for today's orders." });
        }
    }
}