using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Services.Neon;
using Xunit;

namespace SapReplitAPI.Tests.Product;

/// <summary>
/// ADMIN_PROJ_01–08 — Post-deploy hardening gate: NeonProductSyncService.FullReplaceAsync
/// (the admin/manual POST /api/products/sync-to-neon path) previously TRUNCATEd
/// "Products" in its own committed transaction, then committed each reload batch
/// separately — exactly the reader-blocking-and-partial-commit bug already fixed
/// once for NeonSyncJob.ReplaceProductsAsync (the scheduled path), reintroduced
/// here because the two were independent implementations. Fixed by having this
/// path call NeonSyncJob.DeleteAllRowsAsync/UpsertProductsBatchAsync directly, so
/// there is now exactly one Products full-replace projection, not two that can
/// drift. These tests mirror ProductNeonReplacePostgresTests.cs's PROJ_09-12
/// technique (statement-level triggers, txid_current() logging, forced-failure
/// rollback) applied to this admin path instead. Reuses ProductsPostgresFactAttribute
/// from that same file/namespace — same opt-in, disposable-schema-only convention.
/// No production Neon is read or written anywhere in this file.
/// </summary>
public sealed class AdminProductFullReplacePostgresTests
{
    private static async Task Run(Func<CacheDbContext, NeonDbContext, NpgsqlConnection, Task> test)
    {
        var schema = "adminproducts_test_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = schema, Pooling = false };
        await using var pg = new NpgsqlConnection(cs.ConnectionString); await pg.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", pg).ExecuteNonQueryAsync();
        try
        {
            await using var sqliteConnection = new SqliteConnection("Data Source=:memory:"); await sqliteConnection.OpenAsync();
            await using var sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(sqliteConnection).Options);
            await sqlite.Database.EnsureCreatedAsync();
            await using var neon = new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg).Options);
            await new NpgsqlCommand(neon.Database.GenerateCreateScript(), pg).ExecuteNonQueryAsync();
            await test(sqlite, neon, pg);
        }
        finally { await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", pg).ExecuteNonQueryAsync(); }
    }

    private static Task<int> FullReplace(CacheDbContext sqlite, NeonDbContext neon)
        => new NeonProductSyncService(sqlite, neon, NullLogger<NeonProductSyncService>.Instance).FullReplaceAsync();

    private static CachedProduct Product(string code, decimal p1, decimal p2, decimal p3, decimal p4, decimal p5, string? oe = null) => new()
    {
        ItemCode = code, ItemName = code + " Item", U_Item_Name = "",
        Price01 = p1, Price02 = p2, Price = p3, Price04 = p4, Price05 = p5,
        WhsCode = "ALL", LastUpdated = DateTime.UtcNow, U_OE_Numbers = oe,
    };

    private static async Task<int> ProductCount(NpgsqlConnection pg)
        => Convert.ToInt32(await new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Products""", pg).ExecuteScalarAsync());

    // PROJ_07's reference row: full BM10001 values incl. slash-separated U_OE_Numbers.
    private static CachedProduct BM10001() => Product("BM10001", 22187.81m, 35000m, 65000m, 20000m, 41093.91m, "11427566327/11427541827");
    // PROJ_08's reference row: full BM10002 values, U_OE_Numbers null.
    private static CachedProduct BM10002() => Product("BM10002", 860335m, 1700000m, 1700000m, 1360000m, 1280167.5m, null);

    // ADMIN_PROJ_01 — FullReplaceAsync issues a DELETE statement, never a TRUNCATE.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_01_FullReplaceAsync_UsesDeleteNotTruncate()
        => await Run(async (sqlite, neon, pg) =>
        {
            await new NpgsqlCommand(@"
                CREATE TABLE ""__admin_proj_log"" (kind text NOT NULL);
                CREATE FUNCTION __admin_proj_log_delete() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__admin_proj_log"" VALUES ('DELETE'); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_admin_products_delete AFTER DELETE ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __admin_proj_log_delete();
                CREATE FUNCTION __admin_proj_log_truncate() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__admin_proj_log"" VALUES ('TRUNCATE'); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_admin_products_truncate AFTER TRUNCATE ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __admin_proj_log_truncate();
            ", pg).ExecuteNonQueryAsync();

            sqlite.Products.Add(BM10001());
            await sqlite.SaveChangesAsync();

            await FullReplace(sqlite, neon);

            var kinds = new List<string>();
            await using (var rdr = await new NpgsqlCommand(@"SELECT kind FROM ""__admin_proj_log""", pg).ExecuteReaderAsync())
                while (await rdr.ReadAsync()) kinds.Add(rdr.GetString(0));

            Assert.Contains("DELETE", kinds);
            Assert.DoesNotContain("TRUNCATE", kinds);
        });

    // ADMIN_PROJ_02 — DELETE and every reload batch INSERT share exactly one transaction.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_02_DeleteAndReload_ExecuteInSameTransaction()
        => await Run(async (sqlite, neon, pg) =>
        {
            await new NpgsqlCommand(@"
                CREATE TABLE ""__admin_proj_txid_log"" (kind text NOT NULL, txid bigint NOT NULL);
                CREATE FUNCTION __admin_proj_log_delete_txid() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__admin_proj_txid_log"" VALUES ('DELETE', txid_current()); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_admin_products_delete_txid AFTER DELETE ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __admin_proj_log_delete_txid();
                CREATE FUNCTION __admin_proj_log_insert_txid() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__admin_proj_txid_log"" VALUES ('INSERT', txid_current()); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_admin_products_insert_txid AFTER INSERT ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __admin_proj_log_insert_txid();
            ", pg).ExecuteNonQueryAsync();

            sqlite.Products.Add(BM10001());
            sqlite.Products.Add(BM10002());
            await sqlite.SaveChangesAsync();

            await FullReplace(sqlite, neon);

            var kinds = new List<string>();
            var txids = new List<long>();
            await using (var rdr = await new NpgsqlCommand(@"SELECT kind, txid FROM ""__admin_proj_txid_log""", pg).ExecuteReaderAsync())
                while (await rdr.ReadAsync()) { kinds.Add(rdr.GetString(0)); txids.Add(rdr.GetInt64(1)); }

            Assert.Contains("DELETE", kinds);
            Assert.Contains("INSERT", kinds);
            Assert.Single(txids.Distinct());
        });

    // ADMIN_PROJ_03 — commit happens only once, after all batches complete: a failure
    // in a later batch rolls back the DELETE and every earlier batch's inserts too.
    // NeonSyncJob.BatchSize = 500 (shared constant, confirmed via source), so 501
    // rows force exactly two UpsertProductsBatchAsync calls inside one transaction.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_03_FailureInLaterBatch_RollsBackDeleteAndEarlierBatches()
        => await Run(async (sqlite, neon, pg) =>
        {
            await new NpgsqlCommand(@"ALTER TABLE ""Products"" ADD CONSTRAINT reject_negative_price01 CHECK (""Price01"" >= 0)", pg).ExecuteNonQueryAsync();

            for (int i = 1; i <= 500; i++)
                sqlite.Products.Add(Product($"AP{i:D4}", 1m, 2m, 3m, 4m, 5m));
            sqlite.Products.Add(Product("BADROW", -1m, 0m, 0m, 0m, 0m));
            await sqlite.SaveChangesAsync();

            await Assert.ThrowsAsync<PostgresException>(() => FullReplace(sqlite, neon));

            // Batch 1's 500 valid rows must NOT survive — proves one commit at the
            // end, not per-batch, and that the DELETE rolled back too.
            Assert.Equal(0, await ProductCount(pg));
        });

    // ADMIN_PROJ_04 — readers never observe an empty or partially-rebuilt Products
    // table, only ever the fully-old or fully-new committed dataset. Proven with a
    // real concurrent reader on a second connection, polling tightly while a
    // large-enough replace (2000 rows, several batches) is in flight — the
    // assertion (no zero, no in-between count) holds regardless of whether any
    // sample actually lands mid-transaction, and is strengthened whenever one does.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_04_ReadersNeverObserveEmptyOrPartialReplacement()
        => await Run(async (sqlite, neon, pg) =>
        {
            // Baseline: one successful replace so there is an "old" committed dataset.
            sqlite.Products.Add(BM10001());
            await sqlite.SaveChangesAsync();
            await FullReplace(sqlite, neon);
            int oldCount = await ProductCount(pg);
            Assert.Equal(1, oldCount);

            for (int i = 1; i <= 2000; i++)
                sqlite.Products.Add(Product($"AQ{i:D5}", 1m, 2m, 3m, 4m, 5m));
            await sqlite.SaveChangesAsync();
            int newCount = 2001; // BM10001 + 2000 new rows, same ItemCode never collides

            var schema = new NpgsqlConnectionStringBuilder(pg.ConnectionString).SearchPath;
            var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = schema, Pooling = false };
            await using var reader = new NpgsqlConnection(cs.ConnectionString);
            await reader.OpenAsync();

            var observed = new List<int>();
            using var cts = new CancellationTokenSource();
            var pollTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var c = Convert.ToInt32(await new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Products""", reader).ExecuteScalarAsync());
                        lock (observed) observed.Add(c);
                    }
                    catch { /* transient — ignore, this is a best-effort concurrent sampler */ }
                    await Task.Delay(2);
                }
            });

            await FullReplace(sqlite, neon);
            cts.Cancel();
            try { await pollTask; } catch (OperationCanceledException) { }

            Assert.Equal(newCount, await ProductCount(pg));
            lock (observed)
            {
                Assert.DoesNotContain(0, observed);
                Assert.All(observed, c => Assert.True(c == oldCount || c == newCount,
                    $"Reader observed partial replacement state: {c} rows (expected {oldCount} or {newCount})"));
            }
        });

    // ADMIN_PROJ_05 — PL1–PL5 survive full replacement exactly, for both reference items.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_05_PL1Through5_SurviveFullReplacement()
        => await Run(async (sqlite, neon, pg) =>
        {
            sqlite.Products.Add(BM10001());
            sqlite.Products.Add(BM10002());
            await sqlite.SaveChangesAsync();

            await FullReplace(sqlite, neon);

            await using var rdr = await new NpgsqlCommand(
                @"SELECT ""ItemCode"",""Price01"",""Price02"",""Price"",""Price04"",""Price05"" FROM ""Products"" ORDER BY ""ItemCode""", pg).ExecuteReaderAsync();
            Assert.True(await rdr.ReadAsync());
            Assert.Equal("BM10001", rdr.GetString(0));
            Assert.Equal(22187.81m, rdr.GetDecimal(1));
            Assert.Equal(35000m, rdr.GetDecimal(2));
            Assert.Equal(65000m, rdr.GetDecimal(3));
            Assert.Equal(20000m, rdr.GetDecimal(4));
            Assert.Equal(41093.91m, rdr.GetDecimal(5));
            Assert.True(await rdr.ReadAsync());
            Assert.Equal("BM10002", rdr.GetString(0));
            Assert.Equal(860335m, rdr.GetDecimal(1));
            Assert.Equal(1700000m, rdr.GetDecimal(2));
            Assert.Equal(1700000m, rdr.GetDecimal(3));
            Assert.Equal(1360000m, rdr.GetDecimal(4));
            Assert.Equal(1280167.5m, rdr.GetDecimal(5));
        });

    // ADMIN_PROJ_06 — U_OE_Numbers survives full replacement exactly: populated
    // (slash-separated) and NULL cases, both proven against the real column.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_06_UOENumbers_SurvivesFullReplacement()
        => await Run(async (sqlite, neon, pg) =>
        {
            sqlite.Products.Add(BM10001()); // "11427566327/11427541827"
            sqlite.Products.Add(BM10002()); // null
            await sqlite.SaveChangesAsync();

            await FullReplace(sqlite, neon);

            await using var rdr = await new NpgsqlCommand(
                @"SELECT ""ItemCode"",""U_OE_Numbers"" FROM ""Products"" ORDER BY ""ItemCode""", pg).ExecuteReaderAsync();
            Assert.True(await rdr.ReadAsync());
            Assert.Equal("BM10001", rdr.GetString(0));
            Assert.Equal("11427566327/11427541827", rdr.GetString(1));
            Assert.True(await rdr.ReadAsync());
            Assert.Equal("BM10002", rdr.GetString(0));
            Assert.True(await rdr.IsDBNullAsync(1));
        });

    // ADMIN_PROJ_07 — BM10001 full-row reference values preserved through the admin path.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_07_BM10001_ReferenceValuesPreserved()
        => await Run(async (sqlite, neon, pg) =>
        {
            sqlite.Products.Add(BM10001());
            await sqlite.SaveChangesAsync();
            await FullReplace(sqlite, neon);

            await using var rdr = await new NpgsqlCommand(
                @"SELECT ""ItemCode"",""Price01"",""Price02"",""Price"",""Price04"",""Price05"",""U_OE_Numbers"" FROM ""Products"" WHERE ""ItemCode""='BM10001'", pg).ExecuteReaderAsync();
            Assert.True(await rdr.ReadAsync());
            Assert.Equal(22187.81m, rdr.GetDecimal(1));
            Assert.Equal(35000m, rdr.GetDecimal(2));
            Assert.Equal(65000m, rdr.GetDecimal(3));
            Assert.Equal(20000m, rdr.GetDecimal(4));
            Assert.Equal(41093.91m, rdr.GetDecimal(5));
            Assert.Equal("11427566327/11427541827", rdr.GetString(6));
        });

    // ADMIN_PROJ_08 — BM10002 full-row reference values preserved through the admin path.
    [ProductsPostgresFact]
    public async Task ADMIN_PROJ_08_BM10002_ReferenceValuesPreserved()
        => await Run(async (sqlite, neon, pg) =>
        {
            sqlite.Products.Add(BM10002());
            await sqlite.SaveChangesAsync();
            await FullReplace(sqlite, neon);

            await using var rdr = await new NpgsqlCommand(
                @"SELECT ""ItemCode"",""Price01"",""Price02"",""Price"",""Price04"",""Price05"",""U_OE_Numbers"" FROM ""Products"" WHERE ""ItemCode""='BM10002'", pg).ExecuteReaderAsync();
            Assert.True(await rdr.ReadAsync());
            Assert.Equal(860335m, rdr.GetDecimal(1));
            Assert.Equal(1700000m, rdr.GetDecimal(2));
            Assert.Equal(1700000m, rdr.GetDecimal(3));
            Assert.Equal(1360000m, rdr.GetDecimal(4));
            Assert.Equal(1280167.5m, rdr.GetDecimal(5));
            Assert.True(await rdr.IsDBNullAsync(6));
        });
}
