using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Quartz;
using System.Net.Http.Json;
using SapReplitAPI.Controllers;
using SapReplitAPI.Extensions;
using SapReplitAPI.Filters;
using SapReplitAPI.Jobs;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Returns;
using Xunit;

namespace SapReplitAPI.Tests.Returns;

/// <summary>
/// Phase 6 TASK 4 — a genuinely minimal isolated host: real Quartz scheduling of the real job
/// classes, and the real SystemController answering real HTTP requests over an in-process
/// TestServer, with NOTHING else from Program.cs registered — no SapService, no invoice sync,
/// no pick-list jobs, no SAP outbox poller. IReturnRequestSapReader is a Fake; NeonDbContext
/// points at an isolated Postgres schema; CacheDbContext points at an in-memory SQLite. This
/// answers "can Quartz + the health endpoint be exercised without the full production app" —
/// yes, demonstrated here — without starting the actual production-configured application.
/// </summary>
public sealed class ReturnReconciliationIsolatedHostTests
{
    [InvoiceReturnsPostgresFactShim]
    public async Task IH01_MinimalHost_QuartzFiresRealJob_HealthEndpointReportsItOverRealHttp()
    {
        var schema = "retrecon_isohost_" + Guid.NewGuid().ToString("N");
        // Each DI scope (the HTTP request scope and the Quartz job's own scope) gets its OWN
        // physical connection from a normal Npgsql pool scoped to this schema — a single shared
        // NpgsqlConnection object handed to many scoped DbContexts caused genuine
        // "already an open DataReader"-class races the moment the job and a health request
        // overlapped, which is not a production design (production never shares one physical
        // connection across concurrent scopes either).
        var neonConnStr = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = schema }.ConnectionString;
        // Admin connection also targets the isolated schema (search_path is resolved per-statement,
        // so setting it before the schema exists is fine — CREATE SCHEMA below makes it resolvable
        // for every DDL/DML statement that follows on this same connection).
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = schema, Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin).ExecuteNonQueryAsync();

        var sqlitePath = Path.Combine(Path.GetTempPath(), "retrecon_isohost_" + Guid.NewGuid().ToString("N") + ".db");
        var sqliteConnStr = $"Data Source={sqlitePath}";

        try
        {
            var reader = new FakeReturnRequestSapReader
            {
                ChangedDocEntries = new List<int> { 900 },
            };
            reader.Snapshots[900] = FakeReturnRequestSapReader.Simple(900);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            // Everything this host needs — and NOTHING else. No SapService, no
            // AddHttpClient-for-SAP, no invoice/pick-list/outbox registrations at all.
            builder.Services.AddControllers().AddApplicationPart(typeof(SystemController).Assembly);
            builder.Services.Configure<ApiSecuritySettings>(o => o.ApiKey = "isolated-host-test-key");
            builder.Services.AddScoped<ApiKeyAuthFilter>();

            builder.Services.Configure<ReturnReconciliationOptions>(o =>
            {
                o.Enabled = true; // this isolated host, not production — proving the mechanism only
                o.BatchSize = 10;
                o.MaxExecutionSeconds = 30;
            });
            builder.Services.AddSingleton(reader);
            builder.Services.AddSingleton<IReturnRequestSapReader>(sp => sp.GetRequiredService<FakeReturnRequestSapReader>());
            builder.Services.AddScoped(_ => new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(sqliteConnStr).Options));
            builder.Services.AddScoped(_ => new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(neonConnStr).Options));
            builder.Services.AddScoped<ReturnRequestCacheService>();
            builder.Services.AddScoped<NeonReturnRequestWriteService>();
            builder.Services.AddScoped<ReturnRequestReconciliationService>();
            builder.Services.AddScoped<ReturnRequestReconciliationJob>();

            builder.Services.AddQuartz(q =>
            {
                // Every 2 seconds so the test doesn't need to wait long to observe a real fire.
                q.AddCronJobAndTrigger<ReturnRequestReconciliationJob>("ReturnRequestReconciliationJob", "0/2 * * * * ?");
            });
            builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

            var app = builder.Build();

            // Prepare SQLite's cursor table and Neon's schema — in the real app Program.cs does
            // this at startup; this minimal host does the same two steps up front.
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var sqlite = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
                await sqlite.Database.EnsureCreatedAsync();
                await sqlite.Database.ExecuteSqlRawAsync(ReturnReconciliationCursorPersistenceTests.CursorTableDdl);
            }
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var neon = scope.ServiceProvider.GetRequiredService<NeonDbContext>();
                await new NpgsqlCommand(neon.Database.GenerateCreateScript(), admin).ExecuteNonQueryAsync();
            }

            app.MapControllers();

            await app.StartAsync();
            try
            {
                var client = app.GetTestClient();
                client.DefaultRequestHeaders.Add("X-API-Key", "isolated-host-test-key");

                // The health endpoint (real HTTP, real controller, real DI) reports the feature
                // as enabled from the very first request. Not asserting "NeverRun" here — under
                // a loaded test run (e.g. the full suite, where this process is one of hundreds
                // of concurrently scheduled tests) enough wall-clock time can elapse between
                // app.StartAsync() and this first request that Quartz's 2-second cron trigger
                // has already fired once; that race is immaterial to what this test exists to
                // prove (see the "after" assertions below) and asserting it would make the test
                // flaky under load for no real benefit.
                var before = await client.GetFromJsonAsync<ReturnReconciliationHealthResponse>("/api/system/return-reconciliation-health");
                Assert.NotNull(before);
                Assert.True(before!.Enabled);

                // Wait for Quartz to actually fire the real job on its real schedule — polling
                // the health endpoint itself (not inspecting internals) rather than a fixed sleep.
                ReturnReconciliationHealthResponse? after = null;
                for (int i = 0; i < 30 && (after is null || after.LastRunAtUtc is null); i++)
                {
                    await Task.Delay(500);
                    after = await client.GetFromJsonAsync<ReturnReconciliationHealthResponse>("/api/system/return-reconciliation-health");
                }

                Assert.NotNull(after);
                Assert.NotNull(after!.LastRunAtUtc); // Quartz genuinely fired ReturnRequestReconciliationJob
                Assert.Equal(1, after.LastRunRefreshed); // and it processed the one fake candidate
                Assert.Equal("Healthy", after.Status);
                Assert.Contains(900, reader.SnapshotCallLog); // confirms the REAL job called the REAL service
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", admin).ExecuteNonQueryAsync();
            try { File.Delete(sqlitePath); } catch { /* best-effort cleanup */ }
        }
    }
}
