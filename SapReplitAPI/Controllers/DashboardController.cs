using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.DTOs.Dashboard;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Queue;
using System.Threading.Tasks;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly DashboardService _dashboardService;
        private readonly IBackgroundTaskQueue _backgroundTaskQueue;
        private readonly ILogger<DashboardController> _logger;



        public DashboardController(
    DashboardService dashboardService,
    IBackgroundTaskQueue backgroundTaskQueue,
    ILogger<DashboardController> logger)
        {
            _dashboardService = dashboardService;
            _backgroundTaskQueue = backgroundTaskQueue;
            _logger = logger; // ✅ this will now work
        }





        [HttpGet("month-to-date-sales")]
        public async Task<IActionResult> GetMonthToDateSales([FromQuery] int? slpCode)
        {
            var list = await _dashboardService.GetMonthToDateSalesAsync(slpCode);

            if (slpCode.HasValue)
            {
                var dto = list.FirstOrDefault() ?? new MonthToDateSalesDto();
                var collected = dto.CashSales + dto.CreditSales; // exclude Pending
                return Ok(new
                {
                    totalSales = collected,   // FE-friendly key
                    cash = dto.CashSales,
                    credit = dto.CreditSales
                });
            }

            var grandTotal = list.Sum(x => x.CashSales + x.CreditSales);
            return Ok(new
            {
                totalSales = grandTotal     // FE-friendly key
               
            });
        }

        [HttpGet("sales-range")]
        public async Task<IActionResult> GetMonthToDateSalesRange(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            [FromQuery] int? slpCode)
        {
            var result = await _dashboardService.GetMonthToDateSalesRangeAsync(slpCode, startDate, endDate);

            if (slpCode.HasValue)
            {
                var dto = (MonthToDateSalesDto)result;
                var collected = dto.CashSales + dto.CreditSales;
                return Ok(new
                {
                    totalSales = collected,   // FE-friendly key
                    cash = dto.CashSales,
                    credit = dto.CreditSales
                });
            }

            var list = result as List<MonthToDateSalesDto> ?? new List<MonthToDateSalesDto>();
            var collectedTotal = list.Sum(x => x.CashSales + x.CreditSales);
            return Ok(new
            {
                totalSales = collectedTotal, // FE-friendly key
                
            });
        }

        [HttpGet("sales-range-admin")]
        public async Task<IActionResult> GetMonthToDateSalesRangeAdmin(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate)
        {
            startDate = startDate.Date;
            endDate = endDate.Date;
            if (endDate < startDate)
                return BadRequest(new { Message = "endDate must be on/after startDate." });

            var result = await _dashboardService.GetMonthToDateSalesRangeAsync(null, startDate, endDate);
            var list = result as List<MonthToDateSalesDto> ?? new List<MonthToDateSalesDto>();

            var collectedTotal = list.Sum(x => x.CashSales + x.CreditSales);

            return Ok(new[]
            {
        new { slpCode = "all", totalSales = collectedTotal } // FE-friendly key
    });
        }



        [HttpGet("month-todate-cash-vs-credit")]
        public async Task<IActionResult> GetCashVsCreditSapAlignedAsync([FromQuery] int? slpCode)
        {
            var today = DateTime.Today;
            var year = today.Year;
            var month = today.Month;

            var result = await _dashboardService.GetCashVsCreditSapAlignedAsync(slpCode, year, month);
            return Ok(result);
        }


        [HttpGet("cash-vs-credit-range")]
        public async Task<IActionResult> GetCashVsCreditRange(
     [FromQuery] DateTime startDate,
     [FromQuery] DateTime endDate,
     [FromQuery] int? slpCode)
        {
            var result = await _dashboardService.GetCashVsCreditRangeAsync(slpCode, startDate, endDate);
            return Ok(result);
        }

        [HttpGet("cash-vs-credit-range-admin")]
        public async Task<IActionResult> GetCashVsCreditRangeAll(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate)
        {
            var result = await _dashboardService.GetCashVsCreditRangeAsync(null, startDate, endDate);

            return Ok(new
            {
                slpCode = "all",
                cash = result.Cash,
                credit = result.Credit,
                total = result.Total
            });
        }


        [HttpGet("month-todate-average-order-value")]
        public async Task<IActionResult> GetAverageOrderValue([FromQuery] int? slpCode)
        {
            var result = await _dashboardService.GetAverageOrderValueAsync(slpCode);
            return Ok(result);
        }

        [HttpGet("average-order-value-range")]
        public async Task<IActionResult> GetAverageOrderValueRange(
    [FromQuery] int? slpCode,
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate)
        {
            var result = await _dashboardService.GetAverageOrderValueRangeAsync(slpCode, startDate, endDate);
            return Ok(result);
        }


        [HttpGet("avg-order-value-range-admin")]
        public async Task<IActionResult> GetAvgOrderValueRange([FromQuery] DateTime startDate, [FromQuery] DateTime endDate)
        {
            var result = await _dashboardService.GetAverageOrderValueRangeAsync(null, startDate, endDate);
            return Ok(result);
        }



        [HttpGet("month-todate-unpaid-orders")]
        public async Task<IActionResult> GetUnpaidOrders([FromQuery] int? slpCode)
        {
            var result = await _dashboardService.GetUnpaidOrdersAsync(slpCode);
            return Ok(result);
        }


        [HttpGet("unpaid-orders-range")]
        public async Task<IActionResult> GetUnpaidOrdersRange(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate,
    [FromQuery] int? slpCode)
        {
            var result = await _dashboardService.GetUnpaidOrdersByDateRangeAsync(slpCode, startDate, endDate);
            return Ok(result);
        }

        [HttpGet("unpaid-orders-range-admin")]
        public async Task<IActionResult> GetUnpaidOrdersRangeAll(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate)
        {
            var result = await _dashboardService.GetUnpaidOrdersByDateRangeAsync(null, startDate, endDate);
            return Ok(result);
        }



        [HttpGet("revenue-trend")]
        public async Task<IActionResult> GetRevenueTrend()
        {
            var result = await _dashboardService.GetRevenueTrendAsync();
            return Ok(result);
        }


        [HttpGet("revenue-trend/by-salesperson")]
        public async Task<IActionResult> GetRevenueTrendBySalesperson([FromQuery] int slpCode)
        {
            var result = await _dashboardService.GetRevenueTrendBySlpCodeAsync(slpCode);
            return Ok(result);
        }





        [HttpGet("open-docs/current-year")]
        public async Task<IActionResult> GetOpenDocsCurrentYear([FromQuery] int? slpCode)
        {
            var list = await _dashboardService.GetOpenDocsCurrentYearAsync(slpCode);

            if (slpCode.HasValue)
            {
                var dto = list.FirstOrDefault() ?? new OpenDocsBreakdownDto { SlpCode = slpCode.Value };
                return Ok(new
                {
                    slpCode = dto.SlpCode,
                   
                    totalOpen = dto.OpenTotal
                });
            }

            var total = list.Sum(x => x.OpenTotal);
            return Ok(new
            {
                totalOpen = total,
               
            });
        }
    }
}


