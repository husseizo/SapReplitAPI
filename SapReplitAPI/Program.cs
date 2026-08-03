using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Quartz;
using SapReplitAPI.Extensions; // Required for AddJobAndTrigger
using SapReplitAPI.Jobs;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Queue;
using Serilog;
using System.Runtime.Versioning;
using System.Text;
using SAPbobsCOM; // Required for SAP Company object

var logPath = @"C:\SAPLogs\app.log";

// Logger Configuration
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(Serilog.Events.LogEventLevel.Debug)
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .CreateLogger();

try
{
    Log.Information("🚀 Starting SAP Replit API host...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    if (OperatingSystem.IsWindows())
    {
        EnableWindowsService(builder.Host);
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(5050);
    });

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Name        = "X-API-Key",
            In          = ParameterLocation.Header,
            Type        = SecuritySchemeType.ApiKey,
            Description = "Enter your API key in the field below."
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id   = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
    });

    // Dependency Injection Config
    // Validate SAP credentials at startup — fail fast with a clear message instead of
    // getting an opaque SAP auth error on the first job/request execution.
    // Credentials must be set via environment variables SAP__UserName and SAP__Password.
    // Do NOT store credentials in appsettings.json.
    builder.Services.AddOptions<SapSettings>()
        .BindConfiguration("SAP")
        .Validate(s =>
            !string.IsNullOrWhiteSpace(s.UserName) && !string.IsNullOrWhiteSpace(s.Password),
            "SAP credentials are missing. Set env vars SAP__UserName and SAP__Password on the host machine.")
        .ValidateOnStart(); // Fail immediately at startup, not on first SAP call

    builder.Services.AddOptions<PaymentSettings>()
        .BindConfiguration("Payments")
        .ValidateOnStart();

    builder.Services.AddOptions<ApiSecuritySettings>()
        .BindConfiguration("ApiSecurity")
        .ValidateOnStart();

    builder.Services.AddScoped<SapService>();
    builder.Services.AddScoped<SapProductService>();
    builder.Services.AddScoped<SapCustomerService>();
    builder.Services.AddScoped<SapInvoiceService>();
    builder.Services.AddScoped<InvoiceLifecycleStatusService>();
    builder.Services.AddScoped<ProductCacheService>();
    builder.Services.AddScoped<InvoiceCacheService>();
    builder.Services.AddScoped<CustomerCacheService>();
    builder.Services.AddScoped<OrderCacheService>();
    builder.Services.AddScoped<DashboardService>();
    builder.Services.AddScoped<UserCacheService>();
    builder.Services.AddScoped<TodayOrderCacheService>(); // ✅ Add this for SyncTodayOrdersJob
    builder.Services.AddScoped<OpenOrderCacheService>(); // 🆕 Required


    // Background task queue
    builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
    builder.Services.AddHostedService<QueuedHostedService>();

    builder.Services.AddDbContext<CacheDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("CacheDB")));

    // Neon (PostgreSQL) mirror — scoped so each job run gets its own connection.
    // If NeonDb connection string is absent the app still starts; the job just logs a warning.
    var neonCs = builder.Configuration.GetConnectionString("NeonDb");
    if (!string.IsNullOrWhiteSpace(neonCs))
    {
        builder.Services.AddDbContext<NeonDbContext>(options =>
            options.UseNpgsql(neonCs));
    }
    else
    {
        Log.Warning("⚠️ NeonDb connection string not found — Neon mirror is disabled.");
    }

    // Quartz Jobs Configuration
    Log.Information("📅 Configuring Quartz jobs...");

    builder.Services.AddQuartz(q =>
    {
        // UseMicrosoftDependencyInjectionJobFactory is now the default — no call needed

        // Frequent cache freshness jobs — use cron instead of startup-relative intervals
        q.AddCronJobAndTrigger<InvoiceDeltaSyncJob>("InvoiceDeltaSyncJob", "0 0/5 * * * ?");
        q.AddCronJobAndTrigger<OrderDeltaSyncJob>("OrderDeltaSyncJob", "0 2/5 * * * ?");
        q.AddCronJobAndTrigger<SyncTodayOrdersJob>("SyncTodayOrdersJob", "0 1/3 6-19 * * ?");
        q.AddCronJobAndTrigger<SyncOpenOrdersJob>("SyncOpenOrdersJob", "0 4/10 * * * ?");
        q.AddCronJobAndTrigger<ProductDeltaSyncJob>("ProductDeltaSyncJob", "0 3/15 * * * ?");

        // Reconciliation / cleanup jobs
        q.AddCronJobAndTrigger<CustomerFullSyncJob>("CustomerFullSyncJob", "0 0 1 * * ?");
        q.AddCronJobAndTrigger<ProductFullSyncJob>("ProductFullSyncJob", "0 0 2 * * ?");
        q.AddCronJobAndTrigger<OrderFullSyncJob>("OrderFullSyncJob", "0 0 3 * * ?");
        q.AddCronJobAndTrigger<InvoiceFullSyncJob>("InvoiceFullSyncJob", "0 0 4 * * ?");
        q.AddCronJobAndTrigger<InvoiceStatusCacheJob>("InvoiceStatusCacheJob", "0 15 5 * * ?");

        // GL account statements: SAP → SQLite (always, no Neon dependency)
        q.AddCronJobAndTrigger<AccountStatementSyncJob>("AccountStatementSyncJob", "0 0/30 * * * ?");

        // Neon mirror — offset after upstream cache jobs and only registered if connection string present
        if (!string.IsNullOrWhiteSpace(neonCs))
            q.AddCronJobAndTrigger<NeonSyncJob>("NeonSyncJob", "0 9/10 * * * ?");
    });

    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    // Register jobs for DI
    builder.Services.AddScoped<ProductFullSyncJob>();
    builder.Services.AddScoped<ProductDeltaSyncJob>();
    builder.Services.AddScoped<CustomerFullSyncJob>();
    builder.Services.AddScoped<OrderFullSyncJob>();
    builder.Services.AddScoped<OrderDeltaSyncJob>();
    builder.Services.AddScoped<InvoiceFullSyncJob>();
    builder.Services.AddScoped<InvoiceDeltaSyncJob>();
    builder.Services.AddScoped<InvoiceStatusCacheJob>();
    builder.Services.AddScoped<SyncTodayOrdersJob>();
    builder.Services.AddScoped<SyncOpenOrdersJob>();
    builder.Services.AddScoped<AccountStatementSyncJob>(); // always registered — writes to SQLite, not Neon
    if (!string.IsNullOrWhiteSpace(neonCs))
        builder.Services.AddScoped<NeonSyncJob>();

    var app = builder.Build();

    if (!args.Contains("--ef"))
    {
        Log.Information("🗃️ Initializing database...");
        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();

                // Ensure the SQLite DB directory exists — without this, SQLite silently
                // falls back to in-memory mode when the directory is missing, causing
                // "no such table" errors after each connection is closed.
                {
                    var cs = app.Configuration.GetConnectionString("CacheDB") ?? "";
                    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs);
                    var dir = Path.GetDirectoryName(csb.DataSource);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                }

                // Enable WAL mode on Windows
                if (OperatingSystem.IsWindows())
                {
                    SetPragmaWal(db);
                }

                db.Database.Migrate();
                logger.LogInformation("📦 SQLite schema migration complete.");

                // Guarantee the unique index on Products.ItemCode exists regardless of
                // migration discovery — required for ON CONFLICT(ItemCode) UPSERT syntax.
                db.Database.ExecuteSqlRaw(@"
DELETE FROM Products WHERE Id NOT IN (
    SELECT MAX(Id) FROM Products GROUP BY ItemCode
)");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Products_ItemCode"" ON ""Products"" (""ItemCode"")");
                logger.LogInformation("✅ Products.ItemCode unique index ensured.");

                // Drop the stray Id column from OrderHeaders only if it still exists.
                // Check first via pragma_table_info to avoid EF logging a spurious Error for a no-op DROP.
                {
                    var conn = db.Database.GetDbConnection();
                    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                    using var chk = conn.CreateCommand();
                    chk.CommandText = "SELECT COUNT(*) FROM pragma_table_info('OrderHeaders') WHERE name='Id'";
                    var hasId = (long)chk.ExecuteScalar()! > 0;
                    if (hasId)
                    {
                        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""OrderHeaders"" DROP COLUMN ""Id""");
                        logger.LogInformation("✅ Dropped stray OrderHeaders.Id column.");
                    }
                }

                // Customers.CardCode — required for ON CONFLICT(CardCode) UPSERT
                db.Database.ExecuteSqlRaw(@"
DELETE FROM Customers WHERE Id NOT IN (
    SELECT MAX(Id) FROM Customers GROUP BY CardCode
)");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_CardCode"" ON ""Customers"" (""CardCode"")");
                logger.LogInformation("✅ Customers.CardCode unique index ensured.");

                // OrderLines.(DocEntry, LineNum) — required for ON CONFLICT(DocEntry, LineNum) UPSERT
                db.Database.ExecuteSqlRaw(@"
DELETE FROM OrderLines WHERE Id NOT IN (
    SELECT MAX(Id) FROM OrderLines GROUP BY DocEntry, LineNum
)");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_OrderLines_DocEntry_LineNum"" ON ""OrderLines"" (""DocEntry"", ""LineNum"")");
                logger.LogInformation("✅ OrderLines.(DocEntry,LineNum) unique index ensured.");

                // InvoicePayments.(DocEntry,PaymentDocEntry) — one payment can apply to multiple invoices.
                db.Database.ExecuteSqlRaw(@"DROP INDEX IF EXISTS ""IX_InvoicePayments_PaymentDocEntry""");
                db.Database.ExecuteSqlRaw(@"
DELETE FROM InvoicePayments
WHERE Id NOT IN (
    SELECT MAX(Id) FROM InvoicePayments GROUP BY DocEntry, PaymentDocEntry
)");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_InvoicePayments_DocEntry_PaymentDocEntry"" ON ""InvoicePayments"" (""DocEntry"", ""PaymentDocEntry"")");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_InvoicePayments_PaymentDocEntry_Lookup"" ON ""InvoicePayments"" (""PaymentDocEntry"")");
                logger.LogInformation("✅ InvoicePayments.(DocEntry,PaymentDocEntry) unique index ensured.");

                // AccountStatements — created here because migrations don't cover manual additions;
                // safe to run on every startup (IF NOT EXISTS).
                db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""AccountStatements"" (
    ""Id""              INTEGER PRIMARY KEY AUTOINCREMENT,
    ""TransId""         INTEGER NOT NULL,
    ""Account""         TEXT    NOT NULL,
    ""AccountName""     TEXT    NOT NULL DEFAULT '',
    ""RefDate""         TEXT    NOT NULL,
    ""Debit""           REAL    NOT NULL DEFAULT 0,
    ""Credit""          REAL    NOT NULL DEFAULT 0,
    ""LineMemo""        TEXT    NOT NULL DEFAULT '',
    ""TransType""       TEXT    NOT NULL DEFAULT '',
    ""Ref1""            TEXT    NOT NULL DEFAULT '',
    ""Ref2""            TEXT    NOT NULL DEFAULT '',
    ""PaymentDocEntry"" INTEGER,
    ""PaymentDocNum""   INTEGER,
    ""InvoiceDocEntry"" INTEGER,
    ""InvoiceDocNum""   INTEGER,
    ""CardCode""        TEXT    NOT NULL DEFAULT '',
    ""CardName""        TEXT    NOT NULL DEFAULT ''
)");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AccountStatements_TransId_Account"" ON ""AccountStatements"" (""TransId"", ""Account"")");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_AccountStatements_Account_RefDate"" ON ""AccountStatements"" (""Account"", ""RefDate"" DESC)");
                logger.LogInformation("✅ AccountStatements table and indexes ensured.");

                db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""PaymentIdempotencyLogs"" (
    ""Id""              INTEGER PRIMARY KEY AUTOINCREMENT,
    ""ClientReference"" TEXT    NOT NULL UNIQUE,
    ""PaymentDocEntry"" INTEGER NOT NULL,
    ""PaymentDocNum""   INTEGER NOT NULL,
    ""CreatedAt""       TEXT    NOT NULL
)");
                logger.LogInformation("✅ PaymentIdempotencyLogs table ensured.");

                // Neon: auto-create schema on first run (no migrations needed for the mirror)
                if (!string.IsNullOrWhiteSpace(neonCs))
                {
                    try
                    {
                        var neonDb = scope.ServiceProvider.GetRequiredService<NeonDbContext>();
                        await neonDb.Database.EnsureCreatedAsync();

                        // EnsureCreated is a no-op on an existing DB — explicitly create the
                        // AccountStatements table so it exists even when the DB was already
                        // initialized before this table was added.
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""AccountStatements"" (
    ""Id""              SERIAL PRIMARY KEY,
    ""TransId""         integer      NOT NULL,
    ""Account""         text         NOT NULL,
    ""AccountName""     text         NOT NULL DEFAULT '',
    ""RefDate""         timestamptz  NOT NULL,
    ""Debit""           numeric(18,2) NOT NULL DEFAULT 0,
    ""Credit""          numeric(18,2) NOT NULL DEFAULT 0,
    ""LineMemo""        text         NOT NULL DEFAULT '',
    ""TransType""       text         NOT NULL DEFAULT '',
    ""Ref1""            text         NOT NULL DEFAULT '',
    ""Ref2""            text         NOT NULL DEFAULT '',
    ""PaymentDocEntry"" integer,
    ""PaymentDocNum""   integer,
    ""InvoiceDocEntry"" integer,
    ""InvoiceDocNum""   integer,
    ""CardCode""        text         NOT NULL DEFAULT '',
    ""CardName""        text         NOT NULL DEFAULT '',
    UNIQUE (""TransId"", ""Account"")
);
CREATE INDEX IF NOT EXISTS ""IX_AccountStatements_Account_RefDate""
    ON ""AccountStatements"" (""Account"", ""RefDate"" DESC);
CREATE INDEX IF NOT EXISTS ""IX_AccountStatements_PaymentDocEntry""
    ON ""AccountStatements"" (""PaymentDocEntry"") WHERE ""PaymentDocEntry"" IS NOT NULL;");

                        logger.LogInformation("☁️ Neon schema ready (AccountStatements table ensured).");
                    }
                    catch (Exception neonEx)
                    {
                        logger.LogWarning(neonEx, "⚠️ Neon schema init failed — mirror disabled until next restart.");
                    }
                }

                var requiredTypes = new[]
                {
                    "Invoice",
                    "Order",
                    "Product",
                    "TodayOrder",
                    "Customer",
                    "OpenOrder",
                    "InvoiceStatusCache"
                };
                var existingTypes = db.SyncMetadata.Select(m => m.Type).ToList();

                foreach (var type in requiredTypes)
                {
                    if (!existingTypes.Contains(type))
                    {
                        db.SyncMetadata.Add(new SyncMetadata
                        {
                            Type = type,
                            LastSyncedAt = new DateTime(2024, 1, 1)
                        });
                        logger.LogInformation("✅ Seeded SyncMetadata: {Type}", type);
                    }
                }

                await db.SaveChangesAsync();
                logger.LogInformation("🧠 SyncMetadata initialization complete.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Database initialization failed.");
                throw;
            }
        }

        Log.Information("⚙️ Starting middleware...");

        // ── Startup diagnostic — confirm security config loaded ───────────────
        {
            var sc = app.Services.GetRequiredService<IOptions<ApiSecuritySettings>>().Value;
            Log.Information("🔐 Security config — ApiKey set: {ApiKeySet}, SwaggerPassword set: {PwdSet}",
                !string.IsNullOrWhiteSpace(sc.ApiKey),
                !string.IsNullOrWhiteSpace(sc.SwaggerPassword));
        }

        // ── Security: must be first in the pipeline ───────────────────────────
        app.Use(async (context, next) =>
        {
            var cfg = context.RequestServices
                .GetRequiredService<IOptions<ApiSecuritySettings>>().Value;

            var path = context.Request.Path.Value ?? "";

            // Swagger UI — HTTP Basic Auth (any username, password checked)
            if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(cfg.SwaggerPassword))
                {
                    var authorized = false;
                    var authHeader = context.Request.Headers["Authorization"].ToString();
                    if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var decoded  = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader[6..].Trim()));
                            var colon    = decoded.IndexOf(':');
                            var supplied = colon >= 0 ? decoded[(colon + 1)..] : decoded;
                            authorized   = supplied == cfg.SwaggerPassword;
                        }
                        catch { /* invalid base64 — leave authorized = false */ }
                    }
                    if (!authorized)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"SAP API Docs\"";
                        await context.Response.WriteAsync("Unauthorized");
                        return;
                    }
                }
            }
            // Payment endpoint — always requires X-API-Key (accounts app caller).
            // Other /api/* routes are left open until the sales app is updated to send the key.
            else if (path.StartsWith("/api/payments/incoming", StringComparison.OrdinalIgnoreCase)
                     && context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                {
                    Log.Warning("⚠️ POST /api/payments/incoming — ApiKey not configured, request allowed through");
                }
                else
                {
                    var supplied = context.Request.Headers["X-API-Key"].ToString();
                    if (supplied != cfg.ApiKey)
                    {
                        context.Response.StatusCode  = 401;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(
                            "{\"message\":\"Missing or invalid API key. Provide it in the X-API-Key header.\"}");
                        return;
                    }
                }
            }

            await next(context);
        });

        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseCors("AllowAll");
        app.UseAuthorization();
        app.MapControllers();

        var hostIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
    .AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        Log.Information("✅ SAP Replit API reachable at http://{ip}:7121", hostIp);
        app.Run();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

//
// 🪟 Windows-only helper methods
//

[SupportedOSPlatform("windows")]
static void EnableWindowsService(IHostBuilder host)
{
    host.UseWindowsService();
}

[SupportedOSPlatform("windows")]
static void SetPragmaWal(CacheDbContext db)
{
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}
