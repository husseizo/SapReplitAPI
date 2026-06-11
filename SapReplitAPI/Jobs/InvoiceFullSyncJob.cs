using Quartz;
using SapReplitAPI.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace SapReplitAPI.Jobs
{
    [DisallowConcurrentExecution]
    public class InvoiceFullSyncJob : IJob
    {
        private readonly InvoiceCacheService _invoiceCache;
        private readonly ILogger<InvoiceFullSyncJob> _logger;

        public InvoiceFullSyncJob(InvoiceCacheService invoiceCache, ILogger<InvoiceFullSyncJob> logger)
        {
            _invoiceCache = invoiceCache;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var start = DateTime.Now;
            _logger.LogInformation("📥 [InvoiceFullSyncJob] Starting full invoice and payment sync at {Time}", start);

            try
            {
                await _invoiceCache.FullSyncInvoicesAsync();

                var duration = DateTime.Now - start;
                _logger.LogInformation("✅ [InvoiceFullSyncJob] Sync completed in {Duration} seconds.", duration.TotalSeconds.ToString("F2"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [InvoiceFullSyncJob] Full sync failed.");
                throw;
            }
        }
    }
}
