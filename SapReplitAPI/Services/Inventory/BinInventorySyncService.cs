using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Inventory;
using System.Diagnostics;

namespace SapReplitAPI.Services.Inventory;

public class BinInventorySyncService
{
    // Full + Delta bin syncs must not run concurrently.
    // This lock is separate from WarehouseInventorySyncService._lock — the two pipelines
    // write to different SQLite tables and there is no strong reason to serialize them.
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly string[] ConfiguredWarehouses = { "001", "002", "003", "004" };
    private const  string            WatermarkKey         = "BinInventory.Source";
    private static readonly TimeSpan LookbackWindow       = TimeSpan.FromHours(2);

    private readonly CacheDbContext                    _db;
    private readonly SapService                        _sap;
    private readonly ILogger<BinInventorySyncService>  _log;

    public BinInventorySyncService(
        CacheDbContext db,
        SapService sap,
        ILogger<BinInventorySyncService> log)
    {
        _db  = db;
        _sap = sap;
        _log = log;
    }

    // ── Public: Full sync ─────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the complete positive-stock OIBQ snapshot from SAP,
    /// upserts all rows, deletes globally stale rows, advances the watermark,
    /// and returns a full diagnostic report including reconciliation vs WarehouseInventory.
    /// </summary>
    public async Task<BinSyncReport> FullSyncAsync()
    {
        await _lock.WaitAsync();
        var sw = Stopwatch.StartNew();
        try
        {
            _log.LogInformation("[BinInv] Full sync started.");

            await SetPragmasAsync();

            // 1. Fetch complete positive-stock OIBQ snapshot for 001-004 (BinActivat='Y')
            var rows    = _sap.GetBinInventorySnapshot();
            var syncTime = DateTime.UtcNow;

            var byWhs = ConfiguredWarehouses.ToDictionary(
                w => w,
                w => rows.Count(r => r.WhsCode == w));

            var distinctItems = rows
                .Select(r => r.ItemCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            _log.LogInformation(
                "[BinInv] SAP snapshot — Total: {Total} | 001:{W001} 002:{W002} 003:{W003} 004:{W004} | Distinct items: {Items}",
                rows.Count, byWhs["001"], byWhs["002"], byWhs["003"], byWhs["004"], distinctItems);

            // 2. Build key set for stale detection
            var sapKeys = rows
                .Select(r => (IC: r.ItemCode.ToUpperInvariant(), WC: r.WhsCode, BA: r.BinAbsEntry))
                .ToHashSet();

            // 3. Upsert all rows inside one SQLite transaction
            int upserted = await UpsertRowsAsync(rows, syncTime);

            // 4. Delete stale rows (bins no longer positive in SAP)
            int removed = await RemoveGlobalStaleAsync(sapKeys);

            // 5. Advance watermark — only after successful persistence
            await UpdateWatermarkAsync(syncTime);

            sw.Stop();

            // 6. Reconciliation vs WarehouseInventory.OnHand
            var recon = await BuildReconciliationReportAsync();

            _log.LogInformation(
                "[BinInv] Full sync complete — upserted: {U} | removed: {R} | duration: {S:F1}s",
                upserted, removed, sw.Elapsed.TotalSeconds);
            _log.LogInformation(
                "[BinInv] Reconciliation — checked: {C} | matched: {M} | mismatched: {MM}",
                recon.Checked, recon.Matched, recon.Mismatched);

            return new BinSyncReport(
                TotalSapRows:     rows.Count,
                DistinctItemCodes: distinctItems,
                RowsByWarehouse:  byWhs,
                Upserted:         upserted,
                Removed:          removed,
                SyncTime:         syncTime,
                DurationSeconds:  sw.Elapsed.TotalSeconds,
                Reconciliation:   recon);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BinInv] Full sync failed.");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Public: Delta sync ────────────────────────────────────────────────────

    /// <summary>
    /// Detects physically changed ItemCodes via OINM + OITM (ORDR excluded — SO commitment
    /// does not alter bin-level OnHandQty), fetches current bin snapshots for those items,
    /// upserts updated rows, deletes stale bin rows, and advances the watermark.
    /// </summary>
    public async Task<(int Upserted, int Removed)> DeltaSyncAsync()
    {
        await _lock.WaitAsync();
        var sw = Stopwatch.StartNew();
        try
        {
            // 1. Watermark with 2-hour lookback overlap
            var meta        = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == WatermarkKey);
            var lastSync    = meta?.LastSyncedAt ?? DateTime.UtcNow.AddDays(-7);
            var effectiveFrom = lastSync - LookbackWindow;

            _log.LogInformation("[BinInv] Delta from {From} (watermark {Last} - 2h).",
                effectiveFrom.ToString("yyyy-MM-dd HH:mm:ss"), lastSync.ToString("yyyy-MM-dd HH:mm:ss"));

            await SetPragmasAsync();

            // 2. Detect changed ItemCodes — OINM (physical) + OITM (master, defensive)
            //    ORDR/RDR1 excluded: SO commitment ≠ bin-level stock movement.
            var detection = _sap.GetChangedBinItemCodes(effectiveFrom);

            _log.LogInformation(
                "[BinInv] Delta detection — OINM: {Oinm} | OITM: {Oitm} | Distinct: {Total}",
                detection.OinmCount, detection.OitmCount, detection.TotalDistinct);

            if (detection.TotalDistinct == 0)
            {
                _log.LogInformation("[BinInv] Delta: no changed items — watermark advanced.");
                await UpdateWatermarkAsync(DateTime.UtcNow);
                return (0, 0);
            }

            // 3. Normalize: Trim + ToUpperInvariant + Distinct
            var normalizedCodes = detection.ItemCodes
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 4. Fetch fresh bin snapshot for affected items (batched in 200 per SAP query)
            var rows     = _sap.GetBinInventorySnapshotForItems(normalizedCodes);
            var syncTime = DateTime.UtcNow;

            _log.LogInformation("[BinInv] Delta snapshot — {Rows} bin rows for {Items} changed items.",
                rows.Count, normalizedCodes.Count);

            // 5. Per-item (WhsCode, BinAbsEntry) key sets from fresh SAP data
            var freshByItem = rows
                .GroupBy(r => r.ItemCode.ToUpperInvariant())
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => (r.WhsCode, r.BinAbsEntry)).ToHashSet());

            // 6. Upsert returned rows (BinCode updated if changed, BinOnHand updated)
            int upserted = await UpsertRowsAsync(rows, syncTime);

            // 7. Delete stale bin rows per affected item
            //    Example: BIN-A was 5, now 0 → SAP stops returning it → DELETE cached row.
            int removed = await RemoveStaleForItemsAsync(normalizedCodes, freshByItem);

            // 8. Advance watermark — only after successful persistence
            await UpdateWatermarkAsync(syncTime);

            sw.Stop();
            _log.LogInformation(
                "[BinInv] Delta done — upserted: {U} | removed: {R} | rows: {SR} | {S:F1}s",
                upserted, removed, rows.Count, sw.Elapsed.TotalSeconds);

            return (upserted, removed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BinInv] Delta sync failed.");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Private: SQLite upsert ────────────────────────────────────────────────

    private async Task<int> UpsertRowsAsync(List<BinInventoryRow> rows, DateTime syncTime)
    {
        if (rows.Count == 0) return 0;

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var tx  = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO BinInventory (ItemCode, WhsCode, BinAbsEntry, BinCode, BinOnHand, LastUpdated)
VALUES ($ItemCode, $WhsCode, $BinAbsEntry, $BinCode, $BinOnHand, $LastUpdated)
ON CONFLICT(ItemCode, WhsCode, BinAbsEntry) DO UPDATE SET
    BinCode     = excluded.BinCode,
    BinOnHand   = excluded.BinOnHand,
    LastUpdated = excluded.LastUpdated";

        var pItem    = cmd.Parameters.Add("$ItemCode",    SqliteType.Text);
        var pWhs     = cmd.Parameters.Add("$WhsCode",     SqliteType.Text);
        var pBinAbs  = cmd.Parameters.Add("$BinAbsEntry", SqliteType.Integer);
        var pBinCode = cmd.Parameters.Add("$BinCode",     SqliteType.Text);
        var pOnHand  = cmd.Parameters.Add("$BinOnHand",   SqliteType.Real);
        var pUpdated = cmd.Parameters.Add("$LastUpdated", SqliteType.Text);

        var ts    = syncTime.ToString("yyyy-MM-dd HH:mm:ss");
        int count = 0;

        foreach (var r in rows)
        {
            pItem.Value    = r.ItemCode;
            pWhs.Value     = r.WhsCode;
            pBinAbs.Value  = r.BinAbsEntry;
            pBinCode.Value = r.BinCode;
            pOnHand.Value  = (double)r.BinOnHand;
            pUpdated.Value = ts;

            await cmd.ExecuteNonQueryAsync();
            count++;
        }

        tx.Commit();
        return count;
    }

    // ── Private: stale-row removal ────────────────────────────────────────────

    /// <summary>
    /// Full sync: removes cached rows whose (ItemCode, WhsCode, BinAbsEntry) triple
    /// is no longer returned by SAP (stock dropped to zero or bin was relocated).
    /// </summary>
    private async Task<int> RemoveGlobalStaleAsync(
        HashSet<(string IC, string WC, int BA)> sapKeys)
    {
        var cached = await _db.BinInventories
            .Where(b => ConfiguredWarehouses.Contains(b.WhsCode))
            .Select(b => new { b.Id, IC = b.ItemCode.ToUpper(), b.WhsCode, b.BinAbsEntry })
            .ToListAsync();

        var staleIds = cached
            .Where(c => !sapKeys.Contains((c.IC, c.WhsCode, c.BinAbsEntry)))
            .Select(c => c.Id)
            .ToList();

        if (staleIds.Count == 0) return 0;

        var stale = await _db.BinInventories
            .Where(b => staleIds.Contains(b.Id))
            .ToListAsync();

        _db.BinInventories.RemoveRange(stale);
        await _db.SaveChangesAsync();

        _log.LogInformation("[BinInv] Removed {Count} global stale bin rows.", stale.Count);
        return stale.Count;
    }

    /// <summary>
    /// Delta sync: for each affected ItemCode, removes cached bin rows that SAP no longer
    /// returns as positive-stock (stock hit zero → bin should be deleted, not kept as 0).
    /// </summary>
    private async Task<int> RemoveStaleForItemsAsync(
        IReadOnlyCollection<string>                              affectedCodes,
        Dictionary<string, HashSet<(string WhsCode, int BinAbsEntry)>> freshByItem)
    {
        int total = 0;

        foreach (var code in affectedCodes)
        {
            var upperCode = code.ToUpperInvariant();

            var cached = await _db.BinInventories
                .Where(b => ConfiguredWarehouses.Contains(b.WhsCode)
                         && b.ItemCode.ToUpper() == upperCode)
                .ToListAsync();

            if (cached.Count == 0) continue;

            var freshSet = freshByItem.TryGetValue(upperCode, out var ks)
                ? ks
                : new HashSet<(string, int)>();

            var stale = cached
                .Where(b => !freshSet.Contains((b.WhsCode, b.BinAbsEntry)))
                .ToList();

            if (stale.Count == 0) continue;

            _db.BinInventories.RemoveRange(stale);
            total += stale.Count;
        }

        if (total > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation("[BinInv] Removed {Count} stale bin rows for delta items.", total);
        }

        return total;
    }

    // ── Private: reconciliation ───────────────────────────────────────────────

    /// <summary>
    /// After a full sync, verifies that SUM(BinInventory.BinOnHand) per (ItemCode, WhsCode)
    /// matches WarehouseInventory.OnHand for bin-managed warehouses.
    /// Logs a warning with up to 10 sample mismatches for investigation.
    /// </summary>
    private async Task<BinReconciliationReport> BuildReconciliationReportAsync()
    {
        // Bin sums — all items across configured warehouses
        var binSums = await _db.BinInventories
            .Where(b => ConfiguredWarehouses.Contains(b.WhsCode))
            .GroupBy(b => new { b.ItemCode, b.WhsCode })
            .Select(g => new
            {
                g.Key.ItemCode,
                g.Key.WhsCode,
                BinTotal = g.Sum(b => b.BinOnHand)
            })
            .ToListAsync();

        // Warehouse totals for bin-managed warehouses
        var whRows = await _db.WarehouseInventories
            .Where(w => ConfiguredWarehouses.Contains(w.WhsCode) && w.IsBinManaged)
            .Select(w => new { w.ItemCode, w.WhsCode, w.OnHand })
            .ToListAsync();

        var whLookup = whRows.ToDictionary(
            w => (w.ItemCode.ToUpperInvariant(), w.WhsCode),
            w => w.OnHand);

        int matched = 0, mismatched = 0;
        var samples = new List<string>();

        foreach (var b in binSums)
        {
            var key = (b.ItemCode.ToUpperInvariant(), b.WhsCode);
            if (!whLookup.TryGetValue(key, out var whOnHand)) continue;

            if (Math.Abs(b.BinTotal - whOnHand) <= 0.0001m)
            {
                matched++;
            }
            else
            {
                mismatched++;
                if (samples.Count < 10)
                    samples.Add($"{b.ItemCode}/{b.WhsCode}: BinSum={b.BinTotal:F4} WH={whOnHand:F4}");
            }
        }

        if (mismatched > 0)
            _log.LogWarning("[BinInv] Reconciliation — {M} mismatch(es). Samples: {S}",
                mismatched, string.Join(" | ", samples));

        return new BinReconciliationReport(
            Checked:    matched + mismatched,
            Matched:    matched,
            Mismatched: mismatched);
    }

    // ── Private: watermark + pragmas ──────────────────────────────────────────

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
