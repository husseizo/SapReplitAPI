using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using SapReplitAPI.Extensions; // Required for AddJobAndTrigger
using SapReplitAPI.Jobs;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Queue;
using Serilog;
using System.Runtime.Versioning;
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
    builder.Services.AddSwaggerGen();

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

    builder.Services.AddScoped<SapService>();
    builder.Services.AddScoped<ProductCacheService>();
    builder.Services.AddScoped<InvoiceCacheService>();
    builder.Services.AddScoped<CustomerCacheService>();
    builder.Services.AddScoped<OrderCacheService>();
    builder.Services.AddScoped<DashboardService>();
    builder.Services.AddScoped<UserCacheService>();
    builder.Services.AddScoped<TodayOrderCacheService>(); // ✅ Add this for SyncTodayOrdersJob
    builder.Services.AddScoped<OpenOrderCacheService>(); // 🆕 Required

    // SAP Company instance registration (singleton).
    // Credentials come from IOptions<SapSettings> which is validated above at startup.
    builder.Services.AddSingleton<SAPbobsCOM.Company>(sp =>
    {
        var cfg = sp.GetRequiredService<IOptions<SapSettings>>().Value;

        var c = new SAPbobsCOM.Company
        {
            Server = cfg.Server,
            CompanyDB = cfg.CompanyDB,
            UserName = cfg.UserName,
            Password = cfg.Password,
            DbServerType = SAPbobsCOM.BoDataServerTypes.dst_MSSQL2016,
            language = SAPbobsCOM.BoSuppLangs.ln_English,
            UseTrusted = false,
            LicenseServer = cfg.LicenseServer, // host:30000
            SLDServer = cfg.SLDServer      // host:40000  <-- REQUIRED to avoid SLD error
        };

        // ❌ DO NOT call c.Connect() here
        return c;
    });

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
        q.UseMicrosoftDependencyInjectionJobFactory(); // ✔️ Compatible with scoped services

        q.AddJobAndTrigger<ProductDeltaSyncJob>("ProductDeltaSyncJob", TimeSpan.FromMinutes(70));
        q.AddJobAndTrigger<CustomerFullSyncJob>("CustomerFullSyncJob", TimeSpan.FromMinutes(6));
        q.AddJobAndTrigger<OrderFullSyncJob>("OrderFullSyncJob", TimeSpan.FromHours(5));
        q.AddJobAndTrigger<OrderDeltaSyncJob>("OrderDeltaSyncJob", TimeSpan.FromMinutes(5));
        q.AddJobAndTrigger<InvoiceFullSyncJob>("InvoiceFullSyncJob", TimeSpan.FromHours(12));
        q.AddJobAndTrigger<InvoiceDeltaSyncJob>("InvoiceDeltaSyncJob", TimeSpan.FromMinutes(8));
        q.AddJobAndTrigger<SyncTodayOrdersJob>("SyncTodayOrdersJob", TimeSpan.FromMinutes(3));
        q.AddJobAndTrigger<SyncOpenOrdersJob>("SyncOpenOrdersJob", TimeSpan.FromMinutes(5));

        // Neon mirror — runs every 5 min, only registered if connection string present
        if (!string.IsNullOrWhiteSpace(neonCs))
            q.AddJobAndTrigger<NeonSyncJob>("NeonSyncJob", TimeSpan.FromMinutes(5));

        q.AddJob<ProductFullSyncJob>(opts => opts
            .WithIdentity("ProductFullSyncJob")
            .StoreDurably());
        q.ScheduleJob<ProductFullSyncJob>(trigger => trigger
            .WithIdentity("product-full-sync-job")
            .WithCronSchedule("0 0 2 * * ?")); // 2:00 AM every day

        q.ScheduleJob<InvoiceStatusCacheJob>(trigger => trigger
            .WithIdentity("invoice-status-cache-job")
            .WithCronSchedule("0 0 5 * * ?")); // 5:00 AM daily

        q.ScheduleJob<CacheInvoiceStatusJob>(trigger => trigger
            .WithIdentity("cache-invoice-status-job")
            .WithCronSchedule("0 0 5 * * ?")); // 5:00 AM daily
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
    builder.Services.AddScoped<CacheInvoiceStatusJob>();
    builder.Services.AddScoped<SyncTodayOrdersJob>();
    builder.Services.AddScoped<SyncOpenOrdersJob>(); // 🆕 Add this line
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

                // Enable WAL mode on Windows
                if (OperatingSystem.IsWindows())
                {
                    SetPragmaWal(db);
                }

                db.Database.Migrate();
                logger.LogInformation("📦 SQLite schema migration complete.");

                // Neon: auto-create schema on first run (no migrations needed for the mirror)
                if (!string.IsNullOrWhiteSpace(neonCs))
                {
                    try
                    {
                        var neonDb = scope.ServiceProvider.GetRequiredService<NeonDbContext>();
                        await neonDb.Database.EnsureCreatedAsync();
                        logger.LogInformation("☁️ Neon schema ready.");
                    }
                    catch (Exception neonEx)
                    {
                        logger.LogWarning(neonEx, "⚠️ Neon schema init failed — mirror disabled until next restart.");
                    }
                }

                var requiredTypes = new[] { "Invoice", "Order", "Product", "TodayOrder" };
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