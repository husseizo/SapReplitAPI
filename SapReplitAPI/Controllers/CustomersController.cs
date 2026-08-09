using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.CustomerModels;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Queue;
using System.Runtime.InteropServices;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CustomersController : ControllerBase
    {
        private readonly SapService _sapService;
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly PendingCustomerService _pendingCustomers;
        private readonly ILogger<CustomersController> _logger;

        public CustomersController(
            SapService sapService,
            IBackgroundTaskQueue taskQueue,
            PendingCustomerService pendingCustomers,
            ILogger<CustomersController> logger)
        {
            _sapService = sapService;
            _taskQueue = taskQueue;
            _pendingCustomers = pendingCustomers;
            _logger = logger;
        }



        [HttpGet("cache")]
        public async Task<IActionResult> GetCachedCustomers([FromServices] CustomerCacheService cacheService)
        {
            try
            {
                var customers = await cacheService.GetCachedCustomersAsync();
                return Ok(customers);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Failed to fetch cached customers", Error = ex.Message });
            }
        }


        [HttpGet("by-phone")]
        public async Task<IActionResult> GetCustomerByPhone(
    [FromQuery] string phone,
    [FromServices] CustomerCacheService cacheService)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { Message = "Query parameter 'phone' is required." });

            var results = await cacheService.GetCardNamesByPhoneAsync(phone);

            if (results.Count == 0)
                return NotFound(new { Message = "No customer found for supplied phone." });

            if (results.Count == 1)
                return Ok(new { CardName = results[0].CardName, Phone = results[0].Phone });

            // Multiple matches — return all distinct results so caller can disambiguate
            return Ok(new { Customers = results, Count = results.Count });
        }



        [HttpPost]
        public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerDto dto, [FromServices] CustomerCacheService cacheService)
        {
            try
            {
                var newCardCode = _sapService.CreateCustomer(dto);
                var cacheUpdated = await cacheService.TryUpsertCreatedCustomerAsync(newCardCode, dto);
                if (!cacheUpdated)
                    _logger.LogWarning("⚠️ [CustomersController] Customer {CardCode} created in SAP but not yet in SQLite cache.", newCardCode);

                return Ok(new { Message = "Customer created successfully", CardCode = newCardCode, Status = "Synced" });
            }
            catch (Exception ex) when (IsSapOffline(ex))
            {
                var pending = await _pendingCustomers.SavePendingAsync(dto);
                _logger.LogWarning("📥 [CustomersController] SAP offline — customer '{Name}' queued as pending id={Id}.", dto.CardName, pending.Id);
                return Accepted(new
                {
                    Message    = "SAP is currently unavailable. Customer queued and will sync automatically.",
                    PendingId  = pending.Id,
                    CardName   = dto.CardName,
                    Status     = "Pending",
                    CardCode   = (string?)null
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Customer creation failed", Error = ex.Message });
            }
        }

        [HttpGet("pending")]
        public async Task<IActionResult> ListPendingCustomers([FromQuery] string? status = null)
        {
            var list = await _pendingCustomers.GetAllAsync(status);
            return Ok(list.Select(c => new
            {
                c.Id,
                c.CardName,
                c.Phone,
                c.CustomerType,
                c.Region,
                c.SalesPersonName,
                c.SlpCode,
                c.Status,
                c.SapCardCode,
                c.RetryCount,
                c.NextRetryAt,
                c.ErrorMessage,
                c.CreatedAt,
                c.SyncedAt
            }));
        }

        [HttpPost("pending/{id:int}/retry")]
        public async Task<IActionResult> RetryFailedCustomer(int id)
        {
            try
            {
                await _pendingCustomers.ResetForRetryAsync(id);
                return Ok(new { Message = $"PendingCustomer {id} reset for retry." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
        }

        private static bool IsSapOffline(Exception ex) =>
            ex is COMException ||
            ex.Message.Contains("SAP Connection failed", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase);

      
        /// <summary>
        /// Manually trigger a full sync of SAP customers into cache
        /// </summary>
        [HttpPost("sync")]
        public IActionResult SyncCustomersToCache()
        {
            _taskQueue.Enqueue(async (sp, token) =>
            {
                var scoped = sp.GetRequiredService<CustomerCacheService>();
                Console.WriteLine("👥 Syncing customers from SAP...");
                await scoped.FullSyncFromSAPAsync();
                Console.WriteLine("✅ Customer sync complete.");
            });

            return Ok(new { Message = "🕓 Customer sync has been queued." });
        }
    }
}
