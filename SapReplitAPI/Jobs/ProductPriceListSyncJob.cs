using Quartz;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Services.Product;
using System;
using System.Threading.Tasks;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Dedicated pricing-only sync job: SAP OPLN/ITM1 → SQLite PriceLists/ItemPriceLists
/// → Products compatibility projection → Neon mirror. Does not touch stock/inventory —
/// that remains ProductDeltaSyncJob/ProductFullSyncJob's responsibility.
///
/// Failure semantics are enforced inside ProductPriceListSyncService.FullSyncAsync:
/// a SAP read failure propagates here and fails the job run (existing cache is left
/// untouched); a Neon-only failure is swallowed there and logged as a warning, so this
/// job still completes normally and the next scheduled run retries the mirror.
/// </summary>
[DisallowConcurrentExecution]
public class ProductPriceListSyncJob : IJob
{
    private readonly ProductPriceListSyncService _priceSync;
    private readonly ILogger<ProductPriceListSyncJob> _logger;

    public ProductPriceListSyncJob(ProductPriceListSyncService priceSync, ILogger<ProductPriceListSyncJob> logger)
    {
        _priceSync = priceSync;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("💲 [ProductPriceListSyncJob] Triggered at {Time}", DateTime.Now);
        try
        {
            var result = await _priceSync.FullSyncAsync(context.CancellationToken);
            _logger.LogInformation(
                "✅ [ProductPriceListSyncJob] Completed in {Sec:0.0}s. SQLite: {PL} PriceLists, {IP} ItemPrices, {Proj} Products projected. " +
                "Neon: {NPL}/{NIP}/{NProj}. MirrorError={Err}",
                result.ElapsedSeconds, result.SqlitePriceListRows, result.SqliteItemPriceRows, result.SqliteProjectedRows,
                result.NeonPriceListRows, result.NeonItemPriceRows, result.NeonProjectedRows, result.NeonMirrorError ?? "none");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [ProductPriceListSyncJob] Failed — SAP read or SQLite write error. Existing cached prices left untouched.");
            throw;
        }
    }
}
