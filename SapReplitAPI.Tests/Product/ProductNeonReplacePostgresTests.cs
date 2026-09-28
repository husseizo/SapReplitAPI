using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SapReplitAPI.Jobs;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;
using Xunit;

namespace SapReplitAPI.Tests.Product;

/// <summary>
/// Opt-in real-Postgres fact, mirroring Returns.ReturnsPostgresFactAttribute exactly
/// (same env var — this repo has exactly one "point me at a real, non-production
/// Postgres instance for isolated-schema testing" switch; introducing a second name
/// for identical infrastructure would only fragment an existing convention).
/// Skipped by default; every test below owns its own throwaway random schema and
/// drops it afterward. No production database is read or written anywhere here.
/// </summary>
public sealed class ProductsPostgresFactAttribute : FactAttribute
{
    public ProductsPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")))
            Skip = "Set RETURNS_TEST_POSTGRES to a PostgreSQL database permitting isolated test schemas.";
    }
}

/// <summary>
/// PROJ_09–PROJ_12 — Real-Time Neon Foundation, non-blocking full-reconcile
/// correction (TRUNCATE → DELETE in NeonSyncJob.ReplaceProductsAsync).
///
/// PROJ_01–PROJ_08 (ProductNeonProjectionTests.cs) proved row-VALUE correctness via
/// the pure, DB-free BuildProductRowValues function. These four instead prove real
/// PostgreSQL transaction/locking BEHAVIOR — statement identity, same-transaction
/// sequencing, rollback atomicity, single-commit timing — which neither SQLite nor
/// pure-function extraction can exercise, because they are about what Postgres
/// itself does across a DELETE + several batched INSERTs. They run
/// NeonSyncJob.ReplaceProductsAsync (via reflection, exactly as
/// InvoiceReturnsPostgresTests.cs does for ReplaceInvoicesAsync) against a real,
/// isolated, throwaway schema — never production.
/// </summary>
public sealed class ProductNeonReplacePostgresTests
{
    // Every test owns a random schema; no application tables outside it are read or written.
    private static async Task Run(Func<CacheDbContext, NeonDbContext, NpgsqlConnection, Task> test)
    {
        var schema = "products_test_" + Guid.NewGuid().ToString("N");
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

    private static Task ReplaceProducts(CacheDbContext sqlite, NeonDbContext neon)
    {
        var job = new NeonSyncJob(sqlite, neon, new NeonInventoryWriteCoordinator(), null!, null!, NullLogger<NeonSyncJob>.Instance);
        return (Task)typeof(NeonSyncJob).GetMethod("ReplaceProductsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null)!;
    }

    private static CachedProduct Product(string code, decimal p1, decimal p2, decimal p3, decimal p4, decimal p5, string? oe = null) => new()
    {
        ItemCode = code, ItemName = code + " Item", U_Item_Name = "",
        Price01 = p1, Price02 = p2, Price = p3, Price04 = p4, Price05 = p5,
        WhsCode = "ALL", LastUpdated = DateTime.UtcNow, U_OE_Numbers = oe,
    };

    private static async Task<int> ProductCount(NpgsqlConnection pg)
        => Convert.ToInt32(await new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Products""", pg).ExecuteScalarAsync());

    private static async Task<decimal> Price01Of(NpgsqlConnection pg, string code)
        => Convert.ToDecimal(await new NpgsqlCommand(@"SELECT ""Price01"" FROM ""Products"" WHERE ""ItemCode""=@c", pg)
            { Parameters = { new NpgsqlParameter("@c", code) } }.ExecuteScalarAsync());

    // PROJ_09 — full reconcile issues a DELETE statement, never a TRUNCATE, against
    // real Postgres. Proven with statement-level triggers rather than by re-reading
    // source: an AFTER DELETE / AFTER TRUNCATE FOR EACH STATEMENT trigger pair fires
    // synchronously, inside the same transaction, regardless of row count — a
    // direct, deterministic observation of which statement type Postgres actually
    // executed (no reliance on the async stats collector).
    [ProductsPostgresFact]
    public async Task PROJ_09_FullReconcile_UsesDeleteNotTruncate()
        => await Run(async (sqlite, neon, pg) =>
        {
            await new NpgsqlCommand(@"
                CREATE TABLE ""__proj_log"" (kind text NOT NULL);
                CREATE FUNCTION __proj_log_delete() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__proj_log"" VALUES ('DELETE'); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_products_delete AFTER DELETE ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __proj_log_delete();
                CREATE FUNCTION __proj_log_truncate() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__proj_log"" VALUES ('TRUNCATE'); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_products_truncate AFTER TRUNCATE ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __proj_log_truncate();
            ", pg).ExecuteNonQueryAsync();

            sqlite.Products.Add(Product("BM10001", 22187.81m, 35000m, 65000m, 20000m, 41093.91m, "11427566327/11427541827"));
            await sqlite.SaveChangesAsync();

            await ReplaceProducts(sqlite, neon);

            var kinds = new List<string>();
            await using (var rdr = await new NpgsqlCommand(@"SELECT kind FROM ""__proj_log""", pg).ExecuteReaderAsync())
                while (await rdr.ReadAsync()) kinds.Add(rdr.GetString(0));

            Assert.Contains("DELETE", kinds);
            Assert.DoesNotContain("TRUNCATE", kinds);
        });

    // PROJ_10 — the DELETE and every reload batch INSERT execute inside exactly one
    // shared Postgres transaction. Proven by logging txid_current() from statement-
    // level triggers on both DELETE and INSERT: if the clearing statement and the
    // reload were ever split into separate transactions, this would observe two
    // distinct transaction ids instead of one.
    [ProductsPostgresFact]
    public async Task PROJ_10_DeleteAndReload_ExecuteInSameTransaction()
        => await Run(async (sqlite, neon, pg) =>
        {
            await new NpgsqlCommand(@"
                CREATE TABLE ""__proj_txid_log"" (kind text NOT NULL, txid bigint NOT NULL);
                CREATE FUNCTION __proj_log_delete_txid() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__proj_txid_log"" VALUES ('DELETE', txid_current()); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_products_delete_txid AFTER DELETE ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __proj_log_delete_txid();
                CREATE FUNCTION __proj_log_insert_txid() RETURNS trigger AS $$
                    BEGIN INSERT INTO ""__proj_txid_log"" VALUES ('INSERT', txid_current()); RETURN NULL; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER trg_products_insert_txid AFTER INSERT ON ""Products""
                    FOR EACH STATEMENT EXECUTE FUNCTION __proj_log_insert_txid();
            ", pg).ExecuteNonQueryAsync();

            sqlite.Products.Add(Product("BM10001", 22187.81m, 35000m, 65000m, 20000m, 41093.91m, "11427566327/11427541827"));
            sqlite.Products.Add(Product("BM10002", 860335m, 1700000m, 1700000m, 1360000m, 1280167.5m));
            await sqlite.SaveChangesAsync();

            await ReplaceProducts(sqlite, neon);

            var kinds = new List<string>();
            var txids = new List<long>();
            await using (var rdr = await new NpgsqlCommand(@"SELECT kind, txid FROM ""__proj_txid_log""", pg).ExecuteReaderAsync())
                while (await rdr.ReadAsync()) { kinds.Add(rdr.GetString(0)); txids.Add(rdr.GetInt64(1)); }

            Assert.Contains("DELETE", kinds);
            Assert.Contains("INSERT", kinds);
            Assert.Single(txids.Distinct()); // DELETE and every INSERT batch share exactly one transaction id
        });

    // PROJ_11 — a failure during reload rolls back the DELETE too; nothing partially
    // committed. Mirrors InvoiceReturnsPostgresTests.FailedBatchRollsBackTruncate-
    // AndPreservesQuantity's technique (a CHECK constraint the reload violates) —
    // applied to Products' DELETE-based path instead of Invoices' TRUNCATE-based one.
    [ProductsPostgresFact]
    public async Task PROJ_11_FailedReloadBatch_RollsBackDeleteToo_NoPartialCommit()
        => await Run(async (sqlite, neon, pg) =>
        {
            // Baseline: one successful full reconcile establishes existing Neon data.
            sqlite.Products.Add(Product("BM10001", 22187.81m, 35000m, 65000m, 20000m, 41093.91m, "11427566327/11427541827"));
            await sqlite.SaveChangesAsync();
            await ReplaceProducts(sqlite, neon);
            Assert.Equal(1, await ProductCount(pg));
            Assert.Equal(22187.81m, await Price01Of(pg, "BM10001"));

            // Force the NEXT full reconcile's reload phase to fail. If DELETE and the
            // batch reload were ever split into separate transactions, DELETE's
            // effect (an empty table) would remain committed even though the reload
            // failed — this is exactly the bug this correction guards against.
            await new NpgsqlCommand(@"ALTER TABLE ""Products"" ADD CONSTRAINT reject_negative_price01 CHECK (""Price01"" >= 0)", pg).ExecuteNonQueryAsync();
            sqlite.Products.Add(Product("BADROW", -1m, 0m, 0m, 0m, 0m));
            await sqlite.SaveChangesAsync();

            await Assert.ThrowsAsync<PostgresException>(() => ReplaceProducts(sqlite, neon));

            // Old data intact, untouched — DELETE rolled back along with the failed
            // reload, exactly as if the whole operation never ran.
            Assert.Equal(1, await ProductCount(pg));
            Assert.Equal(22187.81m, await Price01Of(pg, "BM10001"));
        });

    // PROJ_12 — commit happens only once, after every batch has completed; not per
    // batch. NeonSyncJob.BatchSize = 500 (confirmed via source at time of writing),
    // so 501 rows force exactly two UpsertProductsBatchAsync calls inside the one
    // ReplaceProductsAsync transaction. Only the very last (highest-Id) row is
    // invalid — ReplaceProductsAsync's unordered ToListAsync() relies on SQLite's
    // natural rowid/insertion order for a plain table scan, so the first 500 valid
    // rows land in batch 1 and the single bad row lands alone in batch 2. Batch 1
    // would succeed if committed on its own; it must not survive if the whole
    // operation is truly one commit after the full loop.
    [ProductsPostgresFact]
    public async Task PROJ_12_CommitOnlyAfterAllBatchesComplete()
        => await Run(async (sqlite, neon, pg) =>
        {
            await new NpgsqlCommand(@"ALTER TABLE ""Products"" ADD CONSTRAINT reject_negative_price01 CHECK (""Price01"" >= 0)", pg).ExecuteNonQueryAsync();

            for (int i = 1; i <= 500; i++)
                sqlite.Products.Add(Product($"P{i:D4}", 1m, 2m, 3m, 4m, 5m));
            sqlite.Products.Add(Product("BADROW", -1m, 0m, 0m, 0m, 0m));
            await sqlite.SaveChangesAsync();

            await Assert.ThrowsAsync<PostgresException>(() => ReplaceProducts(sqlite, neon));

            // Batch 1's 500 valid rows must NOT remain — proves commit did not
            // happen per batch (or via autocommit), only once at the very end.
            Assert.Equal(0, await ProductCount(pg));
        });
}
