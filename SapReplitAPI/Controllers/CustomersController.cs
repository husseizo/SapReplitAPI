using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.CustomerModels;
using SapReplitAPI.Services.Queue;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CustomersController : ControllerBase
    {
        private readonly SapService _sapService;
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly ILogger<CustomersController> _logger;

        public CustomersController(SapService sapService, IBackgroundTaskQueue taskQueue, ILogger<CustomersController> logger)
        {
            _sapService = sapService;
            _taskQueue = taskQueue;
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
                    _logger.LogWarning("⚠️ [CustomersController] Customer {CardCode} was created in SAP but was not immediately visible in SQLite cache.", newCardCode);

                return Ok(new { Message = "Customer created successfully", CardCode = newCardCode });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Customer creation failed", Error = ex.Message });
            }
        }

      
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
