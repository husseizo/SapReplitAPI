using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Product;

/// <summary>
/// Synchronises SAP OPLN → PriceLists and SAP ITM1 → ItemPriceLists,
/// then projects PL1–PL5 back to Products compatibility columns.
///
/// Performance contract:
///   - Two SAP queries per full sync (OPLN, then OITM JOIN ITM1).
///   - SQLite writes via raw transactions; Neon writes via raw Npgsql.
///   - No SAP DI API calls (read-only recordset queries only).
///   - No modifications to SAP data.
/// </summary>
public class ProductPriceListSyncService
{
    private readonly SapService                        _sap;
    private readonly CacheDbContext                    _sqlite;
    private readonly NeonDbContext                     _neon;
    private readonly ILogger<ProductPriceListSyncService> _log;

    // Price lists we actively use; the table stores all SAP price lists.
    public static readonly IReadOnlyList<int> ActivePriceLists = [1, 2, 3, 4, 5];

    public ProductPriceListSyncService(
        SapService sap, CacheDbContext sqlite,
        NeonDbContext neon, ILogger<ProductPriceListSyncService> log)
    {
        _sap    = sap;
        _sqlite = sqlite;
        _neon   = neon;
        _log    = log;
    }

    // ── Full price sync ───────────────────────────────────────────────────────

    /// <summary>
    /// Full price synchronisation:
    ///   SAP OPLN → SQLite PriceLists → Neon PriceLists
    ///   SAP ITM1 → SQLite ItemPriceLists → Neon ItemPriceLists
    ///   then projects PL1–PL5 into Products compatibility columns.
    /// </summary>
    public async Task<PriceSyncResult> FullSyncAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("[PriceSync] Full price sync started.");

        // 1. Fetch from SAP
        var priceLists = _sap.GetPriceLists();
        var itemPrices = _sap.GetAllItemPrices(ActivePriceLists);

        _log.LogInformation("[PriceSync] SAP returned {PL} price lists, {IP} item-price rows.",
            priceLists.Count, itemPrices.Count);

        // 2. Persist to SQLite
        int plRows = await UpsertSqlitePriceListsAsync(priceLists, ct);
        int ipRows = await UpsertSqliteItemPriceListsAsync(itemPrices, ct);

        // 3. Project into Products compatibility columns
        int projRows = await ProjectToProductsAsync(itemPrices, ct);

        // 4. Mirror to Neon
        int neonPl = await UpsertNeonPriceListsAsync(priceLists, ct);
        int neonIp = await UpsertNeonItemPriceListsAsync(itemPrices, ct);
        int neonProj = await ProjectToNeonProductsAsync(itemPrices, ct);

        sw.Stop();
        _log.LogInformation(
            "[PriceSync] Full sync done in {Sec:0.0}s. SQLite: {PL} PLists, {IP} ItemPrices, {Proj} Products projected. " +
            "Neon: {NPL} PLists, {NIP} ItemPrices, {NProj} Products projected.",
            sw.Elapsed.TotalSeconds, plRows, ipRows, projRows, neonPl, neonIp, neonProj);

        return new PriceSyncResult
        {
            SqlitePriceListRows  = plRows,
            SqliteItemPriceRows  = ipRows,
            SqliteProjectedRows  = projRows,
            NeonPriceListRows    = neonPl,
            NeonItemPriceRows    = neonIp,
            NeonProjectedRows    = neonProj,
            ElapsedSeconds       = sw.Elapsed.TotalSeconds,
        };
    }

    // ── Single-item repair ────────────────────────────────────────────────────

    /// <summary>
    /// Repairs ItemPriceLists + Products projection for one item from live SAP.
    /// Used by RepairCacheAsync after a successful SAP price mutation readback.
    /// No SAP mutation — read-only.
    /// </summary>
    public async Task<bool> RepairItemPriceAsync(
        string itemCode, int priceListNum, decimal price, string currency,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // SQLite ItemPriceLists
        await UpsertSingleItemPriceSqliteAsync(itemCode, priceListNum, price, currency, now, ct);

        // SQLite Products projection
        await _sqlite.Database.ExecuteSqlRawAsync(
            $"UPDATE Products SET {PriceColumn(priceListNum)} = @price, LastUpdated = @ts WHERE ItemCode = @ic",
            new SqliteParameter("@price", (double)price),
            new SqliteParameter("@ts",    now.ToString("yyyy-MM-dd HH:mm:ss")),
            new SqliteParameter("@ic",    itemCode));

        // Neon ItemPriceLists
        await UpsertSingleItemPriceNeonAsync(itemCode, priceListNum, price, currency, now, ct);

        // Neon Products projection
        var neonConn = await GetNeonConnectionAsync(ct);
        using var cmd = new NpgsqlCommand(
            $@"UPDATE ""Products"" SET ""{PriceColumn(priceListNum)}"" = @price, ""LastUpdated"" = @ts WHERE ""ItemCode"" = @ic",
            neonConn);
        cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric, price);
        cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.Timestamp, now);
        cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text, itemCode);
        await cmd.ExecuteNonQueryAsync(ct);

        return true;
    }

    // ── Integrity check ───────────────────────────────────────────────────────

    /// <summary>
    /// Compares SQLite ItemPriceLists vs SQLite Products projection vs Neon for each
    /// active item and PL1–PL5. Returns a row per (ItemCode, PriceListNum) with status.
    /// Does NOT repair — read-only.
    /// </summary>
    public async Task<List<PriceIntegrityRow>> CheckIntegrityAsync(
        IReadOnlyList<string>? itemCodes = null,
        CancellationToken ct = default)
    {
        // Load SQLite ItemPriceLists
        var ipl = _sqlite.ItemPriceLists.AsNoTracking();
        if (itemCodes is { Count: > 0 })
            ipl = ipl.Where(x => itemCodes.Contains(x.ItemCode));
        var sqliteIpl = await ipl.ToListAsync(ct);

        // Load SQLite Products for projection comparison
        var prods = _sqlite.Products.AsNoTracking();
        if (itemCodes is { Count: > 0 })
            prods = prods.Where(x => x.ItemCode != null && itemCodes.Contains(x.ItemCode));
        var sqliteProds = (await prods.ToListAsync(ct))
            .ToDictionary(p => p.ItemCode!, StringComparer.OrdinalIgnoreCase);

        // Load Neon ItemPriceLists (raw SQL — avoid EF migrations on Neon)
        var neonPrices = await LoadNeonItemPriceListsAsync(itemCodes, ct);

        var result = new List<PriceIntegrityRow>();

        foreach (var row in sqliteIpl)
        {
            if (!ActivePriceLists.Contains(row.PriceListNum)) continue;

            decimal? projValue = GetProductsProjection(sqliteProds, row.ItemCode, row.PriceListNum);
            bool     neonHas   = neonPrices.TryGetValue((row.ItemCode, row.PriceListNum), out var neonPrice);

            var status = "IN_SYNC";
            if (projValue is null)
                status = "PRODUCT_PROJECTION_MISMATCH";
            else if (Math.Abs(projValue.Value - row.Price) > 0.001m)
                status = "PRODUCT_PROJECTION_MISMATCH";

            if (!neonHas)
                status = status == "IN_SYNC" ? "NEON_PRICE_MISSING" : status + "|NEON_PRICE_MISSING";
            else if (Math.Abs(neonPrice - row.Price) > 0.001m)
                status = status == "IN_SYNC" ? "SAP_CACHE_MISMATCH" : status + "|SAP_CACHE_MISMATCH";

            result.Add(new PriceIntegrityRow
            {
                ItemCode      = row.ItemCode,
                PriceListNum  = row.PriceListNum,
                SqliteIplPrice = row.Price,
                ProductsProjectionPrice = projValue,
                NeonIplPrice  = neonHas ? neonPrice : null,
                Status        = status,
            });
        }

        // Items in Products but with no matching ItemPriceLists row (legacy gap)
        foreach (var prod in sqliteProds.Values)
        {
            foreach (int pl in ActivePriceLists)
            {
                if (sqliteIpl.Any(r => r.ItemCode == prod.ItemCode && r.PriceListNum == pl))
                    continue;
                decimal proj = GetProductsProjection(sqliteProds, prod.ItemCode!, pl) ?? 0;
                result.Add(new PriceIntegrityRow
                {
                    ItemCode     = prod.ItemCode!,
                    PriceListNum = pl,
                    SqliteIplPrice = null,
                    ProductsProjectionPrice = proj,
                    NeonIplPrice = null,
                    Status       = "SQLITE_PRICE_MISSING",
                });
            }
        }

        return result;
    }

    // ── SQLite helpers ────────────────────────────────────────────────────────

    private async Task<int> UpsertSqlitePriceListsAsync(
        List<SapPriceListDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO PriceLists (PriceListNum,PriceListName,BasePriceList,Factor,Currency,IsActive,LastUpdatedUtc)
VALUES ($num,$name,$base,$factor,$cur,$active,$ts)
ON CONFLICT(PriceListNum) DO UPDATE SET
 PriceListName=excluded.PriceListName, BasePriceList=excluded.BasePriceList,
 Factor=excluded.Factor, Currency=excluded.Currency, IsActive=excluded.IsActive,
 LastUpdatedUtc=excluded.LastUpdatedUtc";

        var pNum    = cmd.Parameters.Add("$num",    SqliteType.Integer);
        var pName   = cmd.Parameters.Add("$name",   SqliteType.Text);
        var pBase   = cmd.Parameters.Add("$base",   SqliteType.Integer);
        var pFactor = cmd.Parameters.Add("$factor", SqliteType.Real);
        var pCur    = cmd.Parameters.Add("$cur",    SqliteType.Text);
        var pActive = cmd.Parameters.Add("$active", SqliteType.Integer);
        var pTs     = cmd.Parameters.Add("$ts",     SqliteType.Text);

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        int count = 0;
        foreach (var r in rows)
        {
            pNum.Value    = r.PriceListNum;
            pName.Value   = r.PriceListName;
            pBase.Value   = (object?)r.BasePriceList ?? DBNull.Value;
            pFactor.Value = (double)r.Factor;
            pCur.Value    = r.Currency;
            pActive.Value = r.IsActive ? 1 : 0;
            pTs.Value     = ts;
            await cmd.ExecuteNonQueryAsync(ct);
            count++;
        }
        tx.Commit();
        return count;
    }

    private async Task<int> UpsertSqliteItemPriceListsAsync(
        List<SapItemPriceDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Batch in chunks to avoid SQLite parameter limits
        const int BatchSize = 500;
        int total = 0;

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx  = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO ItemPriceLists (ItemCode,PriceListNum,Price,Currency,LastUpdatedUtc)
VALUES ($ic,$pl,$price,$cur,$ts)
ON CONFLICT(ItemCode,PriceListNum) DO UPDATE SET
 Price=excluded.Price, Currency=excluded.Currency, LastUpdatedUtc=excluded.LastUpdatedUtc";

            var pIc    = cmd.Parameters.Add("$ic",    SqliteType.Text);
            var pPl    = cmd.Parameters.Add("$pl",    SqliteType.Integer);
            var pPrice = cmd.Parameters.Add("$price", SqliteType.Real);
            var pCur   = cmd.Parameters.Add("$cur",   SqliteType.Text);
            var pTs    = cmd.Parameters.Add("$ts",    SqliteType.Text);
            var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var r in batch)
            {
                pIc.Value    = r.ItemCode;
                pPl.Value    = r.PriceListNum;
                pPrice.Value = (double)r.Price;
                pCur.Value   = r.Currency;
                pTs.Value    = ts;
                await cmd.ExecuteNonQueryAsync(ct);
                total++;
            }
            tx.Commit();
        }
        return total;
    }

    private async Task<int> ProjectToProductsAsync(
        List<SapItemPriceDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Group by ItemCode — one UPDATE per item with all 5 columns
        var byItem = rows
            .GroupBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PriceListNum, r => r.Price),
                StringComparer.OrdinalIgnoreCase);

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        int count = 0;

        // Update each price list column independently to avoid overwriting unknown prices
        using var tx = conn.BeginTransaction();
        foreach (var (itemCode, plMap) in byItem)
        {
            foreach (var (plNum, price) in plMap)
            {
                if (!ActivePriceLists.Contains(plNum)) continue;
                string col = PriceColumn(plNum);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE Products SET {col} = $price, LastUpdated = $ts WHERE ItemCode = $ic";
                cmd.Parameters.AddWithValue("$price", (double)price);
                cmd.Parameters.AddWithValue("$ts",    ts);
                cmd.Parameters.AddWithValue("$ic",    itemCode);
                int rows2 = await cmd.ExecuteNonQueryAsync(ct);
                if (rows2 > 0) count++;
            }
        }
        tx.Commit();
        return count;
    }

    private async Task UpsertSingleItemPriceSqliteAsync(
        string itemCode, int plNum, decimal price, string currency,
        DateTime utcNow, CancellationToken ct)
    {
        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ItemPriceLists (ItemCode,PriceListNum,Price,Currency,LastUpdatedUtc)
VALUES ($ic,$pl,$price,$cur,$ts)
ON CONFLICT(ItemCode,PriceListNum) DO UPDATE SET
 Price=excluded.Price, Currency=excluded.Currency, LastUpdatedUtc=excluded.LastUpdatedUtc";
        cmd.Parameters.AddWithValue("$ic",    itemCode);
        cmd.Parameters.AddWithValue("$pl",    plNum);
        cmd.Parameters.AddWithValue("$price", (double)price);
        cmd.Parameters.AddWithValue("$cur",   currency);
        cmd.Parameters.AddWithValue("$ts",    utcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Neon helpers ──────────────────────────────────────────────────────────

    private async Task<int> UpsertNeonPriceListsAsync(
        List<SapPriceListDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var conn = await GetNeonConnectionAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var r in rows)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""PriceLists"" (""PriceListNum"",""PriceListName"",""BasePriceList"",""Factor"",""Currency"",""IsActive"",""LastUpdatedUtc"")
VALUES (@num,@name,@base,@factor,@cur,@active,@ts)
ON CONFLICT (""PriceListNum"") DO UPDATE SET
 ""PriceListName""=EXCLUDED.""PriceListName"", ""BasePriceList""=EXCLUDED.""BasePriceList"",
 ""Factor""=EXCLUDED.""Factor"", ""Currency""=EXCLUDED.""Currency"",
 ""IsActive""=EXCLUDED.""IsActive"", ""LastUpdatedUtc""=EXCLUDED.""LastUpdatedUtc""", conn, tx);

            cmd.Parameters.AddWithValue("@num",    NpgsqlDbType.Integer, r.PriceListNum);
            cmd.Parameters.AddWithValue("@name",   NpgsqlDbType.Text,    r.PriceListName);
            cmd.Parameters.AddWithValue("@base",   NpgsqlDbType.Integer, (object?)r.BasePriceList ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@factor", NpgsqlDbType.Numeric, r.Factor);
            cmd.Parameters.AddWithValue("@cur",    NpgsqlDbType.Text,    r.Currency);
            cmd.Parameters.AddWithValue("@active", NpgsqlDbType.Boolean, r.IsActive);
            cmd.Parameters.AddWithValue("@ts",     NpgsqlDbType.Timestamp, DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private async Task<int> UpsertNeonItemPriceListsAsync(
        List<SapItemPriceDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        const int BatchSize = 200;
        int total = 0;

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            var conn = await GetNeonConnectionAsync(ct);
            using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var r in batch)
            {
                using var cmd = new NpgsqlCommand(@"
INSERT INTO ""ItemPriceLists"" (""ItemCode"",""PriceListNum"",""Price"",""Currency"",""LastUpdatedUtc"")
VALUES (@ic,@pl,@price,@cur,@ts)
ON CONFLICT (""ItemCode"",""PriceListNum"") DO UPDATE SET
 ""Price""=EXCLUDED.""Price"", ""Currency""=EXCLUDED.""Currency"",
 ""LastUpdatedUtc""=EXCLUDED.""LastUpdatedUtc""", conn, tx);

                cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,      r.ItemCode);
                cmd.Parameters.AddWithValue("@pl",    NpgsqlDbType.Integer,   r.PriceListNum);
                cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric,   r.Price);
                cmd.Parameters.AddWithValue("@cur",   NpgsqlDbType.Text,      r.Currency);
                cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.Timestamp, DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync(ct);
                total++;
            }
            await tx.CommitAsync(ct);
        }
        return total;
    }

    private async Task<int> ProjectToNeonProductsAsync(
        List<SapItemPriceDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var conn = await GetNeonConnectionAsync(ct);
        var byItem = rows
            .GroupBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PriceListNum, r => r.Price),
                StringComparer.OrdinalIgnoreCase);

        int count = 0;
        using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (itemCode, plMap) in byItem)
        {
            foreach (var (plNum, price) in plMap)
            {
                if (!ActivePriceLists.Contains(plNum)) continue;
                string col = PriceColumn(plNum);
                using var cmd = new NpgsqlCommand(
                    $@"UPDATE ""Products"" SET ""{col}"" = @price, ""LastUpdated"" = @ts WHERE ""ItemCode"" = @ic",
                    conn, tx);
                cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric,   price);
                cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.Timestamp, DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,      itemCode);
                int updated = await cmd.ExecuteNonQueryAsync(ct);
                if (updated > 0) count++;
            }
        }
        await tx.CommitAsync(ct);
        return count;
    }

    private async Task UpsertSingleItemPriceNeonAsync(
        string itemCode, int plNum, decimal price, string currency,
        DateTime utcNow, CancellationToken ct)
    {
        var conn = await GetNeonConnectionAsync(ct);
        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""ItemPriceLists"" (""ItemCode"",""PriceListNum"",""Price"",""Currency"",""LastUpdatedUtc"")
VALUES (@ic,@pl,@price,@cur,@ts)
ON CONFLICT (""ItemCode"",""PriceListNum"") DO UPDATE SET
 ""Price""=EXCLUDED.""Price"", ""Currency""=EXCLUDED.""Currency"",
 ""LastUpdatedUtc""=EXCLUDED.""LastUpdatedUtc""", conn);
        cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,      itemCode);
        cmd.Parameters.AddWithValue("@pl",    NpgsqlDbType.Integer,   plNum);
        cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric,   price);
        cmd.Parameters.AddWithValue("@cur",   NpgsqlDbType.Text,      currency);
        cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.Timestamp, utcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<(string, int), decimal>> LoadNeonItemPriceListsAsync(
        IReadOnlyList<string>? itemCodes, CancellationToken ct)
    {
        var result = new Dictionary<(string, int), decimal>();
        try
        {
            var conn = await GetNeonConnectionAsync(ct);
            string where = itemCodes is { Count: > 0 }
                ? $"WHERE \"ItemCode\" = ANY(@codes)"
                : "";
            using var cmd = new NpgsqlCommand(
                $@"SELECT ""ItemCode"",""PriceListNum"",""Price"" FROM ""ItemPriceLists"" {where}", conn);
            if (itemCodes is { Count: > 0 })
                cmd.Parameters.AddWithValue("@codes", NpgsqlDbType.Array | NpgsqlDbType.Text, itemCodes.ToArray());
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                result[(rdr.GetString(0), rdr.GetInt32(1))] = rdr.GetDecimal(2);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[PriceSync] Could not load Neon ItemPriceLists for integrity check.");
        }
        return result;
    }

    private async Task<NpgsqlConnection> GetNeonConnectionAsync(CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return conn;
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    internal static string PriceColumn(int priceListNum) => priceListNum switch
    {
        1 => "Price01",
        2 => "Price02",
        3 => "Price",
        4 => "Price04",
        5 => "Price05",
        _ => throw new ArgumentOutOfRangeException(nameof(priceListNum), $"Price list {priceListNum} is not supported.")
    };

    private static decimal? GetProductsProjection(
        Dictionary<string, CachedProduct> prods, string itemCode, int plNum)
    {
        if (!prods.TryGetValue(itemCode, out var p)) return null;
        return plNum switch
        {
            1 => p.Price01,
            2 => p.Price02,
            3 => p.Price,
            4 => p.Price04,
            5 => p.Price05,
            _ => null
        };
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record SapPriceListDto(
    int     PriceListNum,
    string  PriceListName,
    int?    BasePriceList,
    decimal Factor,
    string  Currency,
    bool    IsActive);

public record SapItemPriceDto(
    string  ItemCode,
    int     PriceListNum,
    decimal Price,
    string  Currency);

public record PriceSyncResult
{
    public int    SqlitePriceListRows  { get; init; }
    public int    SqliteItemPriceRows  { get; init; }
    public int    SqliteProjectedRows  { get; init; }
    public int    NeonPriceListRows    { get; init; }
    public int    NeonItemPriceRows    { get; init; }
    public int    NeonProjectedRows    { get; init; }
    public double ElapsedSeconds       { get; init; }
}

public record PriceIntegrityRow
{
    public string   ItemCode                { get; init; } = "";
    public int      PriceListNum            { get; init; }
    public decimal? SqliteIplPrice          { get; init; }
    public decimal? ProductsProjectionPrice { get; init; }
    public decimal? NeonIplPrice            { get; init; }
    public string   Status                  { get; init; } = "IN_SYNC";
}
