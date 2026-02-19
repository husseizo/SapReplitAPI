using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;
using SapReplitAPI.Services;
using SapReplitAPI.DTOs.Dashboard;

namespace SapReplitAPI.Jobs
{
    public class CacheInvoiceStatusJob : IJob
    {
        private readonly DashboardService _dashboardService;
        private readonly SapService _sapService; // 👈 inject SapService here
        private readonly ILogger<CacheInvoiceStatusJob> _logger;

        public CacheInvoiceStatusJob(
            DashboardService dashboardService,
            SapService sapService,
            ILogger<CacheInvoiceStatusJob> logger)
        {
            _dashboardService = dashboardService;
            _sapService = sapService;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation("📅 Running scheduled CacheInvoiceStatusJob...");

            var salesReps = new List<string>
            {
                "Mohamed Laseko",
                "Hussein Abdurahmani",
                "Ahmed Rahman",
                "Suleiman Rajab",
                "Abdulrahman Ally",
                "Mohamed Rashidi",
                "Mikidadi Namwao",
                "Ibrahim Hamisi",
                "Tarik Laseko"
            };

            var today = DateTime.Today;

            foreach (var rep in salesReps)
            {
                try
                {
                    _logger.LogInformation("🔄 Caching invoice status for {Rep} on {Date}", rep, today.ToShortDateString());

                    // ✅ fetch from SAP here
                    var rows = await _sapService.GetDetailedInvoiceStatusReportAsync(rep, today);

                    // ✅ pass rows into DashboardService
                    await _dashboardService.CacheInvoiceStatusReportAsync(rep, today, rows);

                    _logger.LogInformation("✅ Cached report for {Rep}", rep);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to cache invoice status for {SalesRep}", rep);
                }
            }

            _logger.LogInformation("✅ Finished CacheInvoiceStatusJob.");
        }
    }
}