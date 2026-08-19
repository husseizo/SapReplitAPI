using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Inventory;
using System.Diagnostics;

namespace SapReplitAPI.Services.Inventory;

public class WarehouseInventorySyncService
{
    // One lock shared by Full + Delta — they must never run concurrently.
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly string[] ConfiguredWarehouses = { "001", "002", "003", "004" };
    private const string WatermarkKey = "WarehouseInventory.Source";

    // 2-hour safety overlap on the watermark to handle clock drift and partial commits
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromHours(2);

    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly ILogger<WarehouseInventorySyncService> _log;

    public WarehouseInventorySyncService(
        CacheDbContext db,
        SapService sap,
        ILogger<WarehouseInventorySyncService> log)
    {
        _db  = db;
        _sap = sap;
        _log = log;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<(int Upserted, int Removed)> FullSyncAsync()
    {
        await _lock.WaitAsync();
        var sw = Stopwatch.StartNew();
        try
        {
            _log.LogInformation("[WHInv] Full sync started.");

            await SetPragmasAsync();

            // 1. Fetch complete SAP snapshot (no row filter — zero-stock included)
            var rows = _sap.GetWarehouseInventorySnapshot();
            _log.LogInformation("[WHInv] SAP returned {Count} OITW rows.", rows.Count);

            // 2. Build key set for stale detection
            var sapKeys = rows
                .Select(r => (IC: r.ItemCode.ToUpperInvariant(), WC: r.WhsCode.ToUpperInvariant()))
                .ToHashSet();

            var syncTime = DateTime.UtcNow;

            // 3. Upsert all SAP rows
            int upserted = await UpsertRowsAsync(rows, syncTime);

            // 4. Remove stale SQLite rows (configured warehouses only)
            int removed = await RemoveGlobalStaleAsync(sapKeys);

            // 5. Advance watermark — only after successful persistence
            await UpdateWatermarkAsync(syncTime);

            sw.Stop();
            _log.LogInformation("[WHInv] Full sync done: {U} upserted, {R} removed in {S:F1}s.",
                upserted, removed, sw.Elapsed.TotalSeconds);

            return (upserted, removed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[WHInv] Full sync failed.");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(int Upserted, int Removed)> DeltaSyncAsync()
    {
        await _lock.WaitAsync();
        var sw = Stopwatch.StartNew();
        try
        {
            // 1. Determine effective watermark with lookback overlap
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == WatermarkKey);
            var lastSync = meta?.LastSyncedAt ?? DateTime.UtcNow.AddDays(-7);
            var effectiveFrom = lastSync - LookbackWindow;

            _log.LogInformation("[WHInv] Delta sync from {From} (watermark {Last} - 2h).",
                effectiveFrom.ToString("yyyy-MM-dd HH:mm:ss"), lastSync.ToString("yyyy-MM-dd HH:mm:ss"));

            await SetPragmasAsync();

            // 2. Collect affected ItemCodes from OINM + ORDR/RDR1 + OITM
            var detection = _sap.GetChangedWarehouseItemCodes(effectiveFrom);

            _log.LogInformation(
                "[WHInv] Delta detection — effectiveFrom: {From} | OINM: {Oinm} | ORDR: {Ordr} | OITM: {Oitm} | Distinct: {Total}",
                effectiveFrom.ToString("yyyy-MM-dd HH:mm:ss"),
                detection.OinmCount, detection.OrdrCount, detection.OitmCount, detection.TotalDistinct);

            if (detection.TotalDistinct == 0)
            {
                _log.LogInformation("[WHInv] Delta: no changed items detected — watermark advanced.");
                await UpdateWatermarkAsync(DateTime.UtcNow);
                return (0, 0);
            }

            // 3. Fetch current SAP snapshot for those items (batched in 200s)
            var rows = _sap.GetWarehouseInventorySnapshotForItems(detection.ItemCodes);

            _log.LogInformation("[WHInv] Delta snapshot — {SnapshotRows} OITW rows fetched for {ChangedItems} changed items.",
                rows.Count, detection.TotalDistinct);

            // 4. Build (ItemCode → set of WhsCodes) map from fresh SAP rows
            var freshWhsByItem = rows
                .GroupBy(r => r.ItemCode.ToUpperInvariant())
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => r.WhsCode.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase));

            var syncTime = DateTime.UtcNow;

            // 5. Upsert returned rows
            int upserted = await UpsertRowsAsync(rows, syncTime);

            // 6. Remove stale rows for affected items (warehouse rows no longer in SAP)
            int removed = await RemoveStaleForItemsAsync(detection.ItemCodes, freshWhsByItem);

            // 7. Advance watermark — only after successful persistence
            await UpdateWatermarkAsync(syncTime);

            sw.Stop();
            _log.LogInformation(
                "[WHInv] Delta done — upserted: {U} | removed: {R} | snapshot rows: {SR} | duration: {S:F1}s",
                upserted, removed, rows.Count, sw.Elapsed.TotalSeconds);

            return (upserted, removed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[WHInv] Delta sync failed.");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private: upsert ───────────────────────────────────────────────────────

    private async Task<int> UpsertRowsAsync(List<WarehouseInventoryRow> rows, DateTime syncTime)
    {
        if (rows.Count == 0) return 0;

        var sqliteConn = (SqliteConnection)_db.Database.GetDbConnection();
        if (sqliteConn.State != System.Data.ConnectionState.Open)
            await sqliteConn.OpenAsync();

        using var sqliteTx = sqliteConn.BeginTransaction();
        using var cmd = sqliteConn.CreateCommand();
        cmd.Transaction = sqliteTx;
        cmd.CommandText = @"
INSERT INTO WarehouseInventory
    (ItemCode, WhsCode, WarehouseName, OnHand, IsCommitted, OnOrder, AvailableToSell, IsBinManaged, LastUpdated)
VALUES
    ($ItemCode, $WhsCode, $WarehouseName, $OnHand, $IsCommitted, $OnOrder, $AvailableToSell, $IsBinManaged, $LastUpdated)
ON CONFLICT(ItemCode, WhsCode) DO UPDATE SET
    WarehouseName   = excluded.WarehouseName,
    OnHand          = excluded.OnHand,
    IsCommitted     = excluded.IsCommitted,
    OnOrder         = excluded.OnOrder,
    AvailableToSell = excluded.AvailableToSell,
    IsBinManaged    = excluded.IsBinManaged,
    LastUpdated     = excluded.LastUpdated";

        var pItem    = cmd.Parameters.Add("$ItemCode",        SqliteType.Text);
        var pWhs     = cmd.Parameters.Add("$WhsCode",         SqliteType.Text);
        var pName    = cmd.Parameters.Add("$WarehouseName",   SqliteType.Text);
        var pOnHand  = cmd.Parameters.Add("$OnHand",          SqliteType.Real);
        var pCommit  = cmd.Parameters.Add("$IsCommitted",     SqliteType.Real);
        var pOnOrd   = cmd.Parameters.Add("$OnOrder",         SqliteType.Real);
        var pAvail   = cmd.Parameters.Add("$AvailableToSell", SqliteType.Real);
        var pBin     = cmd.Parameters.Add("$IsBinManaged",    SqliteType.Integer);
        var pUpdated = cmd.Parameters.Add("$LastUpdated",     SqliteType.Text);

        var syncTimeStr = syncTime.ToString("yyyy-MM-dd HH:mm:ss");
        int count = 0;

        foreach (var r in rows)
        {
            pItem.Value    = r.ItemCode;
            pWhs.Value     = r.WhsCode;
            pName.Value    = r.WarehouseName;
            pOnHand.Value  = (double)r.OnHand;
            pCommit.Value  = (double)r.IsCommitted;
            pOnOrd.Value   = (double)r.OnOrder;
            pAvail.Value   = (double)r.AvailableToSell;
            pBin.Value     = r.IsBinManaged ? 1 : 0;
            pUpdated.Value = syncTimeStr;

            await cmd.ExecuteNonQueryAsync();
            count++;
        }

        sqliteTx.Commit();
        return count;
    }

    // ── Private: stale-row removal ────────────────────────────────────────────

    /// <summary>Full sync: removes cached rows whose (ItemCode, WhsCode) is no longer in the SAP snapshot.</summary>
    private async Task<int> RemoveGlobalStaleAsync(
        HashSet<(string IC, string WC)> sapKeys)
    {
        // Load keys currently cached for configured warehouses
        var cached = await _db.WarehouseInventories
            .Where(w => ConfiguredWarehouses.Contains(w.WhsCode))
            .Select(w => new { w.Id, IC = w.ItemCode.ToUpper(), WC = w.WhsCode.ToUpper() })
            .ToListAsync();

        var staleIds = cached
            .Where(c => !sapKeys.Contains((c.IC, c.WC)))
            .Select(c => c.Id)
            .ToList();

        if (staleIds.Count == 0) return 0;

        var stale = await _db.WarehouseInventories
            .Where(w => staleIds.Contains(w.Id))
            .ToListAsync();

        _db.WarehouseInventories.RemoveRange(stale);
        await _db.SaveChangesAsync();

        _log.LogInformation("[WHInv] Removed {Count} global stale rows.", stale.Count);
        return stale.Count;
    }

    /// <summary>
    /// Delta sync: for each affected ItemCode, removes cached rows for configured warehouses
    /// that are no longer present in the fresh SAP snapshot for that item.
    /// </summary>
    private async Task<int> RemoveStaleForItemsAsync(
        IReadOnlyCollection<string> affectedCodes,
        Dictionary<string, HashSet<string>> freshWhsByItem)
    {
        int total = 0;

        // For each changed item, find cached whs rows that SAP no longer returns
        foreach (var code in affectedCodes)
        {
            var upperCode = code.ToUpperInvariant();
            var freshWhs = freshWhsByItem.TryGetValue(upperCode, out var whs)
                ? whs
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Configured warehouses NOT returned by SAP for this item → stale
            var staleWhs = ConfiguredWarehouses
                .Where(w => !freshWhs.Contains(w))
                .ToList();

            if (staleWhs.Count == 0) continue;

            var stale = await _db.WarehouseInventories
                .Where(w => w.ItemCode == code && staleWhs.Contains(w.WhsCode))
                .ToListAsync();

            if (stale.Count == 0) continue;

            _db.WarehouseInventories.RemoveRange(stale);
            total += stale.Count;
        }

        if (total > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation("[WHInv] Removed {Count} stale warehouse rows for delta items.", total);
        }

        return total;
    }

    // ── Private: watermark ────────────────────────────────────────────────────

    private async Task UpdateWatermarkAsync(DateTime at)
    {
        var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == WatermarkKey);
        if (meta is null)
            await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = WatermarkKey, LastSyncedAt = at });
        else
            meta.LastSyncedAt = at;

        await _db.SaveChangesAsync();
    }

    private async Task SetPragmasAsync()
    {
        await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await _db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
    }
}
