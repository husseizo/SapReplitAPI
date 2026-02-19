using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Orde_Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SapReplitAPI.Services
{
    public class OpenOrderCacheService
    {
        private readonly CacheDbContext _db;
        private readonly SapService _sap;
        private readonly ILogger<OpenOrderCacheService> _logger;
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        public OpenOrderCacheService(CacheDbContext db, SapService sap, ILogger<OpenOrderCacheService> logger)
        {
            _db = db;
            _sap = sap;
            _logger = logger;
        }

        public async Task SyncOpenOrdersAsync()
        {
            await _syncLock.WaitAsync();
            var sw = Stopwatch.StartNew();

            try
            {
                var fromDate = new DateTime(2024, 1, 1);
                var toDate = DateTime.Today;
                _logger.LogInformation("🔄 Syncing open sales orders from {From} to {To}...", fromDate, toDate);

                // Pull from SAP (SapService ensures connection)
                var orders = _sap.GetOpenOrders(null, null, fromDate, toDate) ?? new List<OrderModel>();
                _logger.LogInformation("📦 Retrieved {Count} open orders.", orders.Count);

                // If nothing to sync, clear cache and return
                if (orders.Count == 0)
                {
                    _logger.LogInformation("No open orders found; clearing cache tables.");
                    await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""OpenOrderLines""; DELETE FROM ""OpenOrderHeaders"";");
                    sw.Stop();
                    _logger.LogInformation("✅ Open order cache cleared in {Sec}s.", Math.Round(sw.Elapsed.TotalSeconds, 2));
                    return;
                }

                var headerRows = new List<CachedOpenOrder>(orders.Count);
                var lineRows = new List<CachedOpenOrderLine>();

                foreach (var order in orders)
                {
                    headerRows.Add(new CachedOpenOrder
                    {
                        DocEntry = order.DocEntry,
                        DocNum = order.DocNum,
                        CardCode = order.CustomerCode ?? string.Empty,
                        CardName = order.CustomerName ?? string.Empty,
                        DocDate = order.DocDate,
                        OrderTotal = order.OrderValue,              // matches your model rename
                        Status = order.Status ?? string.Empty,
                        SlpCode = order.SlpCode,
                        SlpName = order.SlpName ?? string.Empty
                    });

                    var lines = order.Lines ?? new List<OrderLineModel>();
                    var lineNum = 0;

                    foreach (var line in lines)
                    {
                        lineRows.Add(new CachedOpenOrderLine
                        {
                            DocEntry = order.DocEntry,
                            LineNum = lineNum++,
                            ItemCode = line.ItemCode ?? string.Empty,
                            Dscription = line.Dscription ?? string.Empty,
                            Quantity = line.Quantity,
                            Price = line.Price,
                            LineTotal = line.Price * line.Quantity,
                            WhsCode = line.WhsCode ?? string.Empty,
                            DocDate = order.DocDate
                        });
                    }
                }

                _logger.LogInformation("💾 Caching {H} headers and {L} lines...", headerRows.Count, lineRows.Count);

                using var tx = await _db.Database.BeginTransactionAsync();

                // Fast clear
                await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""OpenOrderLines"";");
                await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""OpenOrderHeaders"";");

                // Speed up bulk insert
                var oldAutoDetect = _db.ChangeTracker.AutoDetectChangesEnabled;
                _db.ChangeTracker.AutoDetectChangesEnabled = false;
                try
                {
                    await _db.OpenOrderHeaders.AddRangeAsync(headerRows);
                    await _db.OpenOrderLines.AddRangeAsync(lineRows);
                    await _db.SaveChangesAsync();
                }
                finally
                {
                    _db.ChangeTracker.AutoDetectChangesEnabled = oldAutoDetect;
                }

                await tx.CommitAsync();

                sw.Stop();
                _logger.LogInformation("✅ Open order cache sync complete in {Sec}s.", Math.Round(sw.Elapsed.TotalSeconds, 2));
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "❌ Error syncing open orders (after {Sec}s).", Math.Round(sw.Elapsed.TotalSeconds, 2));
                throw; // keep throwing so callers/Quartz see the failure
            }
            finally
            {
                _syncLock.Release();
            }
        }
    }
}