using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SapReplitAPI.Jobs;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Events;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Returns;
using Xunit;

namespace SapReplitAPI.Tests.Returns;

public sealed class ReturnsPostgresFactAttribute : FactAttribute
{
    public ReturnsPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")))
            Skip = "Set RETURNS_TEST_POSTGRES to a PostgreSQL database permitting isolated test schemas.";
    }
}

public sealed class InvoiceReturnsPostgresTests
{
    // Every test owns a random schema; no application tables are read or written.
    private static async Task Run(Func<CacheDbContext, NeonDbContext, NpgsqlConnection, Task> test)
    {
        var schema = "returns_test_" + Guid.NewGuid().ToString("N");
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
            sqlite.Invoices.Add(new CachedInvoice { DocEntry = 28571, DocDate = new DateTime(2026, 9, 1) });
            sqlite.InvoiceLines.Add(new CachedInvoiceLine { DocEntry = 28571, LineNum = 0, Quantity = 3 });
            await sqlite.SaveChangesAsync();
            await test(sqlite, neon, pg);
        }
        finally { await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", pg).ExecuteNonQueryAsync(); }
    }

    private static Task Reconcile(CacheDbContext sqlite, NeonDbContext neon, bool full = true)
    {
        var job = new NeonSyncJob(sqlite, neon, null!, null!, null!, NullLogger<NeonSyncJob>.Instance);
        return (Task)typeof(NeonSyncJob).GetMethod(full ? "ReplaceInvoicesAsync" : "SyncInvoicesIncrementalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(job, null)!;
    }
    private static async Task<decimal> Quantity(NpgsqlConnection pg)
        => Convert.ToDecimal(await new NpgsqlCommand("SELECT \"ReturnedQty\" FROM \"InvoiceLines\" WHERE \"DocEntry\"=28571 AND \"LineNum\"=0", pg).ExecuteScalarAsync());

    [ReturnsPostgresFact] public async Task RQ01_PostgresFullAndIncrementalPreserveDestinationAgainstStaleSqlite()
        => await Run(async (sqlite, neon, pg) =>
        {
            await Reconcile(sqlite, neon);
            await new NpgsqlCommand("UPDATE \"InvoiceLines\" SET \"ReturnedQty\"=3", pg).ExecuteNonQueryAsync();
            await Reconcile(sqlite, neon); Assert.Equal(3, await Quantity(pg));
            await Reconcile(sqlite, neon, false); Assert.Equal(3, await Quantity(pg));
        });

    private static async Task CreditScenario(int path, string canceled, decimal expected)
        => await Run(async (sqlite, neon, pg) =>
        {
            await Reconcile(sqlite, neon);
            var writer = new NeonCreditMemoWriteService(neon, NullLogger<NeonCreditMemoWriteService>.Instance);
            await writer.UpsertCreditMemoAsync(new CachedCreditMemo { DocEntry = 81, Canceled = canceled, DocDate = new DateTime(2026, 9, 1), DocDueDate = new DateTime(2026, 9, 1), CreateDate = new DateTime(2026, 9, 1), UpdateDate = new DateTime(2026, 9, 1) },
                new[] { new CachedCreditMemoLine { DocEntry = 81, LineNum = 0, InvoiceDocEntry = 28571, InvoiceLineNum = 0, BaseType = path, Quantity = 3 } });
            await Reconcile(sqlite, neon); Assert.Equal(expected, await Quantity(pg));
        });
    [ReturnsPostgresFact] public Task RQ02_PostgresCanceledCreditExcluded() => CreditScenario(13, "Y", 0);
    [ReturnsPostgresFact] public Task RQ03_PostgresPathAIncluded() => CreditScenario(13, "N", 3);
    [ReturnsPostgresFact] public Task RQ04_PostgresPathBIncluded() => CreditScenario(234000031, "N", 3);
    [ReturnsPostgresFact] public async Task RQ05_PostgresNoCreditIsZero()
        => await Run(async (sqlite, neon, pg) => { await Reconcile(sqlite, neon); Assert.Equal(0, await Quantity(pg)); });
    [ReturnsPostgresFact] public async Task NewNeonLineCarriesSqliteBackfilledQuantity()
        => await Run(async (sqlite, neon, pg) =>
        { sqlite.InvoiceLines.Local.Single().ReturnedQty = 3; await sqlite.SaveChangesAsync(); await Reconcile(sqlite, neon); Assert.Equal(3, await Quantity(pg)); });
    [ReturnsPostgresFact] public async Task FailedBatchRollsBackTruncateAndPreservesQuantity()
        => await Run(async (sqlite, neon, pg) =>
        {
            await Reconcile(sqlite, neon); await new NpgsqlCommand("UPDATE \"InvoiceLines\" SET \"ReturnedQty\"=3; ALTER TABLE \"InvoiceLines\" ADD CONSTRAINT reject_bad CHECK (\"Quantity\" >= 0)", pg).ExecuteNonQueryAsync();
            sqlite.InvoiceLines.Local.Single().Quantity = -1; await sqlite.SaveChangesAsync();
            await Assert.ThrowsAsync<PostgresException>(() => Reconcile(sqlite, neon)); Assert.Equal(3, await Quantity(pg));
        });
    [ReturnsPostgresFact] public async Task NeonInvoiceEventPreservesReturnedQty()
        => await Run(async (sqlite, neon, pg) =>
        {
            await Reconcile(sqlite, neon); await new NpgsqlCommand("UPDATE \"InvoiceLines\" SET \"ReturnedQty\"=3", pg).ExecuteNonQueryAsync();
            var writer = new NeonEventWriteService(neon, NullLogger<NeonEventWriteService>.Instance);
            await writer.UpsertInvoiceAsync(sqlite.Invoices.Local.Single(), new[] { new CachedInvoiceLine { DocEntry = 28571, LineNum = 0, Quantity = 3 } }); Assert.Equal(3, await Quantity(pg));
        });
    [ReturnsPostgresFact] public async Task NeonBackfillIsIdempotent()
        => await Run(async (sqlite, neon, pg) =>
        {
            await Reconcile(sqlite, neon);
            var first = await ReturnedQtyBackfillService.RecomputeAsync(pg); var second = await ReturnedQtyBackfillService.RecomputeAsync(pg);
            Assert.Equal(1, first.InvoiceLinesScanned); Assert.Equal(0, second.InvoiceLinesChanged);
        });
}
