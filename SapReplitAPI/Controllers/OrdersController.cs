using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Queue;
using System.Runtime.Versioning;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [SupportedOSPlatform("windows")]
    public class OrdersController : ControllerBase
    {
        private readonly SapService _sapService;
        private readonly OrderCacheService _orderCacheService;
        private readonly IBackgroundTaskQueue _backgroundTaskQueue;
        private readonly ILogger<OrdersController> _logger;

        public OrdersController(
            SapService sapService,
            OrderCacheService orderCacheService,
            IBackgroundTaskQueue backgroundTaskQueue,
            ILogger<OrdersController> logger)
        {
            _sapService = sapService;
            _orderCacheService = orderCacheService;
            _backgroundTaskQueue = backgroundTaskQueue;
            _logger = logger;
        }


        // ✅ Controller endpoint for quotations
        [HttpPost("quotations")]
        public IActionResult CreateQuotation([FromBody] CreateOrderDto dto)
        {
            try
            {
                var docEntry = _sapService.CreateQuotation(dto);
                return Ok(new { Message = "Quotation created successfully", DocEntry = docEntry });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Quotation creation failed", Error = ex.Message });
            }
        }


        [HttpPost]
        public IActionResult CreateOrder([FromBody] CreateOrderDto dto)
        {
            try
            {
                var docEntry = _sapService.CreateOrder(dto);
                return Ok(new { Message = "Order created successfully", DocEntry = docEntry });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Order creation failed", Error = ex.Message });
            }
        }

        [HttpPut("{docEntry:int}")]
        public IActionResult UpdateOrder(int docEntry, [FromBody] UpdateOrderDto dto)
        {
            if (dto.DocEntry != docEntry)
                return BadRequest(new { Message = "DocEntry in URL and body do not match." });

            try
            {
                var success = _sapService.UpdateOrder(dto);
                if (!success)
                    return NotFound(new { Message = $"Order {docEntry} not found or could not be updated." });

                return Ok(new { Message = "Order updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Order update failed", Error = ex.Message });
            }
        }

        [HttpGet("lines/today")]
        public IActionResult GetTodaysOrderLines(
            [FromQuery] string? query = null,
            [FromQuery] string? keyword = null)
        {
            try
            {
                var today = DateTime.Today;
                var searchTerm = !string.IsNullOrWhiteSpace(query) ? query : keyword;

                var lines = _orderCacheService.GetCachedOrderLines()
                    .Where(l => l.DocDate.Date == today);

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    var lowered = searchTerm.ToLower();

                    lines = lines.Where(l =>
                        l.DocEntry.ToString().Contains(lowered) ||
                        (!string.IsNullOrEmpty(l.Dscription) && l.Dscription.ToLower().Contains(lowered)) ||
                        (!string.IsNullOrEmpty(l.ItemCode) && l.ItemCode.ToLower().Contains(lowered)) ||
                        (!string.IsNullOrEmpty(l.U_ItemName) && l.U_ItemName.ToLower().Contains(lowered)) ||
                        (!string.IsNullOrEmpty(l.U_Manufacturer) && l.U_Manufacturer.ToLower().Contains(lowered))
                    );
                }

                return Ok(lines.OrderByDescending(l => l.DocEntry).ToList());
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Failed to retrieve today's order lines",
                    Error = ex.Message
                });
            }
        }

        [HttpGet("lines/search-open-lines")]
        public IActionResult SearchOpenOrderLines(
     [FromQuery] string? query,
     [FromQuery] string? keyword)
        {
            var searchTerm = !string.IsNullOrWhiteSpace(query) ? query : keyword;

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return BadRequest(new
                {
                    Title = "One or more validation errors occurred.",
                    Status = 400,
                    Errors = new { query = new[] { "The query or keyword field is required." } }
                });
            }

            try
            {
                var results = _orderCacheService.SearchOpenOrderLines(searchTerm);
                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Failed to search open order lines",
                    Error = ex.Message
                });
            }
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

                var headers = _orderCacheService.GetCachedOrderHeaders()
                    .Where(h => h.DocDate.Date == today);

                if (!string.IsNullOrWhiteSpace(lowered))
                {
                    headers = headers.Where(h =>
                        (!string.IsNullOrEmpty(h.CardName) && h.CardName.ToLower().Contains(lowered)) ||
                        h.DocNum.ToString().Contains(lowered) ||
                        h.DocEntry.ToString().Contains(lowered) ||
                        h.SlpCode.ToString().Contains(lowered) ||
                        (!string.IsNullOrEmpty(h.SlpName) && h.SlpName.ToLower().Contains(lowered))
                    );
                }

                var result = headers
                    .OrderByDescending(h => h.DocDate)
                    .Select(h => new
                    {
                        h.DocEntry,
                        h.DocNum,
                        h.DocDate,
                        h.CardName,
                        h.OrderValue,
                        h.Status,
                        h.SlpCode,
                        h.SlpName
                    })
                    .ToList();

                return Ok(result);
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

        [HttpGet("headers/search-open-headers")]
        public IActionResult SearchOpenOrderHeaders(
     [FromQuery] string? query,
     [FromQuery] string? keyword)
        {
            var searchTerm = !string.IsNullOrWhiteSpace(query) ? query : keyword;

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return BadRequest(new
                {
                    Title = "One or more validation errors occurred.",
                    Status = 400,
                    Errors = new { query = new[] { "The query or keyword field is required." } }
                });
            }

            try
            {
                // Get all results with very large page size
                var results = _orderCacheService.SearchOpenOrderHeadersPaged(
                    searchTerm,
                    page: 1,
                    pageSize: int.MaxValue
                );

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = $"Failed to search open order headers from {DateTime.Today.Year}",
                    Error = ex.Message
                });
            }
        }

        [HttpPost("sync-full")]
        public IActionResult TriggerOrderFullSync()
        {
            _backgroundTaskQueue.Enqueue(async (sp, token) =>
            {
                var scopedCache = sp.GetRequiredService<OrderCacheService>();
                var logger = sp.GetRequiredService<ILogger<OrdersController>>();
                logger.LogInformation("🔁 Starting full order cache sync...");

                try
                {
                    await scopedCache.FullSyncOrdersAsync();
                    logger.LogInformation("✅ Full order cache sync completed.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Full sync failed.");
                }
            });

            return Ok(new { Message = "🚀 Full order cache sync started in background (queued)." });
        }
    }
}