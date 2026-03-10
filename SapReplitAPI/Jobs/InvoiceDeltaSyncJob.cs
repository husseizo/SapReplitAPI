using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;
using SapReplitAPI.Services;

namespace SapReplitAPI.Jobs
{
    [DisallowConcurrentExecution]
    public class InvoiceDeltaSyncJob : IJob
    {
        private readonly InvoiceCacheService _invoiceCacheService;
        private readonly ILogger<InvoiceDeltaSyncJob> _logger;

        public InvoiceDeltaSyncJob(InvoiceCacheService invoiceCacheService, ILogger<InvoiceDeltaSyncJob> logger)
        {
            _invoiceCacheService = invoiceCacheService;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation("⏳ [InvoiceDeltaSyncJob] Running incremental invoice & payment sync...");

            try
            {
                await _invoiceCacheService.DeltaSyncInvoicesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [InvoiceDeltaSyncJob] Delta sync failed.");
            }
        }
    }
}