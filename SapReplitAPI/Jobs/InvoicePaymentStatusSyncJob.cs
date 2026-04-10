using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;

namespace SapReplitAPI.Jobs
{
    /// <summary>
    /// Runs every 30 seconds.
    ///
    /// Asks SAP B1 for any invoice whose DocStatus flipped to 'C' (Closed/Paid)
    /// within the last 90 seconds, using the correct UpdateDate + UpdateTime
    /// reconstruction (SAP B1 stores time separately as an HHMMSS integer).
    ///
    /// Only patches three columns in the cache (DocStatus, PaidToDate, BalanceDue)
    /// — no line items, no payment records, no full UPSERT — so each run
    /// typically completes in under 100 ms end-to-end.
    ///
    /// The existing InvoiceDeltaSyncJob (every 3 min) still handles:
    ///   - New invoice creation
    ///   - Invoice line changes
    ///   - Partial payments / payment record details
    /// </summary>
    [DisallowConcurrentExecution]
    public class InvoicePaymentStatusSyncJob : IJob
    {
        private readonly SapService _sap;
        private readonly InvoiceCacheService _cache;
        private readonly ILogger<InvoicePaymentStatusSyncJob> _logger;

        public InvoicePaymentStatusSyncJob(
            SapService sap,
            InvoiceCacheService cache,
            ILogger<InvoicePaymentStatusSyncJob> logger)
        {
            _sap   = sap;
            _cache = cache;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                // Query SAP: invoices paid in the last 90 seconds (UpdateDate today + UpdateTime window)
                var patches = _sap.GetRecentlyPaidInvoices(secondsBack: 90);

                if (patches.Count == 0)
                {
                    _logger.LogDebug("[PaymentMicroSync] No invoices paid in last 90 s — nothing to patch.");
                    return;
                }

                // Patch only the status columns — no lines, no full UPSERT
                var updated = await _cache.PatchPaymentStatusAsync(patches);

                _logger.LogInformation(
                    "[PaymentMicroSync] Patched {Patched}/{Found} invoice(s) to Closed in cache.",
                    updated, patches.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PaymentMicroSync] Job failed.");
            }
        }
    }
}
