using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;
using SapReplitAPI.Services;

public class InvoiceStatusCacheJob : IJob
{
    private readonly DashboardService _dashboardService;
    private readonly SapService _sapService; // 👈 inject SapService
    private readonly ILogger<InvoiceStatusCacheJob> _logger;

    public InvoiceStatusCacheJob(
        DashboardService dashboardService,
        SapService sapService,
        ILogger<InvoiceStatusCacheJob> logger)
    {
        _dashboardService = dashboardService;
        _sapService = sapService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var date = DateTime.Today.AddDays(-1).Date;

        string[] salesPeople = {
            "Mohamed Laseko", "Hussein Abdurahmani", "Ahmed Rahman", "Suleiman Rajab",
            "Abdulrahman Ally", "Mohamed Rashidi", "Mikidadi Namwao", "Ibrahim Hamisi",
            "Tarik Laseko", "Bushiri Abdallah", "Mohamed Sahag"
        };

        foreach (var name in salesPeople)
        {
            try
            {
                _logger.LogInformation("🕒 Caching invoice status for {Name} on {Date}", name, date.ToShortDateString());

                // ✅ fetch from SAP
                var rows = await _sapService.GetDetailedInvoiceStatusReportAsync(name, date);

                // ✅ cache into SQLite through DashboardService
                await _dashboardService.CacheInvoiceStatusReportAsync(name, date, rows);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to cache for {Name}", name);
            }
        }
    }
}