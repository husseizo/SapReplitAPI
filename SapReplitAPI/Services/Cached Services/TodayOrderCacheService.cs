#pragma warning disable CA1416 // Possible null argument

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SAPbobsCOM;

public class TodayOrderCacheService
{
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly ILogger<TodayOrderCacheService> _logger;
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public TodayOrderCacheService(CacheDbContext db, SapService sap, ILogger<TodayOrderCacheService> logger)
    {
        _db = db;
        _sap = sap;
        _logger = logger;
    }

    public async Task RefreshTodayOrdersFromSAP()
    {
        Recordset? rs = null;
        Recordset? rsLines = null;
        var sw = Stopwatch.StartNew();

        await _syncLock.WaitAsync();

        try
        {
            var company = typeof(SapService)
                .GetMethod("GetConnectedCompany", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(_sap, Array.Empty<object>()) as Company;

            if (company == null)
                throw new InvalidOperationException("Could not resolve a connected SAP company instance.");

            var today = DateTime.Today;
            var todayStr = today.ToString("yyyy-MM-dd");

            var headers = new List<CachedTodayOrder>();
            var lines = new List<CachedTodayOrderLine>();
            var docEntryList = new List<int>();

            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            var headerQuery = $@"
SELECT O.DocEntry, O.DocNum, O.CardName, O.DocDate, O.DocTotal, O.DocStatus, O.SlpCode,
       O.CANCELED, S.SlpName
FROM ORDR O
LEFT JOIN OSLP S ON O.SlpCode = S.SlpCode
WHERE CAST(O.DocDate AS DATE) = '{todayStr}'";

            _logger.LogDebug("[TodayOrderCache] Running today-order header query:\n{Query}", headerQuery);
            rs.DoQuery(headerQuery);

            while (!rs.EoF)
            {
                var docEntry = Convert.ToInt32(rs.Fields.Item("DocEntry").Value);
                var docStatus = rs.Fields.Item("DocStatus").Value is DBNull
                    ? "O"
                    : rs.Fields.Item("DocStatus").Value.ToString();

                var canceledRaw = rs.Fields.Item("CANCELED").Value is DBNull
                    ? "N"
                    : rs.Fields.Item("CANCELED").Value.ToString();
                var canceled = (canceledRaw ?? "N").Trim().ToUpperInvariant();

                var status = canceled == "Y"
                    ? "Cancelled"
                    : string.Equals(docStatus, "C", StringComparison.OrdinalIgnoreCase) ? "Delivered" : "Open";

                headers.Add(new CachedTodayOrder
                {
                    DocEntry = docEntry,
                    DocNum = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    CardName = rs.Fields.Item("CardName").Value is DBNull ? "" : rs.Fields.Item("CardName").Value.ToString(),
                    DocDate = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    OrderValue = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                    Status = status,
                    SlpCode = rs.Fields.Item("SlpCode").Value is DBNull ? null : Convert.ToInt32(rs.Fields.Item("SlpCode").Value),
                    SlpName = rs.Fields.Item("SlpName").Value is DBNull ? "" : rs.Fields.Item("SlpName").Value.ToString(),
                    Cancelled = canceled == "Y"
                });

                docEntryList.Add(docEntry);
                rs.MoveNext();
            }

            if (docEntryList.Count > 0)
            {
                rsLines = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                var lineQuery = $@"
SELECT R.DocEntry, R.ItemCode, R.Dscription, R.Quantity, R.Price, R.WhsCode, R.DocDate,
       R.U_ItemName, R.U_Manufacturer
FROM RDR1 R
WHERE R.DocEntry IN ({string.Join(",", docEntryList)})";

                _logger.LogDebug("[TodayOrderCache] Running today-order line query:\n{Query}", lineQuery);
                rsLines.DoQuery(lineQuery);

                while (!rsLines.EoF)
                {
                    lines.Add(new CachedTodayOrderLine
                    {
                        DocEntry = Convert.ToInt32(rsLines.Fields.Item("DocEntry").Value),
                        ItemCode = rsLines.Fields.Item("ItemCode").Value is DBNull ? "" : rsLines.Fields.Item("ItemCode").Value.ToString(),
                        Dscription = rsLines.Fields.Item("Dscription").Value is DBNull ? "" : rsLines.Fields.Item("Dscription").Value.ToString(),
                        Quantity = Convert.ToDecimal(rsLines.Fields.Item("Quantity").Value),
                        Price = Convert.ToDecimal(rsLines.Fields.Item("Price").Value),
                        WhsCode = rsLines.Fields.Item("WhsCode").Value is DBNull ? "" : rsLines.Fields.Item("WhsCode").Value.ToString(),
                        U_ItemName = rsLines.Fields.Item("U_ItemName").Value is DBNull ? "" : rsLines.Fields.Item("U_ItemName").Value.ToString(),
                        U_Manufacturer = rsLines.Fields.Item("U_Manufacturer").Value is DBNull ? "" : rsLines.Fields.Item("U_Manufacturer").Value.ToString(),
                        DocDate = Convert.ToDateTime(rsLines.Fields.Item("DocDate").Value)
                    });

                    rsLines.MoveNext();
                }
            }

            await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await _db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var tx = await _db.Database.BeginTransactionAsync();

                    _logger.LogInformation("[TodayOrderCache] Replacing today's orders snapshot in SQLite. Headers={HeaderCount}, Lines={LineCount}", headers.Count, lines.Count);
                    await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"TodayOrderLines\"");
                    await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"TodayOrderHeaders\"");

                    if (headers.Count > 0)
                        await _db.TodayOrderHeaders.AddRangeAsync(headers);

                    if (lines.Count > 0)
                        await _db.TodayOrderLines.AddRangeAsync(lines);

                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                    break;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    if (attempt == maxRetries)
                        throw;

                    _logger.LogWarning(ex, "[TodayOrderCache] SQLite busy/locked during refresh. Retry {Attempt}/{MaxRetries}.", attempt, maxRetries);
                    await Task.Delay(200 * attempt);
                    _db.ChangeTracker.Clear();
                }
            }

            await UpdateSyncMetadataAsync("TodayOrder");

            sw.Stop();
            _logger.LogInformation("[TodayOrderCache] Refreshed today's orders in {DurationSeconds:F2}s. Headers={HeaderCount}, Lines={LineCount}",
                sw.Elapsed.TotalSeconds, headers.Count, lines.Count);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[TodayOrderCache] Failed to refresh today's orders after {DurationSeconds:F2}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            if (rs != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
            if (rsLines != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rsLines);
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
