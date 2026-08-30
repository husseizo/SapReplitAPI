using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Services.Neon;
using System.Diagnostics;
using System.Text;

namespace SapReplitAPI.Services.Inventory;

/// <summary>
/// Event fast-path inventory refresh. Called by event handlers on SAP document commits.
///
/// Two methods:
///   RefreshFullInventoryAsync  — physical events (15/A, 16/A, 20/A, 59/A, 60/A, 67/A/C, 13/A, 14/A)
///                                Updates: WH + Bin + Products
///   RefreshWarehouseInventoryAsync — commitment events (17/A, 17/U, 17/C)
///                                Updates: WH only (IsCommitted/AvailableToSell)
///
/// Lock discipline (never hold both simultaneously):
///   InventoryCacheWriteCoordinator  → SAP read → SQLite COMMIT → release
///   NeonInventoryWriteCoordinator   → SQLite read → Neon COMMIT → release
/// </summary>
public sealed class InventoryEventRefreshService
{
    private static readonly string[] ConfiguredWhs = { "001", "002", "003", "004" };

    private readonly SapService                          _sap;
    private readonly CacheDbContext                      _sqlite;
    private readonly NeonDbContext                       _neon;
    private readonly InventoryCacheWriteCoordinator      _inventoryCoord;
    private readonly NeonInventoryWriteCoordinator       _neonCoord;
    private readonly ILogger<InventoryEventRefreshService> _log;

    public InventoryEventRefreshService(
        SapService sap,
        CacheDbContext sqlite,
        NeonDbContext neon,
        InventoryCacheWriteCoordinator inventoryCoord,
        NeonInventoryWriteCoordinator neonCoord,
        ILogger<InventoryEventRefreshService> log)
    {
        _sap           = sap;
        _sqlite        = sqlite;
        _neon          = neon;
        _inventoryCoord = inventoryCoord;
        _neonCoord     = neonCoord;
        _log           = log;
    }

    // ── Full refresh: WH + Bin + Products ────────────────────────────────────

    public async Task RefreshFullInventoryAsync(IReadOnlyList<string> rawItemCodes, CancellationToken ct = default)
    {
        var itemCodes = NormalizeItemCodes(rawItemCodes);
        if (itemCodes.Count == 0) return;

        var totalSw = Stopwatch.StartNew();

        // ─ InventoryCacheWriteCoordinator window ─────────────────────────────
        var coordWaitSw = Stopwatch.StartNew();
        await _inventoryCoord.WaitAsync(ct);
        long inventoryCoordWaitMs = coordWaitSw.ElapsedMilliseconds;

        long sapReadMs = 0, sqliteCommitMs = 0;
        List<WarehouseInventoryRow> whRows;
        List<BinInventoryRow>       binRows;

        try
        {
            // SAP read
            var sapSw = Stopwatch.StartNew();
            whRows  = _sap.GetWarehouseInventorySnapshotForItems(itemCodes);
            binRows = _sap.GetBinInventorySnapshotForItems(itemCodes);
            sapReadMs = sapSw.ElapsedMilliseconds;

            // Compute per-item stock totals from OITW for Products update
            var stockByItem = ComputeStockByItem(whRows);

            // Find new items (stock > 0, not in Products) → full SAP product fetch
            var existingCodes = await _sqlite.Products
                .AsNoTracking()
                .Where(p => itemCodes.Contains(p.ItemCode!))
                .Select(p => p.ItemCode!)
                .ToListAsync(ct);
            var existingSet = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newItemCodes = itemCodes
                .Where(ic => !existingSet.Contains(ic) && stockByItem.ContainsKey(ic) && stockByItem[ic].Total > 0)
                .ToList();

            List<ProductWithWarehouseDto>? newItemProducts = null;
            if (newItemCodes.Count > 0)
                newItemProducts = _sap.GetProductsForItems(newItemCodes);

            var sqliteSw = Stopwatch.StartNew();
            await RunSqliteFullRefreshAsync(whRows, binRows, itemCodes, stockByItem, existingSet, newItemProducts, ct);
            sqliteCommitMs = sqliteSw.ElapsedMilliseconds;
        }
        finally
        {
            _inventoryCoord.Release();
        }
        // ─ end InventoryCacheWriteCoordinator window ──────────────────────────

        // ─ NeonInventoryWriteCoordinator window ──────────────────────────────
        var neonCoordWaitSw = Stopwatch.StartNew();
        await _neonCoord.WaitAsync(ct);
        long neonCoordWaitMs = neonCoordWaitSw.ElapsedMilliseconds;
        long neonWriteMs = 0;

        try
        {
            var neonSw = Stopwatch.StartNew();
            await RunNeonFullRefreshAsync(itemCodes, ct);
            neonWriteMs = neonSw.ElapsedMilliseconds;
        }
        finally
        {
            _neonCoord.Release();
        }
        // ─ end NeonInventoryWriteCoordinator window ───────────────────────────

        totalSw.Stop();
        _log.LogInformation(
            "[InvRefresh] Full: Items={Items} InventoryCoordWait={IcWait}ms SapRead={SAP}ms SqliteCommit={SQ}ms " +
            "NeonCoordWait={NcWait}ms NeonWrite={NW}ms Total={Total}ms",
            string.Join(",", itemCodes),
            inventoryCoordWaitMs, sapReadMs, sqliteCommitMs,
            neonCoordWaitMs, neonWriteMs, totalSw.ElapsedMilliseconds);
    }

    // ── WH-only refresh (SO commitment: 17/A, 17/U, 17/C) ───────────────────

    public async Task RefreshWarehouseInventoryAsync(IReadOnlyList<string> rawItemCodes, CancellationToken ct = default)
    {
        var itemCodes = NormalizeItemCodes(rawItemCodes);
        if (itemCodes.Count == 0) return;

        var totalSw = Stopwatch.StartNew();

        var coordWaitSw = Stopwatch.StartNew();
        await _inventoryCoord.WaitAsync(ct);
        long inventoryCoordWaitMs = coordWaitSw.ElapsedMilliseconds;

        long sapReadMs = 0, sqliteCommitMs = 0;
        List<WarehouseInventoryRow> whRows;

        try
        {
            var sapSw = Stopwatch.StartNew();
            whRows = _sap.GetWarehouseInventorySnapshotForItems(itemCodes);
            sapReadMs = sapSw.ElapsedMilliseconds;

            var sqliteSw = Stopwatch.StartNew();
            await UpsertWarehouseRowsAsync(whRows, DateTime.UtcNow, ct);
            sqliteCommitMs = sqliteSw.ElapsedMilliseconds;
        }
        finally
        {
            _inventoryCoord.Release();
        }

        var neonCoordWaitSw = Stopwatch.StartNew();
        await _neonCoord.WaitAsync(ct);
        long neonCoordWaitMs = neonCoordWaitSw.ElapsedMilliseconds;
        long neonWriteMs = 0;

        try
        {
            var neonSw = Stopwatch.StartNew();
            await NeonUpsertWarehouseInventoryAsync(itemCodes, ct);
            neonWriteMs = neonSw.ElapsedMilliseconds;
        }
        finally
        {
            _neonCoord.Release();
        }

        totalSw.Stop();
        _log.LogInformation(
            "[InvRefresh] WH-only: Items={Items} InventoryCoordWait={IcWait}ms SapRead={SAP}ms SqliteCommit={SQ}ms " +
            "NeonCoordWait={NcWait}ms NeonWrite={NW}ms Total={Total}ms",
            string.Join(",", itemCodes),
            inventoryCoordWaitMs, sapReadMs, sqliteCommitMs,
            neonCoordWaitMs, neonWriteMs, totalSw.ElapsedMilliseconds);
    }

    // ── SQLite helpers ────────────────────────────────────────────────────────

    private async Task RunSqliteFullRefreshAsync(
        List<WarehouseInventoryRow> whRows,
        List<BinInventoryRow>       binRows,
        IReadOnlyList<string>       itemCodes,
        Dictionary<string, (decimal Total, decimal W001, decimal W002, decimal W003, decimal W004)> stockByItem,
        HashSet<string>             existingProductCodes,
        List<ProductWithWarehouseDto>? newItemProducts,
        CancellationToken ct)
    {
        // Pre-compute stale bins BEFORE the raw transaction (avoids mixing EF Core + raw tx)
        var freshByItem = binRows
            .GroupBy(r => r.ItemCode.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Select(r => (r.WhsCode, r.BinAbsEntry)).ToHashSet());

        // For each item with fresh bins: find which cached rows are stale
        var staleBinDeletes = new List<(string ItemCode, string WhsCode, int BinAbsEntry)>();
        var zeroBinItems    = new List<string>();

        foreach (var ic in itemCodes)
        {
            var upperIc = ic.ToUpperInvariant();
            if (!freshByItem.TryGetValue(upperIc, out var freshSet) || freshSet.Count == 0)
            {
                zeroBinItems.Add(ic);
            }
            else
            {
                // Load current cached rows for this item to identify stale ones
                var cached = await _sqlite.BinInventories
                    .AsNoTracking()
                    .Where(b => b.ItemCode == ic)
                    .Select(b => new { b.WhsCode, b.BinAbsEntry })
                    .ToListAsync(ct);

                foreach (var c in cached.Where(c => !freshSet.Contains((c.WhsCode, c.BinAbsEntry))))
                    staleBinDeletes.Add((ic, c.WhsCode, c.BinAbsEntry));
            }
        }

        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();
        var syncTime = DateTime.UtcNow;
        var ts = syncTime.ToString("yyyy-MM-dd HH:mm:ss");

        try
        {
            // 1. UPSERT WarehouseInventory rows
            await UpsertWhRowsBatchAsync(conn, tx, whRows, ts);

            // 2a. Delete all cached bin rows for zero-bin items
            foreach (var ic in zeroBinItems)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM BinInventory WHERE ItemCode=$ic";
                del.Parameters.AddWithValue("$ic", ic);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 2b. Delete stale (ItemCode, WhsCode, BinAbsEntry) triplets
            foreach (var (ic, whs, ba) in staleBinDeletes)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM BinInventory WHERE ItemCode=$ic AND WhsCode=$whs AND BinAbsEntry=$ba";
                del.Parameters.AddWithValue("$ic",  ic);
                del.Parameters.AddWithValue("$whs", whs);
                del.Parameters.AddWithValue("$ba",  ba);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 2c. UPSERT fresh bin rows
            await UpsertBinRowsBatchAsync(conn, tx, binRows, ts);

            // 3. Products
            // 3a. Delete zero-stock items
            foreach (var ic in itemCodes.Where(ic => !stockByItem.ContainsKey(ic) || stockByItem[ic].Total <= 0))
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM Products WHERE ItemCode=$ic";
                del.Parameters.AddWithValue("$ic", ic);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 3b. Stock-only update for existing items with stock > 0
            foreach (var ic in itemCodes.Where(ic =>
                existingProductCodes.Contains(ic)
                && stockByItem.TryGetValue(ic, out var _) && stockByItem[ic].Total > 0))
            {
                var s = stockByItem[ic];
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"
UPDATE Products SET TotalOnHand=$tot,OnHand=$tot,OnHandQty=$tot,
 Whs_001=$w1,Whs_002=$w2,Whs_003=$w3,Whs_004=$w4,LastUpdated=$ts WHERE ItemCode=$ic";
                upd.Parameters.AddWithValue("$tot", (double)s.Total);
                upd.Parameters.AddWithValue("$w1",  (double)s.W001);
                upd.Parameters.AddWithValue("$w2",  (double)s.W002);
                upd.Parameters.AddWithValue("$w3",  (double)s.W003);
                upd.Parameters.AddWithValue("$w4",  (double)s.W004);
                upd.Parameters.AddWithValue("$ts",  ts);
                upd.Parameters.AddWithValue("$ic",  ic);
                await upd.ExecuteNonQueryAsync(ct);
            }

            // 3c. Full insert for new items with stock > 0
            if (newItemProducts != null)
            {
                foreach (var p in newItemProducts)
                {
                    if (!stockByItem.TryGetValue(p.ItemCode!.ToUpperInvariant(), out var s) || s.Total <= 0) continue;
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = @"
INSERT INTO Products
(ItemCode,ItemName,U_Article_No,U_MdlTEST,U_Item_Name,Price,Price05,
 TotalOnHand,OnHand,OnHandQty,WhsCode,LastUpdated,Whs_001,Whs_002,Whs_003,Whs_004)
VALUES($ic,$in,$art,$mdl,$inm,$pr,$pr5,$tot,$tot,$tot,'ALL',$ts,$w1,$w2,$w3,$w4)
ON CONFLICT(ItemCode) DO UPDATE SET
 TotalOnHand=excluded.TotalOnHand,OnHand=excluded.OnHand,OnHandQty=excluded.OnHandQty,
 Whs_001=excluded.Whs_001,Whs_002=excluded.Whs_002,Whs_003=excluded.Whs_003,
 Whs_004=excluded.Whs_004,LastUpdated=excluded.LastUpdated";
                    ins.Parameters.AddWithValue("$ic",  p.ItemCode ?? "");
                    ins.Parameters.AddWithValue("$in",  p.ItemName ?? "");
                    ins.Parameters.AddWithValue("$art", p.U_Article_No ?? "");
                    ins.Parameters.AddWithValue("$mdl", p.U_MdlTEST ?? "");
                    ins.Parameters.AddWithValue("$inm", p.U_Item_Name ?? "");
                    ins.Parameters.AddWithValue("$pr",  (double)p.Price);
                    ins.Parameters.AddWithValue("$pr5", (double)p.Price05);
                    ins.Parameters.AddWithValue("$tot", (double)s.Total);
                    ins.Parameters.AddWithValue("$ts",  ts);
                    ins.Parameters.AddWithValue("$w1",  (double)s.W001);
                    ins.Parameters.AddWithValue("$w2",  (double)s.W002);
                    ins.Parameters.AddWithValue("$w3",  (double)s.W003);
                    ins.Parameters.AddWithValue("$w4",  (double)s.W004);
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task UpsertWhRowsBatchAsync(SqliteConnection conn, SqliteTransaction tx,
        List<WarehouseInventoryRow> rows, string ts)
    {
        if (rows.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO WarehouseInventory
(ItemCode,WhsCode,WarehouseName,OnHand,IsCommitted,OnOrder,AvailableToSell,IsBinManaged,LastUpdated)
VALUES ($ic,$whs,$wn,$oh,$ic2,$oo,$avail,$bin,$ts)
ON CONFLICT(ItemCode,WhsCode) DO UPDATE SET
 WarehouseName=excluded.WarehouseName,OnHand=excluded.OnHand,IsCommitted=excluded.IsCommitted,
 OnOrder=excluded.OnOrder,AvailableToSell=excluded.AvailableToSell,
 IsBinManaged=excluded.IsBinManaged,LastUpdated=excluded.LastUpdated";

        var pIc    = cmd.Parameters.Add("$ic",    SqliteType.Text);
        var pWhs   = cmd.Parameters.Add("$whs",   SqliteType.Text);
        var pWn    = cmd.Parameters.Add("$wn",    SqliteType.Text);
        var pOh    = cmd.Parameters.Add("$oh",    SqliteType.Real);
        var pIc2   = cmd.Parameters.Add("$ic2",   SqliteType.Real);
        var pOo    = cmd.Parameters.Add("$oo",    SqliteType.Real);
        var pAvail = cmd.Parameters.Add("$avail", SqliteType.Real);
        var pBin   = cmd.Parameters.Add("$bin",   SqliteType.Integer);
        var pTs    = cmd.Parameters.Add("$ts",    SqliteType.Text);
        pTs.Value  = ts;

        foreach (var r in rows)
        {
            pIc.Value    = r.ItemCode;
            pWhs.Value   = r.WhsCode;
            pWn.Value    = r.WarehouseName;
            pOh.Value    = (double)r.OnHand;
            pIc2.Value   = (double)r.IsCommitted;
            pOo.Value    = (double)r.OnOrder;
            pAvail.Value = (double)r.AvailableToSell;
            pBin.Value   = r.IsBinManaged ? 1 : 0;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task UpsertBinRowsBatchAsync(SqliteConnection conn, SqliteTransaction tx,
        List<BinInventoryRow> rows, string ts)
    {
        if (rows.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO BinInventory (ItemCode,WhsCode,BinAbsEntry,BinCode,BinOnHand,LastUpdated)
VALUES ($ic,$whs,$ba,$bc,$oh,$ts)
ON CONFLICT(ItemCode,WhsCode,BinAbsEntry) DO UPDATE SET
 BinCode=excluded.BinCode,BinOnHand=excluded.BinOnHand,LastUpdated=excluded.LastUpdated";

        var pIc  = cmd.Parameters.Add("$ic",  SqliteType.Text);
        var pWhs = cmd.Parameters.Add("$whs", SqliteType.Text);
        var pBa  = cmd.Parameters.Add("$ba",  SqliteType.Integer);
        var pBc  = cmd.Parameters.Add("$bc",  SqliteType.Text);
        var pOh  = cmd.Parameters.Add("$oh",  SqliteType.Real);
        var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Text);
        pTs.Value = ts;

        foreach (var r in rows)
        {
            pIc.Value  = r.ItemCode;
            pWhs.Value = r.WhsCode;
            pBa.Value  = r.BinAbsEntry;
            pBc.Value  = r.BinCode;
            pOh.Value  = (double)r.BinOnHand;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task UpsertWarehouseRowsAsync(List<WarehouseInventoryRow> rows, DateTime syncTime, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        try
        {
            await UpsertWhRowsBatchAsync(conn, tx, rows, syncTime.ToString("yyyy-MM-dd HH:mm:ss"));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── Neon helpers ──────────────────────────────────────────────────────────

    private async Task RunNeonFullRefreshAsync(IReadOnlyList<string> itemCodes, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
        if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync(ct);

        // WH rows from SQLite
        var whRows = await _sqlite.WarehouseInventories
            .AsNoTracking()
            .Where(w => itemCodes.Contains(w.ItemCode))
            .ToListAsync(ct);

        // Bin rows from SQLite
        var binRows = await _sqlite.BinInventories
            .AsNoTracking()
            .Where(b => itemCodes.Contains(b.ItemCode))
            .ToListAsync(ct);

        // Products from SQLite
        var products = await _sqlite.Products
            .AsNoTracking()
            .Where(p => itemCodes.Contains(p.ItemCode!))
            .ToListAsync(ct);

        using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // WH upsert
            foreach (var w in whRows)
            {
                using var cmd = new NpgsqlCommand(@"
INSERT INTO ""WarehouseInventory""
(""ItemCode"",""WhsCode"",""WarehouseName"",""OnHand"",""IsCommitted"",""OnOrder"",""AvailableToSell"",""IsBinManaged"",""LastUpdated"")
VALUES(@ic,@whs,@wn,@oh,@ic2,@oo,@avail,@bin,@ts)
ON CONFLICT(""ItemCode"",""WhsCode"") DO UPDATE SET
 ""WarehouseName""=excluded.""WarehouseName"",""OnHand""=excluded.""OnHand"",
 ""IsCommitted""=excluded.""IsCommitted"",""OnOrder""=excluded.""OnOrder"",
 ""AvailableToSell""=excluded.""AvailableToSell"",""IsBinManaged""=excluded.""IsBinManaged"",
 ""LastUpdated""=excluded.""LastUpdated""", conn, tx);
                cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,        w.ItemCode ?? "");
                cmd.Parameters.AddWithValue("@whs",   NpgsqlDbType.Text,        w.WhsCode ?? "");
                cmd.Parameters.AddWithValue("@wn",    NpgsqlDbType.Text,        w.WarehouseName ?? "");
                cmd.Parameters.AddWithValue("@oh",    NpgsqlDbType.Numeric,     w.OnHand);
                cmd.Parameters.AddWithValue("@ic2",   NpgsqlDbType.Numeric,     w.IsCommitted);
                cmd.Parameters.AddWithValue("@oo",    NpgsqlDbType.Numeric,     w.OnOrder);
                cmd.Parameters.AddWithValue("@avail", NpgsqlDbType.Numeric,     w.AvailableToSell);
                cmd.Parameters.AddWithValue("@bin",   NpgsqlDbType.Boolean,     w.IsBinManaged);
                cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(w.LastUpdated, DateTimeKind.Utc));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Bin: per-item delete stale + upsert
            foreach (var ic in itemCodes)
            {
                var itemBins = binRows.Where(b => string.Equals(b.ItemCode, ic, StringComparison.OrdinalIgnoreCase)).ToList();
                if (itemBins.Count == 0)
                {
                    using var del = new NpgsqlCommand(@"DELETE FROM ""BinInventory"" WHERE ""ItemCode""=@ic", conn, tx);
                    del.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, ic);
                    await del.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    // Build VALUES relation for stale delete
                    var pairs = itemBins.Select((b, i) => $"(@whs{i},@ba{i})").ToList();
                    var delSql = new StringBuilder($@"DELETE FROM ""BinInventory"" WHERE ""ItemCode""=@ic AND (""WhsCode"",""BinAbsEntry"") NOT IN (VALUES ");
                    delSql.Append(string.Join(",", pairs));
                    delSql.Append(")");

                    using var del = new NpgsqlCommand(delSql.ToString(), conn, tx);
                    del.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, ic);
                    for (int i = 0; i < itemBins.Count; i++)
                    {
                        del.Parameters.AddWithValue($"@whs{i}", NpgsqlDbType.Text,    itemBins[i].WhsCode);
                        del.Parameters.AddWithValue($"@ba{i}",  NpgsqlDbType.Integer, itemBins[i].BinAbsEntry);
                    }
                    await del.ExecuteNonQueryAsync(ct);

                    // Upsert
                    foreach (var b in itemBins)
                    {
                        using var ins = new NpgsqlCommand(@"
INSERT INTO ""BinInventory"" (""ItemCode"",""WhsCode"",""BinAbsEntry"",""BinCode"",""BinOnHand"",""LastUpdated"")
VALUES(@ic,@whs,@ba,@bc,@oh,@ts)
ON CONFLICT(""ItemCode"",""WhsCode"",""BinAbsEntry"") DO UPDATE SET
 ""BinCode""=excluded.""BinCode"",""BinOnHand""=excluded.""BinOnHand"",""LastUpdated""=excluded.""LastUpdated""", conn, tx);
                        ins.Parameters.AddWithValue("@ic",  NpgsqlDbType.Text,        b.ItemCode ?? "");
                        ins.Parameters.AddWithValue("@whs", NpgsqlDbType.Text,        b.WhsCode ?? "");
                        ins.Parameters.AddWithValue("@ba",  NpgsqlDbType.Integer,     b.BinAbsEntry);
                        ins.Parameters.AddWithValue("@bc",  NpgsqlDbType.Text,        b.BinCode ?? "");
                        ins.Parameters.AddWithValue("@oh",  NpgsqlDbType.Numeric,     b.BinOnHand);
                        ins.Parameters.AddWithValue("@ts",  NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(b.LastUpdated, DateTimeKind.Utc));
                        await ins.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            // Products: delete zero-stock, upsert positive
            var productByCode = products.ToDictionary(p => p.ItemCode!.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
            foreach (var ic in itemCodes)
            {
                if (!productByCode.ContainsKey(ic.ToUpperInvariant()))
                {
                    // Not in SQLite → delete from Neon too
                    using var del = new NpgsqlCommand(@"DELETE FROM ""Products"" WHERE ""ItemCode""=@ic", conn, tx);
                    del.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, ic);
                    await del.ExecuteNonQueryAsync(ct);
                }
            }
            foreach (var p in products)
            {
                using var upsert = new NpgsqlCommand(@"
INSERT INTO ""Products""
(""ItemCode"",""ItemName"",""U_Article_No"",""U_MdlTEST"",""U_Item_Name"",""Price"",""Price05"",
 ""TotalOnHand"",""OnHand"",""OnHandQty"",""WhsCode"",""LastUpdated"",""Whs_001"",""Whs_002"",""Whs_003"",""Whs_004"")
VALUES(@ic,@in,@art,@mdl,@inm,@pr,@pr5,@tot,@tot,@tot,'ALL',@ts,@w1,@w2,@w3,@w4)
ON CONFLICT(""ItemCode"") DO UPDATE SET
 ""TotalOnHand""=excluded.""TotalOnHand"",""OnHand""=excluded.""OnHand"",""OnHandQty""=excluded.""OnHandQty"",
 ""Whs_001""=excluded.""Whs_001"",""Whs_002""=excluded.""Whs_002"",
 ""Whs_003""=excluded.""Whs_003"",""Whs_004""=excluded.""Whs_004"",
 ""LastUpdated""=excluded.""LastUpdated""", conn, tx);
                upsert.Parameters.AddWithValue("@ic",  NpgsqlDbType.Text,        p.ItemCode ?? "");
                upsert.Parameters.AddWithValue("@in",  NpgsqlDbType.Text,        p.ItemName ?? "");
                upsert.Parameters.AddWithValue("@art", NpgsqlDbType.Text,        p.U_Article_No ?? "");
                upsert.Parameters.AddWithValue("@mdl", NpgsqlDbType.Text,        p.U_MdlTEST ?? "");
                upsert.Parameters.AddWithValue("@inm", NpgsqlDbType.Text,        p.U_Item_Name ?? "");
                upsert.Parameters.AddWithValue("@pr",  NpgsqlDbType.Numeric,     p.Price);
                upsert.Parameters.AddWithValue("@pr5", NpgsqlDbType.Numeric,     p.Price05);
                upsert.Parameters.AddWithValue("@tot", NpgsqlDbType.Numeric,     p.TotalOnHand);
                upsert.Parameters.AddWithValue("@ts",  NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(p.LastUpdated, DateTimeKind.Utc));
                upsert.Parameters.AddWithValue("@w1",  NpgsqlDbType.Integer,     (object?)(p.Whs_001 ?? 0));
                upsert.Parameters.AddWithValue("@w2",  NpgsqlDbType.Integer,     (object?)(p.Whs_002 ?? 0));
                upsert.Parameters.AddWithValue("@w3",  NpgsqlDbType.Integer,     (object?)(p.Whs_003 ?? 0));
                upsert.Parameters.AddWithValue("@w4",  NpgsqlDbType.Integer,     (object?)(p.Whs_004 ?? 0));
                await upsert.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task NeonUpsertWarehouseInventoryAsync(IReadOnlyList<string> itemCodes, CancellationToken ct)
    {
        var whRows = await _sqlite.WarehouseInventories
            .AsNoTracking()
            .Where(w => itemCodes.Contains(w.ItemCode))
            .ToListAsync(ct);

        if (whRows.Count == 0) return;

        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
        if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync(ct);

        using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var w in whRows)
            {
                using var cmd = new NpgsqlCommand(@"
INSERT INTO ""WarehouseInventory""
(""ItemCode"",""WhsCode"",""WarehouseName"",""OnHand"",""IsCommitted"",""OnOrder"",""AvailableToSell"",""IsBinManaged"",""LastUpdated"")
VALUES(@ic,@whs,@wn,@oh,@ic2,@oo,@avail,@bin,@ts)
ON CONFLICT(""ItemCode"",""WhsCode"") DO UPDATE SET
 ""WarehouseName""=excluded.""WarehouseName"",""OnHand""=excluded.""OnHand"",
 ""IsCommitted""=excluded.""IsCommitted"",""OnOrder""=excluded.""OnOrder"",
 ""AvailableToSell""=excluded.""AvailableToSell"",""IsBinManaged""=excluded.""IsBinManaged"",
 ""LastUpdated""=excluded.""LastUpdated""", conn, tx);
                cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,        w.ItemCode ?? "");
                cmd.Parameters.AddWithValue("@whs",   NpgsqlDbType.Text,        w.WhsCode ?? "");
                cmd.Parameters.AddWithValue("@wn",    NpgsqlDbType.Text,        w.WarehouseName ?? "");
                cmd.Parameters.AddWithValue("@oh",    NpgsqlDbType.Numeric,     w.OnHand);
                cmd.Parameters.AddWithValue("@ic2",   NpgsqlDbType.Numeric,     w.IsCommitted);
                cmd.Parameters.AddWithValue("@oo",    NpgsqlDbType.Numeric,     w.OnOrder);
                cmd.Parameters.AddWithValue("@avail", NpgsqlDbType.Numeric,     w.AvailableToSell);
                cmd.Parameters.AddWithValue("@bin",   NpgsqlDbType.Boolean,     w.IsBinManaged);
                cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(w.LastUpdated, DateTimeKind.Utc));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> NormalizeItemCodes(IReadOnlyList<string> raw)
        => raw
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, (decimal Total, decimal W001, decimal W002, decimal W003, decimal W004)>
        ComputeStockByItem(List<WarehouseInventoryRow> rows)
    {
        var result = new Dictionary<string, (decimal, decimal, decimal, decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var ic = r.ItemCode.ToUpperInvariant();
            if (!result.TryGetValue(ic, out var cur))
                cur = (0m, 0m, 0m, 0m, 0m);

            decimal w1 = r.WhsCode == "001" ? r.OnHand : cur.Item2;
            decimal w2 = r.WhsCode == "002" ? r.OnHand : cur.Item3;
            decimal w3 = r.WhsCode == "003" ? r.OnHand : cur.Item4;
            decimal w4 = r.WhsCode == "004" ? r.OnHand : cur.Item5;
            result[ic] = (w1 + w2 + w3 + w4, w1, w2, w3, w4);
        }
        return result;
    }
}
