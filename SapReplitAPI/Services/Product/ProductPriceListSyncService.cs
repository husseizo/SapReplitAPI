using System.Data;
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

    // SyncMetadata.Type keys — one watermark for the SAP read step, one for the
    // Neon mirror step, per table. Kept separate so a Neon-only failure never
    // masks a successful SAP/SQLite sync (see FullSyncAsync failure semantics).
    private const string SourceKeyPriceLists         = "PriceLists.Source";
    private const string SourceKeyItemPriceLists     = "ItemPriceLists.Source";
    private const string NeonMirrorKeyPriceLists     = "PriceLists.NeonMirror";
    private const string NeonMirrorKeyItemPriceLists = "ItemPriceLists.NeonMirror";

    // A sync watermark older than this is considered stale for health reporting.
    // The job runs every 15 minutes; 2x that plus slack tolerates one missed run.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(35);

    // The SAP DI API Company connection is not safe for concurrent use from
    // multiple threads. The Quartz job (its own thread pool) and a manually
    // triggered sync (via the background task queue, a different execution
    // path) can otherwise both call FullSyncAsync at the same moment — this
    // was observed live to hang the SAP connection indefinitely with no
    // exception. Static so it's shared across every Scoped instance of this
    // service.
    private static readonly SemaphoreSlim _syncGate = new(1, 1);

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
    ///
    /// Failure semantics (SAP is the source of truth):
    ///   - SAP read fails            → throws immediately; SQLite/Neon are never touched
    ///                                  and existing cached prices are left exactly as-is.
    ///   - SAP read + SQLite write ok, Neon mirror fails
    ///                               → logged as a CACHE_SYNC_WARNING and swallowed;
    ///                                  SQLite is authoritative/correct, method returns
    ///                                  normally, and the next scheduled run retries the
    ///                                  mirror. SAP is never rolled back (this job never
    ///                                  writes to SAP).
    /// </summary>
    public async Task<PriceSyncResult> FullSyncAsync(CancellationToken ct = default)
    {
        if (!await _syncGate.WaitAsync(0, ct))
        {
            _log.LogWarning("[PriceSync] Skipped — another price sync is already running (SAP connection is not safe for concurrent use).");
            return new PriceSyncResult { NeonMirrorError = "SKIPPED_ALREADY_RUNNING" };
        }
        try
        {
            return await RunFullSyncAsync(ct);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<PriceSyncResult> RunFullSyncAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("[PriceSync] Full price sync started.");

        // 1. Fetch from SAP — read-only. If this throws, propagate immediately without
        // touching SQLite or Neon; existing PriceLists/ItemPriceLists/Products prices
        // are left completely untouched (no clearing, no zeroing, no destructive push).
        List<SapPriceListDto> priceLists;
        List<SapItemPriceDto> itemPrices;
        try
        {
            priceLists = _sap.GetPriceLists();
            itemPrices = _sap.GetAllItemPrices(ActivePriceLists);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[PriceSync] SAP_READ_FAILED — existing cached PriceLists/ItemPriceLists/Products prices left untouched.");
            throw;
        }

        _log.LogInformation("[PriceSync] SAP returned {PL} price lists, {IP} item-price rows.",
            priceLists.Count, itemPrices.Count);

        // 2. Persist to SQLite — local cache becomes authoritative once SAP read succeeded.
        int plRows, ipRows, projRows;
        try
        {
            plRows = await UpsertSqlitePriceListsAsync(priceLists, ct);
            ipRows = await UpsertSqliteItemPriceListsAsync(itemPrices, ct);
            projRows = await ProjectToProductsAsync(itemPrices, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[PriceSync] SQLITE_WRITE_FAILED — SAP read succeeded but local cache write failed. Neon mirror skipped this run.");
            throw;
        }

        await UpdateSyncMetadataAsync(SourceKeyPriceLists, ct);
        await UpdateSyncMetadataAsync(SourceKeyItemPriceLists, ct);

        // 3. Mirror to Neon — best-effort. SQLite is already correct/authoritative;
        // a Neon failure here must never roll back the SQLite write, never push a
        // destructive/empty state, and must not fail the whole sync — it just logs
        // and lets the next scheduled run retry the mirror.
        int neonPl = 0, neonIp = 0, neonProj = 0;
        string? mirrorError = null;
        try
        {
            neonPl = await UpsertNeonPriceListsAsync(priceLists, ct);
            neonIp = await UpsertNeonItemPriceListsAsync(itemPrices, ct);
            neonProj = await ProjectToNeonProductsAsync(itemPrices, ct);
            await UpdateSyncMetadataAsync(NeonMirrorKeyPriceLists, ct);
            await UpdateSyncMetadataAsync(NeonMirrorKeyItemPriceLists, ct);
        }
        catch (Exception ex)
        {
            mirrorError = ex.Message;
            _log.LogWarning(ex,
                "[PriceSync] CACHE_SYNC_WARNING — Neon mirror failed after a successful SQLite write. " +
                "SQLite remains authoritative/correct; Neon will retry on the next scheduled sync.");
        }

        sw.Stop();
        _log.LogInformation(
            "[PriceSync] Full sync done in {Sec:0.0}s. SQLite: {PL} PLists, {IP} ItemPrices, {Proj} Products projected. " +
            "Neon: {NPL} PLists, {NIP} ItemPrices, {NProj} Products projected. MirrorError={Err}",
            sw.Elapsed.TotalSeconds, plRows, ipRows, projRows, neonPl, neonIp, neonProj, mirrorError ?? "none");

        return new PriceSyncResult
        {
            SqlitePriceListRows  = plRows,
            SqliteItemPriceRows  = ipRows,
            SqliteProjectedRows  = projRows,
            NeonPriceListRows    = neonPl,
            NeonItemPriceRows    = neonIp,
            NeonProjectedRows    = neonProj,
            ElapsedSeconds       = sw.Elapsed.TotalSeconds,
            NeonMirrorError      = mirrorError,
        };
    }

    private async Task UpdateSyncMetadataAsync(string type, CancellationToken ct)
    {
        var meta = await _sqlite.SyncMetadata.FirstOrDefaultAsync(x => x.Type == type, ct);
        if (meta == null)
            await _sqlite.SyncMetadata.AddAsync(new SyncMetadata { Type = type, LastSyncedAt = DateTime.UtcNow }, ct);
        else
            meta.LastSyncedAt = DateTime.UtcNow;
        await _sqlite.SaveChangesAsync(ct);
    }

    // ── Single-item repair ────────────────────────────────────────────────────

    /// <summary>
    /// Repairs ItemPriceLists + Products projection for one item from live SAP.
    /// Used by RepairCacheAsync after a successful SAP price mutation readback.
    /// No SAP mutation — read-only. SQLite and Neon are each updated atomically
    /// (ItemPriceLists + Products projection in one transaction per side) via
    /// UpdateSqliteNormalizedPriceAsync / UpdateNeonNormalizedPriceAsync.
    /// </summary>
    public async Task<bool> RepairItemPriceAsync(
        string itemCode, int priceListNum, decimal price, string currency,
        CancellationToken ct = default)
    {
        await UpdateSqliteNormalizedPriceAsync(itemCode, priceListNum, price, currency, ct);
        await UpdateNeonNormalizedPriceAsync(itemCode, priceListNum, price, currency, ct);
        return true;
    }

    /// <summary>
    /// Atomically updates SQLite ItemPriceLists + the Products compatibility projection
    /// for one (ItemCode, PriceListNum) inside a single SQLite transaction — a successful
    /// call never leaves the normalized and compatibility models disagreeing.
    /// Returns true if a Products row existed and was updated.
    /// </summary>
    public async Task<bool> UpdateSqliteNormalizedPriceAsync(
        string itemCode, int priceListNum, decimal price, string currency,
        CancellationToken ct = default)
    {
        var conn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var now = DateTime.UtcNow;
        var ts  = now.ToString("yyyy-MM-dd HH:mm:ss");

        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO ItemPriceLists (ItemCode,PriceListNum,Price,Currency,LastUpdatedUtc)
VALUES ($ic,$pl,$price,$cur,$ts)
ON CONFLICT(ItemCode,PriceListNum) DO UPDATE SET
 Price=excluded.Price, Currency=excluded.Currency, LastUpdatedUtc=excluded.LastUpdatedUtc";
                cmd.Parameters.AddWithValue("$ic",    itemCode);
                cmd.Parameters.AddWithValue("$pl",    priceListNum);
                cmd.Parameters.AddWithValue("$price", (double)price);
                cmd.Parameters.AddWithValue("$cur",   currency);
                cmd.Parameters.AddWithValue("$ts",    ts);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            int rows;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE Products SET {PriceColumn(priceListNum)} = $price, LastUpdated = $ts WHERE ItemCode = $ic";
                cmd.Parameters.AddWithValue("$price", (double)price);
                cmd.Parameters.AddWithValue("$ts",    ts);
                cmd.Parameters.AddWithValue("$ic",    itemCode);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return rows > 0;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Atomically updates Neon ItemPriceLists + the Products compatibility projection
    /// for one (ItemCode, PriceListNum) inside a single Postgres transaction.
    /// Returns true if a Products row existed and was updated.
    /// </summary>
    public async Task<bool> UpdateNeonNormalizedPriceAsync(
        string itemCode, int priceListNum, decimal price, string currency,
        CancellationToken ct = default)
    {
        var conn = await GetNeonConnectionAsync(ct);
        var now  = DateTime.UtcNow;

        using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""ItemPriceLists"" (""ItemCode"",""PriceListNum"",""Price"",""Currency"",""LastUpdatedUtc"")
VALUES (@ic,@pl,@price,@cur,@ts)
ON CONFLICT (""ItemCode"",""PriceListNum"") DO UPDATE SET
 ""Price""=EXCLUDED.""Price"", ""Currency""=EXCLUDED.""Currency"",
 ""LastUpdatedUtc""=EXCLUDED.""LastUpdatedUtc""", conn, tx))
            {
                cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,      itemCode);
                cmd.Parameters.AddWithValue("@pl",    NpgsqlDbType.Integer,   priceListNum);
                cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric,   price);
                cmd.Parameters.AddWithValue("@cur",   NpgsqlDbType.Text,      currency);
                cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.TimestampTz, now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            int rows;
            using (var cmd = new NpgsqlCommand(
                $@"UPDATE ""Products"" SET ""{PriceColumn(priceListNum)}"" = @price, ""LastUpdated"" = @ts WHERE ""ItemCode"" = @ic",
                conn, tx))
            {
                cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric,   price);
                cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.TimestampTz, now);
                cmd.Parameters.AddWithValue("@ic",    NpgsqlDbType.Text,      itemCode);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return rows > 0;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── Integrity check ───────────────────────────────────────────────────────

    /// <summary>
    /// Compares SQLite ItemPriceLists vs SQLite Products projection vs Neon for each
    /// active item and PL1–PL5. Returns a row per (ItemCode, PriceListNum) with status.
    /// Does NOT repair — read-only.
    ///
    /// Without an itemCodes filter this would scan the entire cached catalogue (and
    /// issue an unfiltered Neon query), so a full-catalogue scan requires an explicit
    /// opt-in via allowFullScan; otherwise this throws InvalidOperationException.
    /// </summary>
    public async Task<List<PriceIntegrityRow>> CheckIntegrityAsync(
        IReadOnlyList<string>? itemCodes = null,
        bool allowFullScan = false,
        CancellationToken ct = default)
    {
        if ((itemCodes is null || itemCodes.Count == 0) && !allowFullScan)
            throw new InvalidOperationException(
                "price-integrity requires an itemCode filter. Pass allowFullScan=true to explicitly scan the entire cached catalogue.");

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
            cmd.Parameters.AddWithValue("@ts",     NpgsqlDbType.TimestampTz, DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// Bulk upsert via a single multi-row INSERT per batch (VALUES (...),(...),...),
    /// matching the pattern in NeonProductSyncService.UpsertBatchAsync. The earlier
    /// version issued one round trip PER ROW inside a shared transaction, which for
    /// a full catalog (~50k rows) meant ~50k sequential network round trips to a
    /// remote Postgres — tens of minutes to hours. Batching the statement itself
    /// (not just the transaction) cuts that to one round trip per ~500 rows.
    /// </summary>
    private async Task<int> UpsertNeonItemPriceListsAsync(
        List<SapItemPriceDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        const int BatchSize = 500;
        var conn = await GetNeonConnectionAsync(ct);
        int total = 0;
        var now = DateTime.UtcNow;

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync(ct);

            var sb = new System.Text.StringBuilder(
                @"INSERT INTO ""ItemPriceLists"" (""ItemCode"",""PriceListNum"",""Price"",""Currency"",""LastUpdatedUtc"") VALUES ");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@ic{i},@pl{i},@price{i},@cur{i},@ts{i})");
            }
            sb.Append(@" ON CONFLICT (""ItemCode"",""PriceListNum"") DO UPDATE SET " +
                      @"""Price""=EXCLUDED.""Price"", ""Currency""=EXCLUDED.""Currency"", ""LastUpdatedUtc""=EXCLUDED.""LastUpdatedUtc""");

            using var cmd = new NpgsqlCommand(sb.ToString(), conn, tx);
            for (int i = 0; i < batch.Count; i++)
            {
                var r = batch[i];
                cmd.Parameters.AddWithValue($"@ic{i}",    NpgsqlDbType.Text,        r.ItemCode);
                cmd.Parameters.AddWithValue($"@pl{i}",    NpgsqlDbType.Integer,     r.PriceListNum);
                cmd.Parameters.AddWithValue($"@price{i}", NpgsqlDbType.Numeric,     r.Price);
                cmd.Parameters.AddWithValue($"@cur{i}",   NpgsqlDbType.Text,        r.Currency);
                cmd.Parameters.AddWithValue($"@ts{i}",    NpgsqlDbType.TimestampTz, now);
            }
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            total += batch.Count;
        }
        return total;
    }

    /// <summary>
    /// Bulk projection via UPDATE ... FROM (VALUES ...) per price-list column, batched.
    /// The earlier version issued one UPDATE round trip PER (item, price-list) pair —
    /// for a full catalog that's potentially hundreds of thousands of round trips in a
    /// single unbounded transaction. Grouping by price-list column (only 5 distinct
    /// columns exist) and batching the VALUES list cuts this to a handful of round
    /// trips per price list.
    /// </summary>
    private async Task<int> ProjectToNeonProductsAsync(
        List<SapItemPriceDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        const int BatchSize = 500;
        var conn = await GetNeonConnectionAsync(ct);
        // Products.LastUpdated is a plain 'timestamp' (no time zone) column on Neon,
        // matching NeonProductSyncService's convention — Npgsql rejects Kind=Utc
        // for that column type, so tag the value Unspecified while keeping the
        // actual UTC wall-clock value.
        var ts = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        int count = 0;
        foreach (int plNum in ActivePriceLists)
        {
            string col = PriceColumn(plNum);
            var plRows = rows.Where(r => r.PriceListNum == plNum).ToList();
            if (plRows.Count == 0) continue;

            for (int off = 0; off < plRows.Count; off += BatchSize)
            {
                var batch = plRows.Skip(off).Take(BatchSize).ToList();
                using var tx = await conn.BeginTransactionAsync(ct);

                var sb = new System.Text.StringBuilder("(VALUES ");
                for (int i = 0; i < batch.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@ic{i},@price{i})");
                }
                sb.Append(") AS v(\"ItemCode\",\"Price\")");

                string sql = $@"
UPDATE ""Products"" AS p SET ""{col}"" = v.""Price"", ""LastUpdated"" = @ts
FROM {sb} WHERE p.""ItemCode"" = v.""ItemCode""";

                using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("@ts", NpgsqlDbType.Timestamp, ts);
                for (int i = 0; i < batch.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"@ic{i}",    NpgsqlDbType.Text,    batch[i].ItemCode);
                    cmd.Parameters.AddWithValue($"@price{i}", NpgsqlDbType.Numeric, batch[i].Price);
                }
                int updated = await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                count += updated;
            }
        }
        return count;
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

    // ── Health ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only observability snapshot: sync watermarks, row counts, and a
    /// computed status. Never mutates anything.
    /// </summary>
    public async Task<PriceListHealthReport> GetHealthAsync(CancellationToken ct = default)
    {
        string? lastError = null;

        var srcPl  = await _sqlite.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Type == SourceKeyPriceLists, ct);
        var srcIp  = await _sqlite.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Type == SourceKeyItemPriceLists, ct);
        var mirPl  = await _sqlite.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Type == NeonMirrorKeyPriceLists, ct);
        var mirIp  = await _sqlite.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Type == NeonMirrorKeyItemPriceLists, ct);

        int sqlitePlCount = await _sqlite.PriceLists.AsNoTracking().CountAsync(ct);
        int sqliteIpCount = await _sqlite.ItemPriceLists.AsNoTracking().CountAsync(ct);

        int neonPlCount = -1, neonIpCount = -1;
        try
        {
            var conn = await GetNeonConnectionAsync(ct);
            using (var cmd = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""PriceLists""", conn))
                neonPlCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            using (var cmd = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""ItemPriceLists""", conn))
                neonIpCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (Exception ex)
        {
            lastError = $"Neon row-count read failed: {ex.Message}";
        }

        return ComputeHealth(
            srcPl?.LastSyncedAt, srcIp?.LastSyncedAt, mirPl?.LastSyncedAt, mirIp?.LastSyncedAt,
            sqlitePlCount, sqliteIpCount, neonPlCount, neonIpCount, lastError, DateTime.UtcNow);
    }

    /// <summary>
    /// Pure status computation, factored out of GetHealthAsync so it can be unit
    /// tested without a live Neon/Postgres connection.
    /// </summary>
    internal static PriceListHealthReport ComputeHealth(
        DateTime? srcPlSyncedAt, DateTime? srcIpSyncedAt, DateTime? mirPlSyncedAt, DateTime? mirIpSyncedAt,
        int sqlitePlCount, int sqliteIpCount, int neonPlCount, int neonIpCount,
        string? lastError, DateTime now)
    {
        DateTime? lastSapSyncUtc  = Min(srcPlSyncedAt, srcIpSyncedAt);
        DateTime? lastNeonSyncUtc = Min(mirPlSyncedAt, mirIpSyncedAt);
        double? lagSeconds = lastSapSyncUtc.HasValue ? (now - lastSapSyncUtc.Value).TotalSeconds : null;

        string status;
        if (lastError is not null)
            status = "ERROR";
        else if (lastSapSyncUtc is null || now - lastSapSyncUtc.Value > StaleThreshold)
            status = "SAP_SYNC_STALE";
        else if (lastNeonSyncUtc is null || now - lastNeonSyncUtc.Value > StaleThreshold)
            status = "NEON_MIRROR_STALE";
        else if (sqlitePlCount != neonPlCount || sqliteIpCount != neonIpCount)
            status = "ROW_COUNT_MISMATCH";
        else
            status = "HEALTHY";

        return new PriceListHealthReport
        {
            Status                = status,
            LastSapSyncUtc        = lastSapSyncUtc,
            LastNeonSyncUtc       = lastNeonSyncUtc,
            SqlitePriceListCount  = sqlitePlCount,
            SqliteItemPriceCount  = sqliteIpCount,
            NeonPriceListCount    = neonPlCount,
            NeonItemPriceCount    = neonIpCount,
            LagSeconds            = lagSeconds,
            LastError             = lastError,
        };
    }

    private static DateTime? Min(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value < b.Value ? a : b;
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
    public int     SqlitePriceListRows  { get; init; }
    public int     SqliteItemPriceRows  { get; init; }
    public int     SqliteProjectedRows  { get; init; }
    public int     NeonPriceListRows    { get; init; }
    public int     NeonItemPriceRows    { get; init; }
    public int     NeonProjectedRows    { get; init; }
    public double  ElapsedSeconds       { get; init; }
    // Non-null when SAP read + SQLite write succeeded but the Neon mirror step failed
    // (CACHE_SYNC_WARNING). SQLite is authoritative/correct in that case; the next
    // scheduled sync retries the mirror.
    public string? NeonMirrorError      { get; init; }
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

public record PriceListHealthReport
{
    public string    Status               { get; init; } = "ERROR";
    public DateTime? LastSapSyncUtc       { get; init; }
    public DateTime? LastNeonSyncUtc      { get; init; }
    public int       SqlitePriceListCount { get; init; }
    public int       SqliteItemPriceCount { get; init; }
    public int       NeonPriceListCount   { get; init; }
    public int       NeonItemPriceCount   { get; init; }
    public double?   LagSeconds           { get; init; }
    public string?   LastError            { get; init; }
}
