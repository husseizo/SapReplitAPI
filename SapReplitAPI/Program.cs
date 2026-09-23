using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Quartz;
using SapReplitAPI.Extensions; // Required for AddJobAndTrigger
using SapReplitAPI.Jobs;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Events;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Queue;
using SapReplitAPI.Services.SoDelivery;
using SapReplitAPI.Services.Inventory;
using QuestPDF.Infrastructure;
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

    // QuestPDF license — set once at process startup before any PDF is generated.
    // Community license is free for organizations with annual gross revenue below USD $1M.
    // Upgrade to LicenseType.Professional or LicenseType.Enterprise above that threshold.
    QuestPDF.Settings.License = LicenseType.Community;

    var builder = WebApplication.CreateBuilder(args.Where(a => a != "--backfill-returned-qty" && a != "--verify-invoice-returns" && a != "--backfill-returns-mirror").ToArray());
    builder.Host.UseSerilog();

    if (OperatingSystem.IsWindows())
    {
        EnableWindowsService(builder.Host);
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(5050);
    });

    builder.Services.AddScoped<SapReplitAPI.Filters.ApiKeyAuthFilter>();
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
    builder.Services.AddScoped<PendingOrderService>();
    builder.Services.AddScoped<SapReplitAPI.Services.Neon.NeonProductSyncService>();
    builder.Services.AddScoped<SapReplitAPI.Services.Product.ProductPriceListSyncService>();

    // Warehouse inventory sync services
    builder.Services.AddScoped<SapWarehouseInventoryService>();
    builder.Services.AddScoped<WarehouseInventorySyncService>();

    // Bin inventory sync services
    builder.Services.AddScoped<SapBinInventoryService>();
    builder.Services.AddScoped<BinInventorySyncService>();

    // SO → Delivery automation services
    builder.Services.AddScoped<SoDeliveryDbService>();
    builder.Services.AddScoped<SoDeliveryReportService>();
    builder.Services.AddScoped<SoDeliveryService>();


    // Phase 2 inventory write coordinators — singletons, shared across all services + event handlers.
    // Lock discipline: InventoryCacheWriteCoordinator → release → NeonInventoryWriteCoordinator → release.
    // NEVER hold both simultaneously (deadlock risk).
    builder.Services.AddSingleton<InventoryCacheWriteCoordinator>();
    builder.Services.AddSingleton<NeonInventoryWriteCoordinator>();

    // Zone Fulfillment — experimental (Phase C)
    builder.Services.Configure<SapReplitAPI.Models.ZoneFulfillment.ZoneFulfillmentOptions>(
        builder.Configuration.GetSection(SapReplitAPI.Models.ZoneFulfillment.ZoneFulfillmentOptions.Section));
    builder.Services.AddSingleton<SapReplitAPI.Services.ZoneFulfillment.OrderAllocationCoordinator>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.PayloadHashService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZfReconciliationRepo,
                               SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentRepository>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZfPriorityRepo,
                               SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentRepository>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentRepository>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneAllocationEngine>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.TieredZoneAllocationEngine>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.TieredWarehousePriorityResolver>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZfAllocationPolicy>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.SapOitwAdapter>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentSapOrderService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentOrchestrationService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZfWhsChangeSapReader,
                               SapReplitAPI.Services.ZoneFulfillment.ZfSapWhsChangeSapReader>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZoneFulfillmentWarehouseChangeService,
                               SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentWarehouseChangeService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZoneFulfillmentOrderEditCoordinator,
                               SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentOrderEditCoordinator>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentPickListService>();
    builder.Services.AddSingleton<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentDeliveryCoordinator>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentDeliveryService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentInvoiceService>();
    builder.Services.AddSingleton<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentInvoiceStartupHealth>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.PickerResolutionService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZfAutomation,
                               SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentAutomationService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentAutomationService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentReconciliationService>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.IZfPickSapReader,
                               SapReplitAPI.Services.ZoneFulfillment.SapZfPickAdapter>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentPickReconciliationService>();

    // ZF Operations Console — Phase 1A (read-only diagnostic) + Phase 1B (controlled recovery)
    builder.Services.AddOptions<SapReplitAPI.Models.ZfAdminSettings>()
        .BindConfiguration(SapReplitAPI.Models.ZfAdminSettings.Section);
    builder.Services.AddScoped<SapReplitAPI.Filters.ZfAdminKeyAuthFilter>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZfAdminDiagnosticService>();
    builder.Services.AddSingleton<SapReplitAPI.Services.ZoneFulfillment.ZfAdminAuditRepository>();
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZfAdminActionService>();
    // Phase 4: Diagnosis Console — incident resolution history (MolasIntegration SQL Server, append-only)
    builder.Services.AddSingleton<SapReplitAPI.Services.ZoneFulfillment.ZfIncidentResolutionRepository>();
    // Phase 2: Dashboard — live cross-db query + resolution enrichment
    builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZfDashboardService>();

    // Product Price Administration — Phase 1
    // Audit repository is Singleton (creates its own SqlConnection per method; never holds one open).
    // SapPriceAdapter is Scoped (wraps Scoped SapService).
    // Service is Scoped (depends on Scoped SapService, ProductCacheService, NeonProductSyncService).
    builder.Services.AddSingleton<SapReplitAPI.Services.ProductAdmin.ProductPriceAuditRepository>();
    builder.Services.AddScoped<SapReplitAPI.Services.ProductAdmin.ISapPriceAdapter,
                                SapReplitAPI.Services.ProductAdmin.SapPriceAdapter>();
    builder.Services.AddScoped<SapReplitAPI.Services.ProductAdmin.ZfProductAdminService>();

    // Offline Fulfillment V2 — options always bound; services only active when Enabled=true
    builder.Services.Configure<SapReplitAPI.Models.Offline.OfflineFulfillmentOptions>(
        builder.Configuration.GetSection(SapReplitAPI.Models.Offline.OfflineFulfillmentOptions.Section));
    builder.Services.AddScoped<SapReplitAPI.Services.Offline.OfflineFulfillmentService>();
    builder.Services.AddScoped<SapReplitAPI.Services.Offline.IOfflineSapAdapter,
                                 SapReplitAPI.Services.Offline.OfflineSapAdapter>();
    builder.Services.AddScoped<SapReplitAPI.Services.Offline.OfflineFulfillmentRecoveryService>();
    builder.Services.AddScoped<SapReplitAPI.Jobs.OfflineFulfillmentRecoveryJob>();

    // Delivery cache (SQLite only — no Neon dependency)
    builder.Services.AddScoped<DeliveryCacheService>();

    // Credit Memo cache (ORIN/RIN1 fast path — SQLite + Neon)
    builder.Services.AddScoped<CreditMemoCacheService>();
    builder.Services.AddScoped<ReturnRequestCacheService>();
    builder.Services.AddScoped<SapReplitAPI.Services.Returns.ReturnsMirrorBackfillService>();
    builder.Services.AddScoped<SapReplitAPI.Services.Invoice.InvoiceBaseRefBackfillService>();

    // Pick list cache + event-driven fast path (Gate: PickList Cache / Neon Freshness)
    builder.Services.AddSingleton<SapReplitAPI.Services.PickList.NeonPickListWriteCoordinator>();
    builder.Services.AddScoped<SapReplitAPI.Services.PickList.PickListTimestampReader>();
    builder.Services.AddScoped<SapReplitAPI.Services.PickList.PickListCacheService>();
    builder.Services.AddScoped<SapReplitAPI.Services.PickList.IPickListEventRefreshService,
                                SapReplitAPI.Services.PickList.PickListEventRefreshService>();

    // Warehouse App — Select Bin & Pick workflow
    builder.Services.AddScoped<SapReplitAPI.Services.Warehouse.IWarehousePickSapAdapter,
                                SapReplitAPI.Services.Warehouse.SapWarehousePickAdapter>();
    builder.Services.AddScoped<SapReplitAPI.Services.Warehouse.WarehouseBinPickService>();

    // TodayOrders event-driven fast path (Gate: Today Orders Event Refresh)
    // Singleton coordinator serializes Neon TodayOrder writes between event path and NeonSyncJob full replace.
    builder.Services.AddSingleton<SapReplitAPI.Services.TodayOrders.NeonTodayOrderWriteCoordinator>();
    builder.Services.AddScoped<SapReplitAPI.Services.TodayOrders.TodayOrderEventRefreshService>();

    // Background task queue
    builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
    builder.Services.AddHostedService<QueuedHostedService>();

    builder.Services.AddDbContext<CacheDbContext>(options =>
        options
            .UseSqlite(builder.Configuration.GetConnectionString("CacheDB"))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

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

    // ── Phase 1 OutboxPoller — guarded by MolasIntegration connection string ─
    // Connection string expected via env var ConnectionStrings__MolasIntegration
    // (maps to GetConnectionString("MolasIntegration")). Never set in appsettings.json.
    // Login SapReplitOutboxApp requires SELECT + UPDATE on dbo.SapEventOutbox only.
    var molasCs = builder.Configuration.GetConnectionString("MolasIntegration");
    if (!string.IsNullOrWhiteSpace(molasCs))
    {
        // OutboxClaimService: Singleton — creates new SqlConnection per method, never holds one open.
        builder.Services.AddSingleton<OutboxClaimService>();

        // EventHandlerRouter always registered when poller is active.
        // If no handlers are registered (e.g. Neon not configured), router marks events Done.
        builder.Services.AddScoped<EventHandlerRouter>();

        // Event handlers require Neon — only register when Neon is present.
        if (!string.IsNullOrWhiteSpace(neonCs))
        {
            builder.Services.AddScoped<NeonEventWriteService>();
            builder.Services.AddScoped<NeonDeliveryWriteService>();
            builder.Services.AddScoped<NeonCreditMemoWriteService>();
            builder.Services.AddScoped<NeonReturnRequestWriteService>();
            // Phase 2: inventory fast-path service (SAP → SQLite → Neon, coordinator-guarded)
            builder.Services.AddScoped<InventoryEventRefreshService>();
            // Shared per-invoice refresh (event fast-path + drift detection)
            builder.Services.AddScoped<InvoiceMirrorRefreshService>();
            builder.Services.AddScoped<SapReplitAPI.Services.Events.IInvoiceMirrorRefresher>(sp => sp.GetRequiredService<InvoiceMirrorRefreshService>());
            builder.Services.AddScoped<SapReplitAPI.Services.Events.IInvoiceChangeSource>(sp => sp.GetRequiredService<SapService>());
            // ZF report snapshot + on-demand PDF (registered alongside InvoiceEventHandler — same guards)
            builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentReportRepository>();
            builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentReportCacheService>();
            builder.Services.AddScoped<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentReportService>();
            // Phase 1 handlers
            builder.Services.AddScoped<ISapEventHandler, InvoiceEventHandler>();
            builder.Services.AddScoped<ISapEventHandler, IncomingPaymentEventHandler>();
            builder.Services.AddScoped<ISapEventHandler, CreditMemoEventHandler>();
            builder.Services.AddScoped<ISapEventHandler, ReturnRequestEventHandler>();
            // Phase 2 handlers — inventory + delivery events
            builder.Services.AddScoped<ISapEventHandler, SalesOrderCommitmentEventHandler>();   // 17/A,U,C
            builder.Services.AddScoped<ISapEventHandler, DeliveryInventoryEventHandler>();      // 15/A
            builder.Services.AddScoped<ISapEventHandler, ReturnInventoryEventHandler>();        // 16/A
            builder.Services.AddScoped<ISapEventHandler, GoodsReceiptPoEventHandler>();        // 20/A
            builder.Services.AddScoped<ISapEventHandler, GoodsReceiptInventoryEventHandler>(); // 59/A
            builder.Services.AddScoped<ISapEventHandler, GoodsIssueInventoryEventHandler>();   // 60/A
            builder.Services.AddScoped<ISapEventHandler, StockTransferInventoryEventHandler>();// 67/A,C
        }
        else
        {
            // CRITICAL: MolasIntegration is active (SP will emit events) but Neon is absent.
            // All outbox events would be silently marked Done with no handler to process them.
            // This is a misconfiguration that must be caught at startup, not at first event.
            throw new InvalidOperationException(
                "CRITICAL CONFIGURATION ERROR: MolasIntegration (SapEventOutbox) is enabled " +
                "but NeonDb connection string is missing. " +
                "All SAP events would be silently discarded with no handler. " +
                "Set ConnectionStrings__NeonDb before starting the service.");
        }

        // Background poller — Singleton lifetime via AddHostedService.
        builder.Services.AddHostedService<OutboxPollerService>();
        Log.Information("✅ OutboxPollerService registered (MolasIntegration connection string found).");
    }
    else
    {
        Log.Warning(
            "[EVENT-PIPELINE] MolasIntegrationConfigured=false OutboxPoller=DISABLED — " +
            "event-driven sync is INACTIVE; InvoiceDeltaSyncJob and NeonSyncJob are the only sync path. " +
            "Set env var ConnectionStrings__MolasIntegration on the host to enable.");
    }

    // Startup timezone diagnostic — Tanzania EAT (UTC+3, no DST).
    // Logged so ops can confirm the Windows TZ ID is present on the host machine.
    {
        var tz = SoDeliveryJob.BusinessTz;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        Log.Information("🌍 Business timezone: {TzId} | UTC offset: {Offset} | Local now (EAT): {Now:yyyy-MM-dd HH:mm:ss}",
            tz.Id, tz.BaseUtcOffset, localNow);
    }

    // Quartz Jobs Configuration
    Log.Information("📅 Configuring Quartz jobs...");

    builder.Services.AddQuartz(q =>
    {
        // UseMicrosoftDependencyInjectionJobFactory is now the default — no call needed

        // Frequent cache freshness jobs — use cron instead of startup-relative intervals
        q.AddCronJobAndTrigger<InvoiceDeltaSyncJob>("InvoiceDeltaSyncJob", "0 0/5 * * * ?");
        q.AddCronJobAndTrigger<OrderDeltaSyncJob>("OrderDeltaSyncJob", "0 2/5 * * * ?");
        q.AddCronJobAndTrigger<SyncTodayOrdersJob>("SyncTodayOrdersJob", "0 0/3 * * * ?");
        q.AddCronJobAndTrigger<SyncOpenOrdersJob>("SyncOpenOrdersJob", "0 4/10 * * * ?");
        q.AddCronJobAndTrigger<ProductDeltaSyncJob>("ProductDeltaSyncJob", "0 3/15 * * * ?");

        // Reconciliation / cleanup jobs
        q.AddCronJobAndTrigger<CustomerFullSyncJob>("CustomerFullSyncJob", "0 0 1 * * ?");
        q.AddCronJobAndTrigger<ProductFullSyncJob>("ProductFullSyncJob", "0 0 2 * * ?");
        q.AddCronJobAndTrigger<OrderFullSyncJob>("OrderFullSyncJob", "0 0 3 * * ?");
        q.AddCronJobAndTrigger<InvoiceFullSyncJob>("InvoiceFullSyncJob", "0 0 4 * * ?");
        q.AddCronJobAndTrigger<InvoiceStatusCacheJob>("InvoiceStatusCacheJob", "0 15 5 * * ?");

        // GL account statements: SAP → SQLite (always, no Neon dependency)
        q.AddCronJobAndTrigger<AccountStatementSyncJob>("AccountStatementSyncJob", "0 0/5 * * * ?");

        // Invoice open deliveries every hour from 06:00 to 20:00
        q.AddCronJobAndTrigger<InvoiceFromDeliveryJob>("InvoiceFromDeliveryJob", "0 0 6-20 * * ?");

        // WarehouseInventory full sync — 02:30 EAT, DoNothing misfire (delta covers gaps)
        {
            var whFullKey = new JobKey("WarehouseInventoryFullSyncJob");
            q.AddJob<WarehouseInventoryFullSyncJob>(opts => opts.WithIdentity(whFullKey));
            q.AddTrigger(opts => opts
                .ForJob(whFullKey)
                .WithIdentity("WarehouseInventoryFullSyncJob-trigger")
                .WithCronSchedule("0 30 2 * * ?", cron => cron
                    .InTimeZone(SoDeliveryJob.BusinessTz)
                    .WithMisfireHandlingInstructionDoNothing()));
        }

        // WarehouseInventory delta sync — every 15 min at :08/:23/:38/:53 EAT.
        // Offset from ProductDeltaSyncJob (:03/:18/:33/:48) to avoid simultaneous SAP queries.
        {
            var whDeltaKey = new JobKey("WarehouseInventoryDeltaSyncJob");
            q.AddJob<WarehouseInventoryDeltaSyncJob>(opts => opts.WithIdentity(whDeltaKey));
            q.AddTrigger(opts => opts
                .ForJob(whDeltaKey)
                .WithIdentity("WarehouseInventoryDeltaSyncJob-trigger")
                .WithCronSchedule("0 8/15 * * * ?", cron => cron
                    .InTimeZone(SoDeliveryJob.BusinessTz)
                    .WithMisfireHandlingInstructionDoNothing()));
        }

        // BinInventory full sync — 03:00 EAT, DoNothing misfire.
        // Staggered after WarehouseInventoryFullSyncJob (02:30) to avoid simultaneous SAP reads.
        {
            var binFullKey = new JobKey("BinInventoryFullSyncJob");
            q.AddJob<BinInventoryFullSyncJob>(opts => opts.WithIdentity(binFullKey));
            q.AddTrigger(opts => opts
                .ForJob(binFullKey)
                .WithIdentity("BinInventoryFullSyncJob-trigger")
                .WithCronSchedule("0 0 3 * * ?", cron => cron
                    .InTimeZone(SoDeliveryJob.BusinessTz)
                    .WithMisfireHandlingInstructionDoNothing()));
        }

        // BinInventory delta sync — every 15 min at :13/:28/:43/:58 EAT.
        // Staggered 5 min after WarehouseInventoryDeltaSyncJob (:08/:23/:38/:53)
        // and 10 min after ProductDeltaSyncJob (:03/:18/:33/:48).
        {
            var binDeltaKey = new JobKey("BinInventoryDeltaSyncJob");
            q.AddJob<BinInventoryDeltaSyncJob>(opts => opts.WithIdentity(binDeltaKey));
            q.AddTrigger(opts => opts
                .ForJob(binDeltaKey)
                .WithIdentity("BinInventoryDeltaSyncJob-trigger")
                .WithCronSchedule("0 13/15 * * * ?", cron => cron
                    .InTimeZone(SoDeliveryJob.BusinessTz)
                    .WithMisfireHandlingInstructionDoNothing()));
        }

        // Delivery full sync — 04:30 EAT, DoNothing misfire.
        // Staggered after BinInventoryFullSyncJob (03:00) and WarehouseInventoryFullSyncJob (02:30).
        {
            var delFullKey = new JobKey("DeliveryFullSyncJob");
            q.AddJob<DeliveryFullSyncJob>(opts => opts.WithIdentity(delFullKey));
            q.AddTrigger(opts => opts
                .ForJob(delFullKey)
                .WithIdentity("DeliveryFullSyncJob-trigger")
                .WithCronSchedule("0 30 4 * * ?", cron => cron
                    .InTimeZone(SoDeliveryJob.BusinessTz)
                    .WithMisfireHandlingInstructionDoNothing()));
        }

        // Delivery delta sync — every 5 min at :01/:06/:11...
        q.AddCronJobAndTrigger<DeliveryDeltaSyncJob>("DeliveryDeltaSyncJob", "0 1/5 * * * ?");

        // Pick list full sync — 05:45 EAT, DoNothing misfire (after Delivery full at 04:30)
        {
            var plFullKey = new JobKey("PickListFullSyncJob");
            q.AddJob<PickListFullSyncJob>(opts => opts.WithIdentity(plFullKey));
            q.AddTrigger(opts => opts
                .ForJob(plFullKey)
                .WithIdentity("PickListFullSyncJob-trigger")
                .WithCronSchedule("0 45 5 * * ?", cron => cron
                    .InTimeZone(SoDeliveryJob.BusinessTz)
                    .WithMisfireHandlingInstructionDoNothing()));
        }

        // Pick list delta sync — every 5 min at :03/:08/:13...
        q.AddCronJobAndTrigger<PickListDeltaSyncJob>("PickListDeltaSyncJob", "0 3/5 * * * ?");

        // Pick list cache freshness — every 30 s; mirrors external OPKL picks (Warehouse App,
        // SAP GUI) to SQLite + Neon per-AbsEntry. No delivery automation — that is owned by
        // ZoneFulfillmentPickReconciliationJob below.
        q.AddCronJobAndTrigger<SapReplitAPI.Jobs.PickListCacheFreshnessJob>(
            "PickListCacheFreshnessJob", "0/30 * * * * ?");

        // ZF pick reconciliation — every 30 s; detects SAP-native OPKL confirmations
        // that bypassed the API and unblocks delivery automation.
        q.AddCronJobAndTrigger<SapReplitAPI.Jobs.ZoneFulfillmentPickReconciliationJob>(
            "ZoneFulfillmentPickReconciliationJob", "0/30 * * * * ?");

        // Neon mirror — offset after upstream cache jobs and only registered if connection string present
        if (!string.IsNullOrWhiteSpace(neonCs))
        {
            q.AddCronJobAndTrigger<NeonSyncJob>("NeonSyncJob", "0 2/3 * * * ?");
            q.AddCronJobAndTrigger<InvoiceDriftDetectionJob>("InvoiceDriftDetectionJob", "0 0/15 * * * ?");
            q.AddCronJobAndTrigger<PendingOrderSyncJob>("PendingOrderSyncJob", "0/15 * * * * ?");    // every 15 s
            q.AddCronJobAndTrigger<PendingCustomerSyncJob>("PendingCustomerSyncJob", "0/15 * * * * ?"); // every 15 s
            // Offline Fulfillment V2 recovery — every 30 s; no-op when Enabled=false
            q.AddCronJobAndTrigger<SapReplitAPI.Jobs.OfflineFulfillmentRecoveryJob>(
                "OfflineFulfillmentRecoveryJob", "0/30 * * * * ?");
        }

        // SO → Delivery nightly job — registered as durable but WITHOUT a trigger.
        // Enable the 20:00 EAT cron by adding the q.AddTrigger block below when ready for production.
        //
        // When ready to enable automatic nightly runs, restore the trigger:
        //   q.AddTrigger(opts => opts
        //       .ForJob(soJobKey)
        //       .WithIdentity("SoDeliveryJob-trigger")
        //       .WithCronSchedule("0 0 20 * * ?", cron => cron
        //           .InTimeZone(SoDeliveryJob.BusinessTz)
        //           .WithMisfireHandlingInstructionDoNothing()));
        {
            var soJobKey = new JobKey("SoDeliveryJob");
            q.AddJob<SoDeliveryJob>(opts => opts
                .WithIdentity(soJobKey)
                .StoreDurably());
            // Trigger intentionally omitted — use POST /api/so-delivery/run for manual runs.
        }
    });

    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    Log.Information("📅 Warehouse inventory schedule (EAT = UTC+3):");
    Log.Information("   WarehouseInventoryFullSyncJob  — cron: 0 30 2 * * ? | TZ: E. Africa Standard Time | misfire: DoNothing");
    Log.Information("   WarehouseInventoryDeltaSyncJob — cron: 0 8/15 * * * ? | TZ: E. Africa Standard Time | misfire: DoNothing");
    Log.Information("📅 Bin inventory schedule (EAT = UTC+3):");
    Log.Information("   BinInventoryFullSyncJob  — cron: 0 0 3 * * ? | TZ: E. Africa Standard Time | misfire: DoNothing");
    Log.Information("   BinInventoryDeltaSyncJob — cron: 0 13/15 * * * ? | TZ: E. Africa Standard Time | misfire: DoNothing");

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
    builder.Services.AddScoped<InvoiceFromDeliveryService>();
    builder.Services.AddScoped<InvoiceFromDeliveryJob>();
    builder.Services.AddScoped<WarehouseInventoryFullSyncJob>();
    builder.Services.AddScoped<WarehouseInventoryDeltaSyncJob>();
    builder.Services.AddScoped<BinInventoryFullSyncJob>();
    builder.Services.AddScoped<BinInventoryDeltaSyncJob>();
    builder.Services.AddScoped<DeliveryFullSyncJob>();
    builder.Services.AddScoped<DeliveryDeltaSyncJob>();
    builder.Services.AddScoped<PickListFullSyncJob>();
    builder.Services.AddScoped<PickListDeltaSyncJob>();
    builder.Services.AddScoped<SapReplitAPI.Services.PickList.IPickListSapHeaderReader,
                                  SapReplitAPI.Services.PickList.SapPickListHeaderAdapter>();
    builder.Services.AddScoped<SapReplitAPI.Services.PickList.PickListMirrorFreshnessService>();
    builder.Services.AddScoped<SapReplitAPI.Jobs.PickListCacheFreshnessJob>();
    builder.Services.AddScoped<SapReplitAPI.Jobs.ZoneFulfillmentPickReconciliationJob>();
    if (!string.IsNullOrWhiteSpace(neonCs))
    {
        builder.Services.AddScoped<NeonSyncJob>();
        builder.Services.AddScoped<InvoiceDriftDetectionJob>();
        builder.Services.AddScoped<PendingOrderSyncJob>();
        builder.Services.AddScoped<PendingCustomerService>();
        builder.Services.AddScoped<PendingCustomerSyncJob>();
    }

    // SO → Delivery job DI registration — must be Scoped so SoDeliveryService
    // (which holds the static SemaphoreSlim) resolves correctly per Quartz execution scope.
    builder.Services.AddScoped<SoDeliveryJob>();

    var app = builder.Build();

    // Explicit maintenance/read-only commands exit before database startup, jobs or HTTP hosting.
    if (args.Contains("--backfill-returned-qty") || args.Contains("--verify-invoice-returns") || args.Contains("--backfill-returns-mirror"))
    {
        if (args.Contains("--backfill-returns-mirror"))
        {
            await EnsureReturnsSchemaForMaintenanceAsync(app.Services, app.Configuration);
        }

        using var scope = app.Services.CreateScope();
        if (args.Contains("--backfill-returns-mirror"))
        {
            var service = ActivatorUtilities.CreateInstance<SapReplitAPI.Services.Returns.ReturnsMirrorBackfillService>(scope.ServiceProvider);
            var result = await service.RunAsync();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            Environment.ExitCode = result.Errors.Count == 0 ? 0 : 1;
        }
        else if (args.Contains("--backfill-returned-qty"))
        {
            var service = ActivatorUtilities.CreateInstance<SapReplitAPI.Services.Returns.ReturnedQtyBackfillService>(scope.ServiceProvider);
            var result = await service.RunAsync();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            Environment.ExitCode = result.Complete ? 0 : 1;
        }
        else
        {
            var cardCode = builder.Configuration["returns-card-code"]
                ?? throw new ArgumentException("--returns-card-code is required");
            var result = scope.ServiceProvider.GetRequiredService<SapService>().GetInvoiceReturns(cardCode, "all");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        }
        return;
    }

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

                // Pre-mark EF migrations as applied when their columns already exist from prior manual
                // startup ALTER TABLE statements. Without this, Migrate() fails with "duplicate column name"
                // on databases that were upgraded manually before these migrations were created.
                {
                    var migrConn = db.Database.GetDbConnection();
                    if (migrConn.State != System.Data.ConnectionState.Open) migrConn.Open();

                    // Ensure __EFMigrationsHistory exists so we can INSERT into it before Migrate() creates it.
                    using (var ensureHist = migrConn.CreateCommand())
                    {
                        ensureHist.CommandText = @"CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory""(""MigrationId"" TEXT NOT NULL PRIMARY KEY, ""ProductVersion"" TEXT NOT NULL)";
                        ensureHist.ExecuteNonQuery();
                    }

                    // 20260914000001: ReturnedQty on InvoiceLines
                    using (var chk = migrConn.CreateCommand())
                    {
                        chk.CommandText = @"SELECT COUNT(*) FROM pragma_table_info('InvoiceLines') WHERE name='ReturnedQty'";
                        if ((long)chk.ExecuteScalar()! > 0)
                        {
                            using var ins = migrConn.CreateCommand();
                            ins.CommandText = @"INSERT OR IGNORE INTO ""__EFMigrationsHistory""(""MigrationId"",""ProductVersion"") VALUES('20260914000001_AddReturnedQtyToInvoiceLines','8.0.8')";
                            ins.ExecuteNonQuery();
                            logger.LogInformation("✅ Pre-marked migration 20260914000001 (ReturnedQty already exists).");
                        }
                    }

                    // 20260914000002: InvoiceDocEntry on CreditMemoLines
                    using (var chk = migrConn.CreateCommand())
                    {
                        chk.CommandText = @"SELECT COUNT(*) FROM pragma_table_info('CreditMemoLines') WHERE name='InvoiceDocEntry'";
                        if ((long)chk.ExecuteScalar()! > 0)
                        {
                            using var ins = migrConn.CreateCommand();
                            ins.CommandText = @"INSERT OR IGNORE INTO ""__EFMigrationsHistory""(""MigrationId"",""ProductVersion"") VALUES('20260914000002_AddInvoiceRefToCreditMemoLines','8.0.8')";
                            ins.ExecuteNonQuery();
                            logger.LogInformation("✅ Pre-marked migration 20260914000002 (InvoiceDocEntry already exists).");
                        }
                    }
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
                try
                {
                    db.Database.ExecuteSqlRaw(@"ALTER TABLE ""InvoicePayments"" ADD COLUMN ""ClientReference"" TEXT NOT NULL DEFAULT ''");
                    logger.LogInformation("✅ InvoicePayments.ClientReference column added.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
                try
                {
                    db.Database.ExecuteSqlRaw(@"ALTER TABLE ""InvoicePayments"" ADD COLUMN ""Canceled"" INTEGER NOT NULL DEFAULT 0");
                    logger.LogInformation("✅ InvoicePayments.Canceled column added.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
                try
                {
                    db.Database.ExecuteSqlRaw(@"ALTER TABLE ""InvoicePayments"" ADD COLUMN ""CounterRef"" TEXT NOT NULL DEFAULT ''");
                    logger.LogInformation("✅ InvoicePayments.CounterRef column added.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
                try
                {
                    db.Database.ExecuteSqlRaw(@"ALTER TABLE ""InvoicePayments"" ADD COLUMN ""LastUpdated"" TEXT NOT NULL DEFAULT '1900-01-01 00:00:00'");
                    logger.LogInformation("✅ InvoicePayments.LastUpdated column added.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
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

                db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""InvoiceFromDeliveryLogs"" (
    ""Id""               INTEGER PRIMARY KEY AUTOINCREMENT,
    ""DeliveryDocEntry"" INTEGER NOT NULL,
    ""DeliveryDocNum""   INTEGER NOT NULL,
    ""CardCode""         TEXT    NOT NULL,
    ""InvoiceDocEntry""  INTEGER,
    ""InvoiceDocNum""    INTEGER,
    ""Status""           TEXT    NOT NULL,
    ""ErrorMessage""     TEXT,
    ""TriggerSource""    TEXT    NOT NULL,
    ""ProcessedAt""      TEXT    NOT NULL
)");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_InvoiceFromDeliveryLogs_DeliveryDocEntry_Status"" ON ""InvoiceFromDeliveryLogs"" (""DeliveryDocEntry"", ""Status"")");
                logger.LogInformation("✅ InvoiceFromDeliveryLogs table ensured.");

                // Credit Memo cache (ORIN/RIN1 fast path) — event-driven, no NeonSyncJob dependency
                db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""CreditMemoHeaders"" (
    ""DocEntry""   INTEGER PRIMARY KEY,
    ""DocNum""     INTEGER NOT NULL,
    ""CardCode""   TEXT    NOT NULL,
    ""CardName""   TEXT    NOT NULL,
    ""DocDate""    TEXT    NOT NULL,
    ""DocDueDate"" TEXT    NOT NULL,
    ""DocStatus""  TEXT    NOT NULL,
    ""Canceled""   TEXT    NOT NULL,
    ""DocTotal""   REAL    NOT NULL,
    ""Comments""   TEXT    NOT NULL DEFAULT '',
    ""SlpCode""    INTEGER NOT NULL,
    ""SlpName""    TEXT    NOT NULL DEFAULT '',
    ""U_AppRef""   TEXT,
    ""CreateDate"" TEXT    NOT NULL,
    ""UpdateDate"" TEXT    NOT NULL
)");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CreditMemoHeaders_CardCode"" ON ""CreditMemoHeaders"" (""CardCode"")");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CreditMemoHeaders_DocDate"" ON ""CreditMemoHeaders"" (""DocDate"")");
                db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""CreditMemoLines"" (
    ""Id""         INTEGER PRIMARY KEY AUTOINCREMENT,
    ""DocEntry""   INTEGER NOT NULL,
    ""LineNum""    INTEGER NOT NULL,
    ""ItemCode""   TEXT    NOT NULL,
    ""Dscription"" TEXT    NOT NULL DEFAULT '',
    ""Quantity""   REAL    NOT NULL,
    ""Price""      REAL    NOT NULL,
    ""LineTotal""  REAL    NOT NULL,
    ""WhsCode""    TEXT    NOT NULL,
    ""BaseType""   INTEGER NOT NULL,
    ""BaseEntry""  INTEGER NOT NULL,
    ""BaseLine""   INTEGER NOT NULL
)");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""UX_CreditMemoLines_DocEntry_LineNum"" ON ""CreditMemoLines"" (""DocEntry"", ""LineNum"")");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CreditMemoLines_DocEntry"" ON ""CreditMemoLines"" (""DocEntry"")");

                db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""ReturnRequests"" (
    ""DocEntry""    INTEGER PRIMARY KEY,
    ""DocNum""      INTEGER NOT NULL,
    ""CardCode""    TEXT    NOT NULL,
    ""CardName""    TEXT    NOT NULL,
    ""DocDate""     TEXT    NOT NULL,
    ""DocStatus""   TEXT    NOT NULL,
    ""Canceled""    TEXT    NOT NULL,
    ""DocTotal""    REAL    NOT NULL,
    ""U_AppRef""    TEXT,
    ""U_ReplitId""  TEXT,
    ""Comments""    TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_CardCode"" ON ""ReturnRequests"" (""CardCode"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_DocDate"" ON ""ReturnRequests"" (""DocDate"");

CREATE TABLE IF NOT EXISTS ""ReturnRequestLines"" (
    ""Id""         INTEGER PRIMARY KEY AUTOINCREMENT,
    ""DocEntry""   INTEGER NOT NULL,
    ""LineNum""    INTEGER NOT NULL,
    ""BaseType""   INTEGER NOT NULL,
    ""BaseEntry""  INTEGER NOT NULL,
    ""BaseLine""   INTEGER NOT NULL,
    ""ItemCode""   TEXT    NOT NULL,
    ""Dscription"" TEXT    NOT NULL DEFAULT '',
    ""Quantity""   REAL    NOT NULL,
    ""OpenQty""    REAL    NOT NULL,
    ""WhsCode""    TEXT    NOT NULL,
    ""LineStatus"" TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""UX_ReturnRequestLines_DocEntry_LineNum"" ON ""ReturnRequestLines"" (""DocEntry"", ""LineNum"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_DocEntry"" ON ""ReturnRequestLines"" (""DocEntry"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_BaseRef"" ON ""ReturnRequestLines"" (""BaseType"", ""BaseEntry"", ""BaseLine"");");

                // ReturnedQty on InvoiceLines — Option B line-level return tracking
                try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""ReturnedQty"" REAL NOT NULL DEFAULT 0"); }
                catch { /* already exists */ }
                // PendingReturnQty on InvoiceLines — open ORRR quantities (authoritative recompute)
                try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""PendingReturnQty"" REAL NOT NULL DEFAULT 0"); }
                catch { /* already exists */ }
                // InvoiceDocEntry / InvoiceLineNum on CreditMemoLines — resolved OINV reference
                try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""CreditMemoLines"" ADD COLUMN ""InvoiceDocEntry"" INTEGER"); }
                catch { /* already exists */ }
                try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""CreditMemoLines"" ADD COLUMN ""InvoiceLineNum"" INTEGER"); }
                catch { /* already exists */ }
                logger.LogInformation("✅ CreditMemoHeaders + CreditMemoLines tables ensured. ReturnedQty + InvoiceRef columns ensured.");

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

                        await neonDb.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoicePayments"" ADD COLUMN IF NOT EXISTS ""ClientReference"" text    NOT NULL DEFAULT '';
ALTER TABLE ""InvoicePayments"" ADD COLUMN IF NOT EXISTS ""Canceled""        boolean NOT NULL DEFAULT false;
ALTER TABLE ""InvoicePayments"" ADD COLUMN IF NOT EXISTS ""CounterRef""      text    NOT NULL DEFAULT '';
ALTER TABLE ""InvoicePayments"" ADD COLUMN IF NOT EXISTS ""LastUpdated""     timestamp NOT NULL DEFAULT '1900-01-01 00:00:00';");

                        // Fix Neon InvoicePayments unique index: drop old PaymentDocEntry-only index/constraint,
                        // create composite (DocEntry, PaymentDocEntry).
                        await neonDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    -- Drop unique constraint if EF created it as a CONSTRAINT (not index)
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_name = 'InvoicePayments'
          AND constraint_type = 'UNIQUE'
          AND constraint_name IN ('IX_InvoicePayments_PaymentDocEntry','ix_invoicepayments_paymentdocentry')
    ) THEN
        ALTER TABLE ""InvoicePayments""
            DROP CONSTRAINT IF EXISTS ""IX_InvoicePayments_PaymentDocEntry"",
            DROP CONSTRAINT IF EXISTS ""ix_invoicepayments_paymentdocentry"";
    END IF;
    -- Drop as standalone index
    DROP INDEX IF EXISTS ""IX_InvoicePayments_PaymentDocEntry"";
    DROP INDEX IF EXISTS ""ix_invoicepayments_paymentdocentry"";
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_InvoicePayments_DocEntry_PaymentDocEntry""
    ON ""InvoicePayments"" (""DocEntry"", ""PaymentDocEntry"");");

                        // Offline order queue tables
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""PendingOrders"" (
    ""Id""           SERIAL PRIMARY KEY,
    ""ReplitId""     text         NOT NULL,
    ""CardCode""     text         NOT NULL,
    ""DocDate""      date         NOT NULL,
    ""DeliveryDate"" date,
    ""SlpCode""      integer,
    ""DocCurrency""  text         NOT NULL DEFAULT 'TZS',
    ""Status""       text         NOT NULL DEFAULT 'Pending',
    ""SapDocEntry""  integer,
    ""RetryCount""   integer      NOT NULL DEFAULT 0,
    ""NextRetryAt""  timestamptz,
    ""ErrorMessage"" text,
    ""CreatedAt""    timestamptz  NOT NULL DEFAULT NOW(),
    ""SyncedAt""     timestamptz,
    UNIQUE (""ReplitId"")
);
CREATE INDEX IF NOT EXISTS ""IX_PendingOrders_Status"" ON ""PendingOrders"" (""Status"");
CREATE TABLE IF NOT EXISTS ""PendingOrderLines"" (
    ""Id""             SERIAL PRIMARY KEY,
    ""PendingOrderId"" integer      NOT NULL REFERENCES ""PendingOrders""(""Id"") ON DELETE CASCADE,
    ""LineNum""        integer      NOT NULL,
    ""ItemCode""       text         NOT NULL,
    ""Quantity""       integer      NOT NULL,
    ""Price""          numeric(18,2) NOT NULL,
    ""WhsCode""        text         NOT NULL DEFAULT '001',
    ""Dscription""     text,
    ""U_Manufacturer"" text,
    UNIQUE (""PendingOrderId"", ""LineNum"")
);");

                        // Pending customer queue
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""PendingCustomers"" (
    ""Id""              SERIAL PRIMARY KEY,
    ""CardName""        text         NOT NULL,
    ""Phone""           text         NOT NULL DEFAULT '',
    ""CustomerType""    text         NOT NULL DEFAULT '',
    ""Region""          text         NOT NULL DEFAULT '',
    ""SalesPersonName"" text         NOT NULL DEFAULT '',
    ""SlpCode""         integer      NOT NULL DEFAULT 0,
    ""VIN1""            text,
    ""VIN2""            text,
    ""VIN3""            text,
    ""SapCardCode""     text,
    ""Status""          text         NOT NULL DEFAULT 'Pending',
    ""RetryCount""      integer      NOT NULL DEFAULT 0,
    ""NextRetryAt""     timestamptz,
    ""ErrorMessage""    text,
    ""CreatedAt""       timestamptz  NOT NULL DEFAULT NOW(),
    ""SyncedAt""        timestamptz
);
CREATE INDEX IF NOT EXISTS ""IX_PendingCustomers_Status"" ON ""PendingCustomers"" (""Status"");");

                        // Warehouse inventory mirror — one row per (ItemCode, WhsCode)
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""WarehouseInventory"" (
    ""Id""              SERIAL PRIMARY KEY,
    ""ItemCode""        text          NOT NULL,
    ""WhsCode""         text          NOT NULL,
    ""WarehouseName""   text          NOT NULL DEFAULT '',
    ""OnHand""          numeric(18,4) NOT NULL DEFAULT 0,
    ""IsCommitted""     numeric(18,4) NOT NULL DEFAULT 0,
    ""OnOrder""         numeric(18,4) NOT NULL DEFAULT 0,
    ""AvailableToSell"" numeric(18,4) NOT NULL DEFAULT 0,
    ""IsBinManaged""    boolean       NOT NULL DEFAULT false,
    ""LastUpdated""     timestamptz   NOT NULL,
    UNIQUE (""ItemCode"", ""WhsCode"")
);
CREATE INDEX IF NOT EXISTS ""IX_WarehouseInventory_WhsCode""
    ON ""WarehouseInventory"" (""WhsCode"");");

                        // Bin inventory mirror — one row per (ItemCode, WhsCode, BinAbsEntry)
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""BinInventory"" (
    ""Id""          SERIAL PRIMARY KEY,
    ""ItemCode""    text          NOT NULL,
    ""WhsCode""     text          NOT NULL,
    ""BinAbsEntry"" integer       NOT NULL,
    ""BinCode""     text          NOT NULL DEFAULT '',
    ""BinOnHand""   numeric(18,4) NOT NULL DEFAULT 0,
    ""LastUpdated"" timestamptz   NOT NULL,
    UNIQUE (""ItemCode"", ""WhsCode"", ""BinAbsEntry"")
);
CREATE INDEX IF NOT EXISTS ""IX_BinInventory_WhsCode""
    ON ""BinInventory"" (""WhsCode"");
CREATE INDEX IF NOT EXISTS ""IX_BinInventory_ItemCode""
    ON ""BinInventory"" (""ItemCode"");");

                        // Phase 2: Delivery mirror — header + lines (event-driven + NeonSyncJob reconcile)
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""Deliveries"" (
    ""DocEntry""         integer        NOT NULL PRIMARY KEY,
    ""DocNum""           integer        NOT NULL,
    ""DocDate""          date           NOT NULL,
    ""DocDueDate""       date           NOT NULL,
    ""TaxDate""          date           NOT NULL,
    ""DocStatus""        text           NOT NULL,
    ""Canceled""         text           NOT NULL,
    ""CardCode""         text           NOT NULL,
    ""CardName""         text           NOT NULL,
    ""DocTotal""         numeric(18,2)  NOT NULL,
    ""DocCur""           text           NOT NULL,
    ""SlpCode""          integer        NOT NULL,
    ""SlpName""          text           NOT NULL DEFAULT '',
    ""UserSign""         integer        NOT NULL,
    ""Comments""         text                    DEFAULT '',
    ""CreateDate""       date           NOT NULL,
    ""CreateTS""         integer        NOT NULL,
    ""UpdateDate""       date           NOT NULL,
    ""UpdateTS""         integer        NOT NULL,
    ""BPLId""            integer        NOT NULL,
    ""U_ReplitId""       text,
    ""DocStatusDisplay"" text           NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ""IX_Deliveries_DocDate""
    ON ""Deliveries"" (""DocDate"");
CREATE INDEX IF NOT EXISTS ""IX_Deliveries_CardCode""
    ON ""Deliveries"" (""CardCode"");
CREATE INDEX IF NOT EXISTS ""IX_Deliveries_DocStatus_Canceled""
    ON ""Deliveries"" (""DocStatus"", ""Canceled"");

CREATE TABLE IF NOT EXISTS ""DeliveryLines"" (
    ""DocEntry""    integer        NOT NULL,
    ""LineNum""     integer        NOT NULL,
    ""ItemCode""    text           NOT NULL,
    ""Dscription""  text           NOT NULL DEFAULT '',
    ""Quantity""    numeric(18,4)  NOT NULL,
    ""OpenQty""     numeric(18,4)  NOT NULL,
    ""WhsCode""     text           NOT NULL,
    ""Price""       numeric(18,2)  NOT NULL,
    ""LineTotal""   numeric(18,2)  NOT NULL,
    ""Currency""    text           NOT NULL DEFAULT '',
    ""BaseType""    integer        NOT NULL,
    ""BaseEntry""   integer        NOT NULL,
    ""BaseLine""    integer        NOT NULL,
    ""TargetType""  integer        NOT NULL,
    ""TrgetEntry""  integer        NOT NULL,
    PRIMARY KEY (""DocEntry"", ""LineNum"")
);
CREATE INDEX IF NOT EXISTS ""IX_DeliveryLines_DocEntry""
    ON ""DeliveryLines"" (""DocEntry"");
CREATE INDEX IF NOT EXISTS ""IX_DeliveryLines_ItemCode""
    ON ""DeliveryLines"" (""ItemCode"");");

                        // Credit Memo cache — ORIN/RIN1 fast path (event-driven, no NeonSyncJob dependency)
                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""CreditMemoHeaders"" (
    ""DocEntry""   integer        NOT NULL PRIMARY KEY,
    ""DocNum""     integer        NOT NULL,
    ""CardCode""   text           NOT NULL,
    ""CardName""   text           NOT NULL,
    ""DocDate""    date           NOT NULL,
    ""DocDueDate"" date           NOT NULL,
    ""DocStatus""  text           NOT NULL,
    ""Canceled""   text           NOT NULL,
    ""DocTotal""   numeric(18,2)  NOT NULL,
    ""Comments""   text           NOT NULL DEFAULT '',
    ""SlpCode""    integer        NOT NULL,
    ""SlpName""    text           NOT NULL DEFAULT '',
    ""U_AppRef""   text,
    ""CreateDate"" date           NOT NULL,
    ""UpdateDate"" date           NOT NULL
);
CREATE INDEX IF NOT EXISTS ""IX_CreditMemoHeaders_CardCode""
    ON ""CreditMemoHeaders"" (""CardCode"");
CREATE INDEX IF NOT EXISTS ""IX_CreditMemoHeaders_DocDate""
    ON ""CreditMemoHeaders"" (""DocDate"");

CREATE TABLE IF NOT EXISTS ""CreditMemoLines"" (
    ""Id""         SERIAL         PRIMARY KEY,
    ""DocEntry""   integer        NOT NULL,
    ""LineNum""    integer        NOT NULL,
    ""ItemCode""   text           NOT NULL,
    ""Dscription"" text           NOT NULL DEFAULT '',
    ""Quantity""   numeric(18,4)  NOT NULL,
    ""Price""      numeric(18,2)  NOT NULL,
    ""LineTotal""  numeric(18,2)  NOT NULL,
    ""WhsCode""    text           NOT NULL,
    ""BaseType""   integer        NOT NULL,
    ""BaseEntry""  integer        NOT NULL,
    ""BaseLine""   integer        NOT NULL,
    UNIQUE (""DocEntry"", ""LineNum"")
);
CREATE INDEX IF NOT EXISTS ""IX_CreditMemoLines_DocEntry""
    ON ""CreditMemoLines"" (""DocEntry"");");

                        await neonDb.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""ReturnRequests"" (
    ""DocEntry""    integer        NOT NULL PRIMARY KEY,
    ""DocNum""      integer        NOT NULL,
    ""CardCode""    text           NOT NULL,
    ""CardName""    text           NOT NULL,
    ""DocDate""     date           NOT NULL,
    ""DocStatus""   text           NOT NULL,
    ""Canceled""    text           NOT NULL,
    ""DocTotal""    numeric(18,2)  NOT NULL,
    ""U_AppRef""    text,
    ""U_ReplitId""  text,
    ""Comments""    text           NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_CardCode""
    ON ""ReturnRequests"" (""CardCode"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_DocDate""
    ON ""ReturnRequests"" (""DocDate"");

CREATE TABLE IF NOT EXISTS ""ReturnRequestLines"" (
    ""Id""         SERIAL         PRIMARY KEY,
    ""DocEntry""   integer        NOT NULL,
    ""LineNum""    integer        NOT NULL,
    ""BaseType""   integer        NOT NULL,
    ""BaseEntry""  integer        NOT NULL,
    ""BaseLine""   integer        NOT NULL,
    ""ItemCode""   text           NOT NULL,
    ""Dscription"" text           NOT NULL DEFAULT '',
    ""Quantity""   numeric(18,4)  NOT NULL,
    ""OpenQty""    numeric(18,4)  NOT NULL,
    ""WhsCode""    text           NOT NULL,
    ""LineStatus"" text           NOT NULL,
    UNIQUE (""DocEntry"", ""LineNum"")
);
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_DocEntry""
    ON ""ReturnRequestLines"" (""DocEntry"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_BaseRef""
    ON ""ReturnRequestLines"" (""BaseType"", ""BaseEntry"", ""BaseLine"");");

                        // ReturnedQty on InvoiceLines — Option B line-level return tracking
                        await neonDb.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""ReturnedQty"" numeric(18,4) NOT NULL DEFAULT 0;
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""PendingReturnQty"" numeric(18,4) NOT NULL DEFAULT 0;
ALTER TABLE ""CreditMemoLines"" ADD COLUMN IF NOT EXISTS ""InvoiceDocEntry"" integer;
ALTER TABLE ""CreditMemoLines"" ADD COLUMN IF NOT EXISTS ""InvoiceLineNum"" integer;");

                        await neonDb.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoiceLines""
ADD COLUMN IF NOT EXISTS ""ReturnableQty"" numeric(18,4)
GENERATED ALWAYS AS (GREATEST(0, ""Quantity"" - ""ReturnedQty"" - ""PendingReturnQty"")) STORED;
");

                        // ZF Final Fulfillment Report tables
                        if (!string.IsNullOrWhiteSpace(molasCs))
                        {
                            try
                            {
                                var rptRepo = scope.ServiceProvider
                                    .GetService<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentReportRepository>();
                                if (rptRepo is not null)
                                    await rptRepo.EnsureTablesAsync();
                                logger.LogInformation("☁️ ZoneFulfillmentReports tables ensured.");
                            }
                            catch (Exception rptEx)
                            {
                                logger.LogWarning(rptEx, "⚠️ ZoneFulfillmentReports table init failed — reports disabled until next restart.");
                            }
                        }

                        // Invoice base-document references (additive — safe on existing DB)
                        await neonDb.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseType""  integer;
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseEntry"" integer;
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseLine""  integer;
UPDATE ""InvoiceLines"" SET ""BaseType"" = 0 WHERE ""BaseType"" IS NULL;
");

                        // Product price-list columns — EnsureCreatedAsync is a no-op on existing DBs,
                        // so we explicitly add these to an already-provisioned Neon Products table.
                        await neonDb.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Price01"" numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Price02"" numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Price04"" numeric(18,2) NOT NULL DEFAULT 0;
");
                        logger.LogInformation("☁️ Neon Products price-list columns (Price01/02/04) ensured.");

                        logger.LogInformation("☁️ Neon schema ready (Deliveries + DeliveryLines tables ensured.");
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

            var ps = app.Services.GetRequiredService<IOptions<SapReplitAPI.Models.PaymentSettings>>().Value;
            Log.Information("💳 Payment config — AdvanceCustomerPayments GL: {AdvGL}, DefaultBranchId: {BplId}",
                ps.AdvanceCustomerPayments, ps.DefaultBranchId);
        }

        // ── ZF Invoice automation startup validation (§27) ────────────────────
        {
            var invoiceHealth = app.Services
                .GetRequiredService<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentInvoiceStartupHealth>();
            var invoiceAutoEnabled = app.Configuration
                .GetValue<bool>("ZoneFulfillment:InvoiceAutomationEnabled", defaultValue: false);
            var molasStartupCs = app.Configuration.GetConnectionString("MolasIntegration") ?? "";

            Log.Information("🧾 ZF Invoice: InvoiceAutomationEnabled={Enabled}", invoiceAutoEnabled);

            if (string.IsNullOrWhiteSpace(molasStartupCs))
            {
                invoiceHealth.InvoiceRecordAvailable = false;
                invoiceHealth.ProbeMessage = "MolasIntegration connection string not configured.";
                Log.Warning("⚠️ ZF Invoice startup: MolasIntegration not configured — auto-invoicing disabled.");
            }
            else
            {
                try
                {
                    using var probeConn = new Microsoft.Data.SqlClient.SqlConnection(molasStartupCs);
                    probeConn.Open();
                    using var probeCmd = probeConn.CreateCommand();
                    probeCmd.CommandText =
                        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES " +
                        "WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='InvoiceRecord'";
                    var tableCount = (int)probeCmd.ExecuteScalar()!;
                    if (tableCount == 0)
                    {
                        invoiceHealth.InvoiceRecordAvailable = false;
                        invoiceHealth.ProbeMessage = "dbo.InvoiceRecord not found in MolasIntegration.";
                        Log.Fatal("❌ ZF Invoice startup: dbo.InvoiceRecord NOT FOUND — auto-invoicing disabled.");
                    }
                    else
                    {
                        invoiceHealth.InvoiceRecordAvailable = true;
                        invoiceHealth.ProbeMessage = "dbo.InvoiceRecord verified at startup.";
                        Log.Information("✅ ZF Invoice startup: dbo.InvoiceRecord verified.");
                    }
                }
                catch (Exception probeEx)
                {
                    invoiceHealth.InvoiceRecordAvailable = false;
                    invoiceHealth.ProbeMessage = $"Startup probe failed: {probeEx.Message}";
                    Log.Fatal(probeEx, "❌ ZF Invoice startup: schema probe failed — auto-invoicing disabled.");
                }
            }
        }

        // ── ZfReplanOperation table (ensure exists) ───────────────────────────
        {
            var replanCs = app.Configuration.GetConnectionString("MolasIntegration") ?? "";
            if (!string.IsNullOrWhiteSpace(replanCs))
            {
                try
                {
                    using var replanScope = app.Services.CreateScope();
                    var zfRepo = replanScope.ServiceProvider
                        .GetRequiredService<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentRepository>();
                    zfRepo.EnsureZfReplanOperationTableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Log.Information("✅ ZF replan: dbo.ZfReplanOperation verified/created.");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "⚠️ ZF replan: EnsureZfReplanOperationTableAsync failed — replan durability unavailable.");
                }
            }
        }

        // ── ZfAdminAuditLog table (ensure exists) ─────────────────────────────
        {
            var auditCs = app.Configuration.GetConnectionString("MolasIntegration") ?? "";
            if (!string.IsNullOrWhiteSpace(auditCs))
            {
                try
                {
                    using var auditScope = app.Services.CreateScope();
                    var auditRepo = auditScope.ServiceProvider
                        .GetRequiredService<SapReplitAPI.Services.ZoneFulfillment.ZfAdminAuditRepository>();
                    auditRepo.EnsureTableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Log.Information("✅ ZF admin: dbo.ZfAdminAuditLog verified/created.");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "⚠️ ZF admin: EnsureTableAsync failed — audit log unavailable.");
                }
            }
        }

        // ── ZfIncidentResolutions table (ensure exists — MolasIntegration SQL Server) ───────
        {
            try
            {
                var incidentRepo = app.Services
                    .GetRequiredService<SapReplitAPI.Services.ZoneFulfillment.ZfIncidentResolutionRepository>();
                incidentRepo.EnsureTableAsync(CancellationToken.None).GetAwaiter().GetResult();
                Log.Information("✅ ZF Phase 4: ZfIncidentResolutions table verified/created.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ ZF Phase 4: ZfIncidentResolutions EnsureTableAsync failed — resolution history unavailable.");
            }
        }

        // ── ZfIncidentResolutions.ResolutionRequestId column (Phase 2 idempotency) ───────────
        {
            try
            {
                var incidentRepo = app.Services
                    .GetRequiredService<SapReplitAPI.Services.ZoneFulfillment.ZfIncidentResolutionRepository>();
                incidentRepo.EnsureResolutionRequestIdColumnAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ ZF Phase 2: EnsureResolutionRequestIdColumnAsync failed (non-fatal).");
            }
        }

        // ── ProductPriceAuditLog table (ensure exists) ────────────────────────────
        {
            var ppAuditCs = app.Configuration.GetConnectionString("MolasIntegration") ?? "";
            if (!string.IsNullOrWhiteSpace(ppAuditCs))
            {
                try
                {
                    using var ppScope = app.Services.CreateScope();
                    var ppRepo = ppScope.ServiceProvider
                        .GetRequiredService<SapReplitAPI.Services.ProductAdmin.ProductPriceAuditRepository>();
                    ppRepo.EnsureTableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Log.Information("✅ Product admin: dbo.ProductPriceAuditLog verified/created.");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "⚠️ Product admin: ProductPriceAuditLog table init failed — audit log unavailable.");
                }
            }
        }

        // ── FulfillmentRequestLine.UnitPrice → nullable (price-override migration) ──
        {
            var priceColCs = app.Configuration.GetConnectionString("MolasIntegration") ?? "";
            if (!string.IsNullOrWhiteSpace(priceColCs))
            {
                try
                {
                    using var priceScope = app.Services.CreateScope();
                    var zfRepo = priceScope.ServiceProvider
                        .GetRequiredService<SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentRepository>();
                    zfRepo.EnsureFulfillmentRequestLineUnitPriceNullableAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Log.Information("✅ ZF price: FulfillmentRequestLine.UnitPrice column is nullable.");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "⚠️ ZF price: EnsureFulfillmentRequestLineUnitPriceNullableAsync failed.");
                }
            }
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

            await next(context);
        });

        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseCors("AllowAll");

        // Middleware-driven maintenance entrypoint for returns mirror backfill.
        // Guarded by the same X-API-Key used by controller endpoints.
        app.Use(async (context, next) =>
        {
            if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && context.Request.Path.Equals("/internal/returns/backfill", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = context.RequestServices
                    .GetRequiredService<IOptions<ApiSecuritySettings>>().Value;
                if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                {
                    var supplied = context.Request.Headers["X-API-Key"].ToString();
                    if (!string.Equals(supplied, cfg.ApiKey, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsync("Unauthorized");
                        return;
                    }
                }

                using var scope = context.RequestServices.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SapReplitAPI.Services.Returns.ReturnsMirrorBackfillService>();
                var result = await svc.RunAsync();

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = result.Errors.Count == 0
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(result));
                return;
            }

            await next(context);
        });

        app.UseStaticFiles();  // serves wwwroot/ — required for /zf-diagnosis/
        app.UseAuthorization();
        app.MapControllers();

        // ── SAP UDF bootstrap — runs after server is ready, non-blocking ────────
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)); // let SAP COM warm up
                try
                {
                    using var scope = app.Services.CreateScope();
                    var sap = scope.ServiceProvider.GetRequiredService<SapService>();
                    sap.EnsureIncomingPaymentUdfs();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "⚠️ SAP UDF setup failed — U_ClientRef will be written on next restart.");
                }
            });
        });

        var hostIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
    .AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        Log.Information("✅ SAP Replit API reachable at http://{ip}:7121", hostIp);
        app.Run();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Host terminated unexpectedly.");
    if (args.Contains("--backfill-returned-qty") || args.Contains("--verify-invoice-returns") || args.Contains("--backfill-returns-mirror"))
        Environment.ExitCode = 1;
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

static async Task EnsureReturnsSchemaForMaintenanceAsync(IServiceProvider services, IConfiguration configuration)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();

    // Keep maintenance mode aligned with startup schema bootstrap for returns mirror paths.
    var cs = configuration.GetConnectionString("CacheDB") ?? "";
    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs);
    var dir = Path.GetDirectoryName(csb.DataSource);
    if (!string.IsNullOrWhiteSpace(dir))
        Directory.CreateDirectory(dir);

    if (OperatingSystem.IsWindows())
        SetPragmaWal(db);

    await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""ReturnRequests"" (
    ""DocEntry""    INTEGER PRIMARY KEY,
    ""DocNum""      INTEGER NOT NULL,
    ""CardCode""    TEXT    NOT NULL,
    ""CardName""    TEXT    NOT NULL,
    ""DocDate""     TEXT    NOT NULL,
    ""DocStatus""   TEXT    NOT NULL,
    ""Canceled""    TEXT    NOT NULL,
    ""DocTotal""    REAL    NOT NULL,
    ""U_AppRef""    TEXT,
    ""U_ReplitId""  TEXT,
    ""Comments""    TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_CardCode"" ON ""ReturnRequests"" (""CardCode"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_DocDate"" ON ""ReturnRequests"" (""DocDate"");

CREATE TABLE IF NOT EXISTS ""ReturnRequestLines"" (
    ""Id""         INTEGER PRIMARY KEY AUTOINCREMENT,
    ""DocEntry""   INTEGER NOT NULL,
    ""LineNum""    INTEGER NOT NULL,
    ""BaseType""   INTEGER NOT NULL,
    ""BaseEntry""  INTEGER NOT NULL,
    ""BaseLine""   INTEGER NOT NULL,
    ""ItemCode""   TEXT    NOT NULL,
    ""Dscription"" TEXT    NOT NULL DEFAULT '',
    ""Quantity""   REAL    NOT NULL,
    ""OpenQty""    REAL    NOT NULL,
    ""WhsCode""    TEXT    NOT NULL,
    ""LineStatus"" TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""UX_ReturnRequestLines_DocEntry_LineNum"" ON ""ReturnRequestLines"" (""DocEntry"", ""LineNum"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_DocEntry"" ON ""ReturnRequestLines"" (""DocEntry"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_BaseRef"" ON ""ReturnRequestLines"" (""BaseType"", ""BaseEntry"", ""BaseLine"");
");

    try { await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""ReturnedQty"" REAL NOT NULL DEFAULT 0"); }
    catch { }
    try { await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""PendingReturnQty"" REAL NOT NULL DEFAULT 0"); }
    catch { }
    try { await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""BaseType"" INTEGER NOT NULL DEFAULT 0"); }
    catch { }
    try { await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""BaseEntry"" INTEGER"); }
    catch { }
    try { await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN ""BaseLine"" INTEGER"); }
    catch { }

    var neonCs = configuration.GetConnectionString("NeonDb");
    if (string.IsNullOrWhiteSpace(neonCs))
        return;

    var neon = scope.ServiceProvider.GetService<NeonDbContext>();
    if (neon is null)
        return;

    await neon.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""ReturnRequests"" (
    ""DocEntry""    integer        NOT NULL PRIMARY KEY,
    ""DocNum""      integer        NOT NULL,
    ""CardCode""    text           NOT NULL,
    ""CardName""    text           NOT NULL,
    ""DocDate""     date           NOT NULL,
    ""DocStatus""   text           NOT NULL,
    ""Canceled""    text           NOT NULL,
    ""DocTotal""    numeric(18,2)  NOT NULL,
    ""U_AppRef""    text,
    ""U_ReplitId""  text,
    ""Comments""    text           NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_CardCode""
    ON ""ReturnRequests"" (""CardCode"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequests_DocDate""
    ON ""ReturnRequests"" (""DocDate"");

CREATE TABLE IF NOT EXISTS ""ReturnRequestLines"" (
    ""Id""         SERIAL         PRIMARY KEY,
    ""DocEntry""   integer        NOT NULL,
    ""LineNum""    integer        NOT NULL,
    ""BaseType""   integer        NOT NULL,
    ""BaseEntry""  integer        NOT NULL,
    ""BaseLine""   integer        NOT NULL,
    ""ItemCode""   text           NOT NULL,
    ""Dscription"" text           NOT NULL DEFAULT '',
    ""Quantity""   numeric(18,4)  NOT NULL,
    ""OpenQty""    numeric(18,4)  NOT NULL,
    ""WhsCode""    text           NOT NULL,
    ""LineStatus"" text           NOT NULL,
    UNIQUE (""DocEntry"", ""LineNum"")
);
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_DocEntry""
    ON ""ReturnRequestLines"" (""DocEntry"");
CREATE INDEX IF NOT EXISTS ""IX_ReturnRequestLines_BaseRef""
    ON ""ReturnRequestLines"" (""BaseType"", ""BaseEntry"", ""BaseLine"");
");

    await neon.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""ReturnedQty"" numeric(18,4) NOT NULL DEFAULT 0;
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""PendingReturnQty"" numeric(18,4) NOT NULL DEFAULT 0;
");

    await neon.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoiceLines""
ADD COLUMN IF NOT EXISTS ""ReturnableQty"" numeric(18,4)
GENERATED ALWAYS AS (GREATEST(0, ""Quantity"" - ""ReturnedQty"" - ""PendingReturnQty"")) STORED;
");

    await neon.Database.ExecuteSqlRawAsync(@"
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseType""  integer;
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseEntry"" integer;
ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseLine""  integer;
UPDATE ""InvoiceLines"" SET ""BaseType"" = 0 WHERE ""BaseType"" IS NULL;
");
}
