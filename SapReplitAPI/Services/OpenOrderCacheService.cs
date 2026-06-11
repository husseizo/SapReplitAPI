using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Orde_Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SapReplitAPI.Services
{
    public class OpenOrderCacheService
    {
        private readonly CacheDbContext _db;
        private readonly SapService _sap;
        private readonly ILogger<OpenOrderCacheService> _logger;
        private static readonly SemaphoreSlim _syncLock = new(1, 1);

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
                _logger.LogInformation("[OpenOrderCache] Syncing open sales orders from {From} to {To}...", fromDate, toDate);

                await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                await _db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

                var orders = _sap.GetOpenOrders(null, null, fromDate, toDate) ?? new List<OrderModel>();
                _logger.LogInformation("[OpenOrderCache] Retrieved {Count} open orders.", orders.Count);

                if (orders.Count == 0)
                {
                    _logger.LogInformation("[OpenOrderCache] No open orders found; clearing cache tables.");
                    await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""OpenOrderLines""; DELETE FROM ""OpenOrderHeaders"";");
                    await UpdateSyncMetadataAsync("OpenOrder");
                    sw.Stop();
                    _logger.LogInformation("[OpenOrderCache] Open order cache cleared in {Sec}s.", Math.Round(sw.Elapsed.TotalSeconds, 2));
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
                        OrderTotal = order.OrderValue,
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

                _logger.LogInformation("[OpenOrderCache] Caching {H} headers and {L} lines...", headerRows.Count, lineRows.Count);

                const int maxRetries = 3;
                for (var attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        using var tx = await _db.Database.BeginTransactionAsync();

                        await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""OpenOrderLines"";");
                        await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""OpenOrderHeaders"";");

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
                        break;
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                    {
                        if (attempt == maxRetries)
                            throw;

                        _logger.LogWarning(ex, "[OpenOrderCache] SQLite busy/locked during refresh. Retry {Attempt}/{MaxRetries}.", attempt, maxRetries);
                        await Task.Delay(200 * attempt);
                        _db.ChangeTracker.Clear();
                    }
                }

                await UpdateSyncMetadataAsync("OpenOrder");

                sw.Stop();
                _logger.LogInformation("[OpenOrderCache] Open order cache sync complete in {Sec}s.", Math.Round(sw.Elapsed.TotalSeconds, 2));
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "[OpenOrderCache] Error syncing open orders after {Sec}s.", Math.Round(sw.Elapsed.TotalSeconds, 2));
                throw;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private async Task UpdateSyncMetadataAsync(string type)
        {
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == type);
            if (meta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = type, LastSyncedAt = DateTime.UtcNow });
            else
                meta.LastSyncedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }
}
