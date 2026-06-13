using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Orde_Models; // ✅ OrderModel, OrderLineModel
using Serilog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

public class OrderCacheService
{
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly Serilog.ILogger _log = Log.ForContext<OrderCacheService>();
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public OrderCacheService(CacheDbContext db, SapService sap)
    {
        _db = db;
        _sap = sap;
    }

    // Helper: map SAP status code to human-readable text
    private static string MapStatus(string status)
    {
        return status switch
        {
            "O" => "Open",
            "C" => "Closed",
            _ => status ?? string.Empty
        };
    }

    #region FULL SYNC (chunked by month, UPSERT headers + lines)

    [SupportedOSPlatform("windows")]
    public async Task FullSyncOrdersAsync()
    {
        await _syncLock.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _log.Information("🚀 [OrderCache] Starting FULL order sync (open + closed)...");

            var yearStart = new DateTime(DateTime.Now.Year, 1, 1);
            var today = DateTime.Today;

            var totalHeaders = 0;
            var totalLines = 0;

            var cursor = yearStart;
            while (cursor <= today)
            {
                var from = cursor;
                var to = cursor.AddMonths(1).AddTicks(-1);
                if (to > today.AddDays(1).AddTicks(-1))
                    to = today.AddDays(1).AddTicks(-1);

                _log.Information("📅 [OrderCache] FULL window: {From} → {To}", from, to);

                // SapService returns List<OrderModel>
                var orders = _sap.GetAllOrders(null, null, from, to) ?? new List<OrderModel>();
                if (orders.Count == 0)
                {
                    _log.Information("ℹ️ [OrderCache] No orders for window {From} → {To}", from, to);
                }
                else
                {
                    var result = await RetryOnSqliteLockAsync(() => UpsertOrdersAndLinesAsync(orders));
                    totalHeaders += result.headerCount;
                    totalLines += result.lineCount;

                    _log.Information(
                        "📦 [OrderCache] Window {From}→{To}: {H} headers, {L} lines upserted.",
                        from.ToShortDateString(),
                        to.ToShortDateString(),
                        result.headerCount,
                        result.lineCount
                    );
                }

                cursor = cursor.AddMonths(1);
            }

            // Update sync metadata once at end
            var now = DateTime.Now;
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Order");
            if (meta == null)
            {
                await _db.SyncMetadata.AddAsync(new SyncMetadata
                {
                    Type = "Order",
                    LastSyncedAt = now
                });
            }
            else
            {
                meta.LastSyncedAt = now;
            }

            await _db.SaveChangesAsync();

            sw.Stop();
            _log.Information("✅ [OrderCache] FULL sync completed: {Orders} orders, {Lines} lines in {Sec:F2}s.",
                totalHeaders, totalLines, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error(ex, "❌ [OrderCache] FullSyncOrdersAsync failed after {Sec:F2}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region DELTA SYNC (incremental, UPSERT, minimal SAP load)

    [SupportedOSPlatform("windows")]
    public async Task SyncOrdersDeltaAsync(DateTime? from = null, DateTime? to = null, int batchSize = 200)
    {
        await _syncLock.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _log.Information("📦 [OrderCache] Starting DELTA sync for open + closed orders...");

            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Order");
            var syncFrom = from ?? meta?.LastSyncedAt ?? new DateTime(DateTime.Now.Year, 1, 1);
            var syncTo = to ?? DateTime.Today.AddDays(1).AddTicks(-1);

            _log.Information("🔍 [OrderCache] Delta window: {From} → {To}", syncFrom, syncTo);

            var orders = _sap.GetAllOrders(null, null, syncFrom, syncTo) ?? new List<OrderModel>();
            if (orders.Count == 0)
            {
                _log.Warning("⚠️ [OrderCache] No orders found between {From} and {To}.", syncFrom, syncTo);

                // Still bump metadata so we don't keep re-syncing the same empty window
                var now = DateTime.Now;
                if (meta == null)
                {
                    await _db.SyncMetadata.AddAsync(new SyncMetadata
                    {
                        Type = "Order",
                        LastSyncedAt = now
                    });
                }
                else
                {
                    meta.LastSyncedAt = now;
                }

                await _db.SaveChangesAsync();
                return;
            }

            _log.Information("📊 [OrderCache] Found {Count} orders in delta window.", orders.Count);

            var totalHeaders = 0;
            var totalLines = 0;

            for (int i = 0; i < orders.Count; i += batchSize)
            {
                var batch = orders.Skip(i).Take(batchSize).ToList();
                var result = await RetryOnSqliteLockAsync(() => UpsertOrdersAndLinesAsync(batch));
                totalHeaders += result.headerCount;
                totalLines += result.lineCount;

                _log.Information(
                    "💾 [OrderCache] Delta batch {Start}-{End}: {H} headers, {L} lines upserted.",
                    i + 1,
                    Math.Min(i + batchSize, orders.Count),
                    result.headerCount,
                    result.lineCount
                );
            }

            var now2 = DateTime.Now;
            if (meta == null)
            {
                await _db.SyncMetadata.AddAsync(new SyncMetadata
                {
                    Type = "Order",
                    LastSyncedAt = now2
                });
            }
            else
            {
                meta.LastSyncedAt = now2;
            }

            await _db.SaveChangesAsync();

            sw.Stop();
            _log.Information("✅ [OrderCache] DELTA sync completed: {H} headers, {L} lines in {Sec:F2}s.",
                totalHeaders, totalLines, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error(ex, "❌ [OrderCache] SyncOrdersDeltaAsync failed after {Sec:F2}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region INTERNAL UPSERT HELPERS (raw SQLite, batch, zero EF tracking)

    // Retries on SQLITE_BUSY (5) and SQLITE_LOCKED (6) — both are transient under concurrent writers.
    // busy_timeout only covers SQLITE_BUSY; SQLITE_LOCKED (shared-cache table locks) needs app-level retry.
    private static async Task<T> RetryOnSqliteLockAsync<T>(Func<Task<T>> action, int maxAttempts = 5)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt < maxAttempts)
            {
                int delayMs = 300 * attempt + Random.Shared.Next(0, 200);
                await Task.Delay(delayMs);
            }
        }
    }

    /// <summary>
    /// UPSERTs order headers and replaces lines for affected DocEntries.
    /// Uses synthetic LineNum = index in Lines list to form a stable (DocEntry, LineNum) key.
    /// </summary>
    private async Task<(int headerCount, int lineCount)> UpsertOrdersAndLinesAsync(List<OrderModel> orders)
    {
        if (orders == null || orders.Count == 0)
            return (0, 0);

        // Flatten headers
        var headerRows = orders.Select(o => new
        {
            o.DocEntry,
            o.DocNum,
            CardName = o.CustomerName ?? string.Empty,
            o.DocDate,
            OrderValue = o.OrderValue,
            Status = MapStatus(o.Status ?? string.Empty),
            o.SlpCode,
            SlpName = o.SlpName ?? string.Empty
        }).ToList();

        // Flatten lines, generate LineNum from index
        var lineRows = new List<dynamic>(capacity: orders.Sum(o => o.Lines?.Count ?? 0));
        foreach (var o in orders)
        {
            var lines = o.Lines ?? new List<OrderLineModel>();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                lineRows.Add(new
                {
                    o.DocEntry,
                    LineNum = i, // synthetic but stable per DocEntry
                    o.DocDate,
                    ItemCode = l.ItemCode ?? string.Empty,
                    Dscription = l.Dscription ?? string.Empty,
                    Quantity = l.Quantity,
                    Price = l.Price,
                    WhsCode = l.WhsCode ?? "001",
                    U_ItemName = l.U_ItemName ?? string.Empty,
                    U_Manufacturer = l.U_Manufacturer ?? string.Empty
                });
            }
        }

        var docEntries = headerRows.Select(h => h.DocEntry).Distinct().ToList();
        var headerCount = headerRows.Count;
        var lineCount = lineRows.Count;

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        // Optionally enforce WAL + busy timeout for safety
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=15000;";
            await pragmaCmd.ExecuteNonQueryAsync();
        }

        using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var sqliteConn = (SqliteConnection)connection;
            var sqliteTx = (SqliteTransaction)tx.GetDbTransaction();

            // 1) UPSERT HEADERS
            using (var cmd = sqliteConn.CreateCommand())
            {
                cmd.Transaction = sqliteTx;
                cmd.CommandText = @"
INSERT INTO ""OrderHeaders""
    (""DocEntry"", ""DocNum"", ""CardName"", ""DocDate"", ""OrderValue"", ""Status"", ""SlpCode"", ""SlpName"", ""CancellationStatus"")
VALUES
    ($DocEntry, $DocNum, $CardName, $DocDate, $OrderValue, $Status, $SlpCode, $SlpName, $CancellationStatus)
ON CONFLICT(""DocEntry"") DO UPDATE SET
    ""DocNum"" = excluded.""DocNum"",
    ""CardName"" = excluded.""CardName"",
    ""DocDate"" = excluded.""DocDate"",
    ""OrderValue"" = excluded.""OrderValue"",
    ""Status"" = excluded.""Status"",
    ""SlpCode"" = excluded.""SlpCode"",
    ""SlpName"" = excluded.""SlpName"",
    ""CancellationStatus"" = excluded.""CancellationStatus"";";

                var pDocEntry = cmd.Parameters.Add("$DocEntry", SqliteType.Integer);
                var pDocNum = cmd.Parameters.Add("$DocNum", SqliteType.Integer);
                var pCardName = cmd.Parameters.Add("$CardName", SqliteType.Text);
                var pDocDate = cmd.Parameters.Add("$DocDate", SqliteType.Text);
                var pOrderValue = cmd.Parameters.Add("$OrderValue", SqliteType.Real);
                var pStatus = cmd.Parameters.Add("$Status", SqliteType.Text);
                var pSlpCode = cmd.Parameters.Add("$SlpCode", SqliteType.Integer);
                var pSlpName = cmd.Parameters.Add("$SlpName", SqliteType.Text);
                var pCancellationStatus = cmd.Parameters.Add("$CancellationStatus", SqliteType.Text);

                foreach (var h in headerRows)
                {
                    pDocEntry.Value = h.DocEntry;
                    pDocNum.Value = h.DocNum;
                    pCardName.Value = h.CardName ?? string.Empty;
                    pDocDate.Value = h.DocDate.ToString("yyyy-MM-dd");
                    pOrderValue.Value = h.OrderValue;
                    pStatus.Value = h.Status ?? string.Empty;
                    pSlpCode.Value = h.SlpCode;
                    pSlpName.Value = h.SlpName ?? string.Empty;
                    pCancellationStatus.Value = string.Empty;

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // 2) Replace existing lines for these DocEntries (to avoid stale removed lines)
            if (docEntries.Count > 0)
            {
                using (var delCmd = sqliteConn.CreateCommand())
                {
                    delCmd.Transaction = sqliteTx;
                    delCmd.CommandText = @"DELETE FROM ""OrderLines"" WHERE ""DocEntry"" = $DocEntry;";
                    var pDelDocEntry = delCmd.Parameters.Add("$DocEntry", SqliteType.Integer);

                    foreach (var de in docEntries)
                    {
                        pDelDocEntry.Value = de;
                        await delCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            // 3) UPSERT LINES (DocEntry + LineNum)
            if (lineRows.Count > 0)
            {
                using (var cmdLines = sqliteConn.CreateCommand())
                {
                    cmdLines.Transaction = sqliteTx;
                    cmdLines.CommandText = @"
INSERT INTO ""OrderLines""
    (""DocEntry"", ""LineNum"", ""DocDate"", ""ItemCode"", ""Dscription"", ""Quantity"", ""Price"",
     ""WhsCode"", ""U_ItemName"", ""U_Manufacturer"")
VALUES
    ($DocEntry, $LineNum, $DocDate, $ItemCode, $Dscription, $Quantity, $Price,
     $WhsCode, $U_ItemName, $U_Manufacturer)
ON CONFLICT(""DocEntry"", ""LineNum"") DO UPDATE SET
    ""DocDate"" = excluded.""DocDate"",
    ""ItemCode"" = excluded.""ItemCode"",
    ""Dscription"" = excluded.""Dscription"",
    ""Quantity"" = excluded.""Quantity"",
    ""Price"" = excluded.""Price"",
    ""WhsCode"" = excluded.""WhsCode"",
    ""U_ItemName"" = excluded.""U_ItemName"",
    ""U_Manufacturer"" = excluded.""U_Manufacturer"";";

                    var pDocEntryL = cmdLines.Parameters.Add("$DocEntry", SqliteType.Integer);
                    var pLineNum = cmdLines.Parameters.Add("$LineNum", SqliteType.Integer);
                    var pDocDateL = cmdLines.Parameters.Add("$DocDate", SqliteType.Text);
                    var pItemCode = cmdLines.Parameters.Add("$ItemCode", SqliteType.Text);
                    var pDscription = cmdLines.Parameters.Add("$Dscription", SqliteType.Text);
                    var pQuantity = cmdLines.Parameters.Add("$Quantity", SqliteType.Real);
                    var pPrice = cmdLines.Parameters.Add("$Price", SqliteType.Real);
                    var pWhsCode = cmdLines.Parameters.Add("$WhsCode", SqliteType.Text);
                    var pUItemName = cmdLines.Parameters.Add("$U_ItemName", SqliteType.Text);
                    var pUManufacturer = cmdLines.Parameters.Add("$U_Manufacturer", SqliteType.Text);

                    foreach (var l in lineRows)
                    {
                        pDocEntryL.Value = l.DocEntry;
                        pLineNum.Value = l.LineNum;
                        pDocDateL.Value = l.DocDate.ToString("yyyy-MM-dd");
                        pItemCode.Value = l.ItemCode ?? string.Empty;
                        pDscription.Value = l.Dscription ?? string.Empty;
                        pQuantity.Value = l.Quantity;
                        pPrice.Value = l.Price;
                        pWhsCode.Value = l.WhsCode ?? "001";
                        pUItemName.Value = l.U_ItemName ?? string.Empty;
                        pUManufacturer.Value = l.U_Manufacturer ?? string.Empty;

                        await cmdLines.ExecuteNonQueryAsync();
                    }
                }
            }

            await tx.CommitAsync();
        }

        return (headerCount, lineCount);
    }

    #endregion

    #region READ / QUERY HELPERS (same as before, with AsNoTracking for speed)

    public List<CachedOrder> GetCachedOrderHeaders(
        string? status = null,
        int? slpCode = null,
        string? customer = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var query = _db.OrderHeaders.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status.ToLower() == status.ToLower());

        if (slpCode.HasValue)
            query = query.Where(x => x.SlpCode == slpCode.Value);

        if (!string.IsNullOrWhiteSpace(customer))
            query = query.Where(x => x.CardName.ToLower().Contains(customer.ToLower()));

        if (fromDate.HasValue)
            query = query.Where(x => x.DocDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(x => x.DocDate <= toDate.Value);

        return query.OrderByDescending(x => x.DocDate).ToList();
    }

    public List<CachedOrderLine> GetCachedOrderLines(
        int? docEntry = null,
        string? itemCode = null,
        string? warehouse = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var query = _db.OrderLines.AsNoTracking().AsQueryable();

        if (docEntry.HasValue)
            query = query.Where(x => x.DocEntry == docEntry.Value);

        if (!string.IsNullOrWhiteSpace(itemCode))
            query = query.Where(x => x.ItemCode.ToLower().Contains(itemCode.ToLower()));

        if (!string.IsNullOrWhiteSpace(warehouse))
            query = query.Where(x => x.WhsCode.ToLower() == warehouse.ToLower());

        if (fromDate.HasValue)
            query = query.Where(x => x.DocDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(x => x.DocDate <= toDate.Value);

        return query
            .OrderBy(x => x.DocEntry)
            .ThenBy(x => x.ItemCode)
            .ToList();
    }

    public List<CachedOrderLine> SearchOpenOrderLines(string searchTerm)
    {
        var lowered = searchTerm.ToLower();
        var currentYear = DateTime.Today.Year;
        var fromDate = new DateTime(currentYear, 1, 1);

        var openDocEntries = _db.OrderHeaders
            .AsNoTracking()
            .Where(h =>
                h.DocDate >= fromDate &&
                (h.Status == "O" ||
                 h.Status.Contains("Waiting") ||
                 h.Status.Equals("Open")))
            .Select(h => h.DocEntry)
            .ToHashSet();

        var query = _db.OrderLines.AsNoTracking().AsQueryable();

        var results = query
            .Where(l =>
                openDocEntries.Contains(l.DocEntry) &&
                (
                    (!string.IsNullOrEmpty(l.Dscription) && l.Dscription.ToLower().Contains(lowered)) ||
                    l.DocEntry.ToString().Contains(lowered) ||
                    (!string.IsNullOrEmpty(l.U_ItemName) && l.U_ItemName.ToLower().Contains(lowered)) ||
                    (!string.IsNullOrEmpty(l.U_Manufacturer) && l.U_Manufacturer.ToLower().Contains(lowered))
                ))
            .OrderByDescending(l => l.DocEntry)
            .ToList();

        return results;
    }

    public List<CachedOrder> SearchOpenOrderHeadersPaged(string searchTerm, int page = 1, int pageSize = 100)
    {
        var lowered = searchTerm.ToLower();
        var fromDate = new DateTime(DateTime.Now.Year, 1, 1);

        var query = _db.OrderHeaders.AsNoTracking().AsQueryable();

        query = query.Where(h =>
            h.DocDate >= fromDate &&
            (h.Status == "O" || h.Status.Contains("Waiting") || h.Status.Equals("Open")) &&
            (
                (!string.IsNullOrEmpty(h.CardName) && h.CardName.ToLower().Contains(lowered)) ||
                h.DocNum.ToString().Contains(lowered) ||
                h.DocEntry.ToString().Contains(lowered) ||
                (!string.IsNullOrEmpty(h.Status) && h.Status.ToLower().Contains(lowered)) ||
                h.SlpCode.ToString().Contains(lowered) ||
                (!string.IsNullOrEmpty(h.SlpName) && h.SlpName.ToLower().Contains(lowered))
            ));

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 1000);

        return query
            .OrderByDescending(h => h.DocDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    #endregion
}