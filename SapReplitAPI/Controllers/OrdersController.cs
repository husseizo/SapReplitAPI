using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Services;
using SapReplitAPI.Services.PickList;
using SapReplitAPI.Services.Queue;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

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
        private readonly PendingOrderService _pendingOrders;
        private readonly ILogger<OrdersController> _logger;
        private readonly CacheDbContext _sqlite;
        private readonly IPickListEventRefreshService _plRefresh;

        public OrdersController(
            SapService sapService,
            OrderCacheService orderCacheService,
            IBackgroundTaskQueue backgroundTaskQueue,
            PendingOrderService pendingOrders,
            ILogger<OrdersController> logger,
            CacheDbContext sqlite,
            IPickListEventRefreshService plRefresh)
        {
            _sapService = sapService;
            _orderCacheService = orderCacheService;
            _backgroundTaskQueue = backgroundTaskQueue;
            _pendingOrders = pendingOrders;
            _logger = logger;
            _sqlite = sqlite;
            _plRefresh = plRefresh;
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
        public async Task<IActionResult> CreateOrder([FromBody] CreateOrderDto dto)
        {
            var replitId = PendingOrderService.NewReplitId();
            try
            {
                _sapService.EnrichOrderLines(dto.Lines);
                var docEntry = _sapService.CreateOrder(dto, replitId);
                _logger.LogInformation("✅ Order created in SAP. ReplitId={ReplitId}, DocEntry={DocEntry}", replitId, docEntry);
                return Ok(new { Message = "Order created successfully", ReplitId = replitId, Status = "Synced", DocEntry = docEntry });
            }
            catch (Exception ex) when (IsSapOffline(ex) || IsSapRejection(ex))
            {
                // SAP offline or rejected — queue locally, return success to caller
                var pending = await _pendingOrders.SavePendingAsync(dto, replitId);
                _logger.LogWarning("📥 Order saved as pending (SAP issue). ReplitId={ReplitId}. Error: {Error}", replitId, ex.Message);
                return Accepted(new
                {
                    Message = "SAP is currently unavailable. Order queued and will sync automatically.",
                    ReplitId = replitId,
                    Status = "Pending",
                    PendingId = pending.Id,
                    DocEntry = (int?)null
                });
            }
        }

        private static bool IsSapOffline(Exception ex) =>
            ex.Message.Contains("SAP Connection failed", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) ||
            ex is COMException;

        private static bool IsSapRejection(Exception ex) =>
            ex.Message.StartsWith("Failed to create order:", StringComparison.OrdinalIgnoreCase);

        [HttpPut("{docEntry:int}")]
        public async Task<IActionResult> UpdateOrder(int docEntry, [FromBody] UpdateOrderDto dto, CancellationToken ct)
        {
            if (dto.DocEntry != docEntry)
                return BadRequest(new { Message = "DocEntry in URL and body do not match." });

            try
            {
                var success = _sapService.UpdateOrder(dto);
                if (!success)
                    return NotFound(new { Message = $"Order {docEntry} not found or could not be updated." });

                // Seam 4: refresh any released OPKLs that reference this SO so caches stay current
                var absEntries = await _sqlite.PickListLines.AsNoTracking()
                    .Where(l => l.OrderEntry == docEntry)
                    .Select(l => l.AbsEntry)
                    .Distinct()
                    .ToListAsync(ct);

                foreach (var absEntry in absEntries)
                {
                    try { await _plRefresh.RefreshAsync(absEntry, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[ORDER-UPDATE] PickList cache fast-path non-fatal AbsEntry={Abs}", absEntry); }
                }

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