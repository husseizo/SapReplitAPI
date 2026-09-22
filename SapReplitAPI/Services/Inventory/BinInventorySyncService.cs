using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Services.Neon;
using System.Diagnostics;

namespace SapReplitAPI.Services.Inventory;

public class BinInventorySyncService
{
    private static readonly string[] ConfiguredWarehouses = { "001", "002", "003", "004" };
    private const  string            WatermarkKey         = "BinInventory.Source";
    private static readonly TimeSpan LookbackWindow       = TimeSpan.FromHours(2);

    private readonly CacheDbContext                    _db;
    private readonly SapService                        _sap;
    private readonly InventoryCacheWriteCoordinator    _coord;
    private readonly NeonDbContext                     _neon;
    private readonly NeonInventoryWriteCoordinator     _neonCoord;
    private readonly ILogger<BinInventorySyncService>  _log;

    public BinInventorySyncService(
        CacheDbContext db,
        SapService sap,
        InventoryCacheWriteCoordinator coord,
        NeonDbContext neon,
        NeonInventoryWriteCoordinator neonCoord,
        ILogger<BinInventorySyncService> log)
    {
        _db        = db;
        _sap       = sap;
        _coord     = coord;
        _neon      = neon;
        _neonCoord = neonCoord;
        _log       = log;
    }

    // ── Public: Full sync ─────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the complete positive-stock OIBQ snapshot from SAP,
    /// upserts all rows, deletes globally stale rows, advances the watermark,
    /// pushes all rows to Neon (targeted per-item — no TRUNCATE on event path),
    /// and returns a full diagnostic report including reconciliation vs WarehouseInventory.
    /// </summary>
    public async Task<BinSyncReport> FullSyncAsync()
    {
        var coordSw = Stopwatch.StartNew();
        await _coord.WaitAsync();
        coordSw.Stop();
        var sw = Stopwatch.StartNew();
        List<BinInventoryRow> rows;
        DateTime syncTime;
        IReadOnlyDictionary<string, int> byWhs;
        int distinctItems, upserted, removed;

        try
        {
            _log.LogInformation("[BinInv] Full sync started. InventoryCoordWaitMs={W:F1}", coordSw.Elapsed.TotalMilliseconds);

            await SetPragmasAsync();

            // 1. Fetch complete positive-stock OIBQ snapshot for 001-004 (BinActivat='Y')
            rows      = _sap.GetBinInventorySnapshot();
            syncTime  = DateTime.UtcNow;

            byWhs = ConfiguredWarehouses.ToDictionary(
                w => w,
                w => rows.Count(r => r.WhsCode == w));

            distinctItems = rows
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
            upserted = await UpsertRowsAsync(rows, syncTime);

            // 4. Delete stale rows (bins no longer positive in SAP)
            removed = await RemoveGlobalStaleAsync(sapKeys);

            // 5. Advance watermark — only after successful persistence
            await UpdateWatermarkAsync(syncTime);
        }
        finally
        {
            _coord.Release();
        }

        // 6. Immediate Neon push — all rows, targeted per-item (no TRUNCATE)
        // Uses NeonInventoryWriteCoordinator so it doesn't race with event-driven pushes.
        var neonCoordWaitSw = Stopwatch.StartNew();
        await _neonCoord.WaitAsync();
        neonCoordWaitSw.Stop();
        long neonWriteMs = 0;
        try
        {
            var neonSw = Stopwatch.StartNew();
            var rowsByItem = rows
                .GroupBy(r => r.ItemCode.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            await PushBinRowsToNeonAsync(rowsByItem);
            neonWriteMs = neonSw.ElapsedMilliseconds;
            _log.LogInformation("[BinInv] Neon full push complete — {Items} items | {Rows} rows | {Ms}ms",
                rowsByItem.Count, rows.Count, neonWriteMs);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BinInv] Neon full push failed — SQLite is authoritative; Neon may lag.");
        }
        finally
        {
            _neonCoord.Release();
        }

        sw.Stop();

        // 7. Reconciliation vs WarehouseInventory.OnHand
        var recon = await BuildReconciliationReportAsync();

        _log.LogInformation(
            "[BinInv] Full sync complete — upserted: {U} | removed: {R} | duration: {S:F1}s",
            upserted, removed, sw.Elapsed.TotalSeconds);
        _log.LogInformation(
            "[BinInv] Reconciliation — checked: {C} | matched: {M} | mismatched: {MM} | missing_bin_inventory: {MB}",
            recon.Checked, recon.Matched, recon.Mismatched, recon.MissingBinInventory);

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

    // ── Public: Delta sync ────────────────────────────────────────────────────

    /// <summary>
    /// Detects physically changed ItemCodes via OINM + OITM (ORDR excluded — SO commitment
    /// does not alter bin-level OnHandQty), fetches current bin snapshots for those items,
    /// upserts updated rows, deletes stale bin rows, advances the watermark,
    /// and immediately pushes those items to Neon.
    /// </summary>
    public async Task<(int Upserted, int Removed)> DeltaSyncAsync()
    {
        var coordSw = Stopwatch.StartNew();
        await _coord.WaitAsync();
        coordSw.Stop();
        var sw = Stopwatch.StartNew();
        List<string> normalizedCodes;
        List<BinInventoryRow> rows;
        int upserted, removed;

        try
        {
            _log.LogInformation("[BinInv] Delta sync waiting done. InventoryCoordWaitMs={W:F1}", coordSw.Elapsed.TotalMilliseconds);
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
            normalizedCodes = detection.ItemCodes
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 4. Fetch fresh bin snapshot for affected items (batched in 200 per SAP query)
            rows     = _sap.GetBinInventorySnapshotForItems(normalizedCodes);
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
            upserted = await UpsertRowsAsync(rows, syncTime);

            // 7. Delete stale bin rows per affected item
            removed = await RemoveStaleForItemsAsync(normalizedCodes, freshByItem);

            // 8. Advance watermark — only after successful persistence
            await UpdateWatermarkAsync(syncTime);
        }
        finally
        {
            _coord.Release();
        }

        // 9. Immediate targeted Neon push for affected items
        var neonCoordWaitSw = Stopwatch.StartNew();
        await _neonCoord.WaitAsync();
        neonCoordWaitSw.Stop();
        try
        {
            var rowsByItem = rows
                .GroupBy(r => r.ItemCode.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            await PushBinRowsToNeonAsync(rowsByItem, normalizedCodes);

            _log.LogInformation("[BinInv] Delta Neon push — {Items} items | {Rows} rows | NeonCoordWait={W}ms",
                rowsByItem.Count, rows.Count, neonCoordWaitSw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BinInv] Delta Neon push failed — SQLite is authoritative; Neon may lag.");
        }
        finally
        {
            _neonCoord.Release();
        }

        sw.Stop();
        _log.LogInformation(
            "[BinInv] Delta done — upserted: {U} | removed: {R} | rows: {SR} | {S:F1}s",
            upserted, removed, rows.Count, sw.Elapsed.TotalSeconds);

        return (upserted, removed);
    }

    // ── Private: Neon targeted push ───────────────────────────────────────────

    /// <summary>
    /// Pushes bin rows to Neon for the given items.
    /// Per-item: delete stale Neon rows for that ItemCode, then upsert current rows.
    /// If allItemCodes is provided, also deletes items with no rows in SAP snapshot.
    /// Safe to call under NeonInventoryWriteCoordinator lock only.
    /// </summary>
    private async Task PushBinRowsToNeonAsync(
        Dictionary<string, List<BinInventoryRow>> rowsByItem,
        IReadOnlyList<string>? allItemCodes = null)
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
        if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync();

        // Items to process: union of rowsByItem keys + allItemCodes that have no rows
        var toProcess = rowsByItem.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allItemCodes != null)
        {
            foreach (var ic in allItemCodes)
                toProcess.Add(ic.ToUpperInvariant());
        }

        using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var ic in toProcess)
            {
                var itemBins = rowsByItem.TryGetValue(ic, out var bins) ? bins : new List<BinInventoryRow>();

                if (itemBins.Count == 0)
                {
                    // Item has no positive bin stock — delete any Neon rows
                    using var del = new NpgsqlCommand(@"DELETE FROM ""BinInventory"" WHERE ""ItemCode""=@ic", conn, tx);
                    del.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, ic);
                    await del.ExecuteNonQueryAsync();
                }
                else
                {
                    // Delete stale Neon rows: those not in the current fresh set
                    var pairs = itemBins.Select((b, i) => $"(@whs{i},@ba{i})").ToList();
                    var delSql = $@"DELETE FROM ""BinInventory"" WHERE ""ItemCode""=@ic AND (""WhsCode"",""BinAbsEntry"") NOT IN (VALUES {string.Join(",", pairs)})";
                    using var del = new NpgsqlCommand(delSql, conn, tx);
                    del.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, ic);
                    for (int i = 0; i < itemBins.Count; i++)
                    {
                        del.Parameters.AddWithValue($"@whs{i}", NpgsqlDbType.Text,    itemBins[i].WhsCode);
                        del.Parameters.AddWithValue($"@ba{i}",  NpgsqlDbType.Integer, itemBins[i].BinAbsEntry);
                    }
                    await del.ExecuteNonQueryAsync();

                    // Upsert current rows
                    var ts = DateTime.UtcNow;
                    foreach (var b in itemBins)
                    {
                        using var ins = new NpgsqlCommand(@"
INSERT INTO ""BinInventory"" (""ItemCode"",""WhsCode"",""BinAbsEntry"",""BinCode"",""BinOnHand"",""LastUpdated"")
VALUES(@ic,@whs,@ba,@bc,@oh,@ts)
ON CONFLICT(""ItemCode"",""WhsCode"",""BinAbsEntry"") DO UPDATE SET
 ""BinCode""=excluded.""BinCode"",""BinOnHand""=excluded.""BinOnHand"",""LastUpdated""=excluded.""LastUpdated""", conn, tx);
                        ins.Parameters.AddWithValue("@ic",  NpgsqlDbType.Text,        b.ItemCode);
                        ins.Parameters.AddWithValue("@whs", NpgsqlDbType.Text,        b.WhsCode);
                        ins.Parameters.AddWithValue("@ba",  NpgsqlDbType.Integer,     b.BinAbsEntry);
                        ins.Parameters.AddWithValue("@bc",  NpgsqlDbType.Text,        b.BinCode);
                        ins.Parameters.AddWithValue("@oh",  NpgsqlDbType.Numeric,     b.BinOnHand);
                        ins.Parameters.AddWithValue("@ts",  NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(ts, DateTimeKind.Utc));
                        await ins.ExecuteNonQueryAsync();
                    }
                }
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
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
    /// Also detects MISSING_BIN_INVENTORY: WH rows with OnHand>0, IsBinManaged=true, and zero bin rows.
    /// </summary>
    public async Task<BinReconciliationReport> BuildReconciliationReportAsync()
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

        var binLookup = binSums.ToDictionary(
            b => (b.ItemCode.ToUpperInvariant(), b.WhsCode),
            b => b.BinTotal);

        int matched = 0, mismatched = 0, missingBin = 0;
        var samples      = new List<string>();
        var missingSamples = new List<string>();

        foreach (var w in whRows)
        {
            if (w.OnHand <= 0) continue;

            var key = (w.ItemCode.ToUpperInvariant(), w.WhsCode);

            if (!binLookup.TryGetValue(key, out var binTotal) || binTotal <= 0)
            {
                // WH has positive stock but BinInventory has no rows — production-incident pattern
                missingBin++;
                if (missingSamples.Count < 10)
                    missingSamples.Add($"{w.ItemCode}/{w.WhsCode}: WH={w.OnHand:F4} BinRows=0");
                continue;
            }

            if (Math.Abs(binTotal - w.OnHand) <= 0.0001m)
            {
                matched++;
            }
            else
            {
                mismatched++;
                if (samples.Count < 10)
                    samples.Add($"{w.ItemCode}/{w.WhsCode}: BinSum={binTotal:F4} WH={w.OnHand:F4}");
            }
        }

        if (missingBin > 0)
            _log.LogWarning(
                "[BinInv] MISSING_BIN_INVENTORY — {M} item/whs pair(s) with OnHand>0 but no bin rows. Samples: {S}",
                missingBin, string.Join(" | ", missingSamples));

        if (mismatched > 0)
            _log.LogWarning("[BinInv] Reconciliation — {M} mismatch(es). Samples: {S}",
                mismatched, string.Join(" | ", samples));

        return new BinReconciliationReport(
            Checked:            matched + mismatched + missingBin,
            Matched:            matched,
            Mismatched:         mismatched,
            MissingBinInventory: missingBin);
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
