# Project: SapReplitAPI

## Overview
Windows service (.NET 8, `net8.0`, `win-x64`) that bridges SAP Business One (via DI-API COM)
with a REST API. Uses a local SQLite cache (`productcache.db`) for performance, and syncs
data from SAP on a schedule via Quartz jobs.

## Stack
| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8 Windows Service |
| Web | ASP.NET Core + Swashbuckle (Swagger) |
| ORM | EF Core 9.0.7 + SQLite provider |
| Scheduling | Quartz.Extensions.Hosting 3.14 |
| Logging | Serilog (Console + File sinks) |
| SAP | SAPbobsCOM (DI-API, COM interop) |
| External DB | Neon (PostgreSQL via Npgsql EF provider) |

## Key Files
| File | Purpose |
|------|---------|
| `Program.cs:190` | `database.Migrate()` — startup DB migration |
| `Services/Cached Services/CacheDbContext.cs` | SQLite EF Core context, all entity configs |
| `Services/Cached Services/OrderCacheService.cs` | Order sync: `FullSyncOrdersAsync`, `SyncOrdersDeltaAsync`, raw SQL UPSERT |
| `Services/Cached Services/CustomerCacheService.cs` | Customer cache: raw SQL UPSERT |
| `Services/SapService.cs` | All SAP DI-API calls: CreateCustomer, GetAllOrders, etc. |
| `Jobs/OrderDeltaSyncJob.cs` | Quartz job → calls `SyncOrdersDeltaAsync` |
| `Migrations/CacheDbContextModelSnapshot.cs` | EF model snapshot — must stay in sync |

## Database
- SQLite file: `C:\CacheDbs\SapReplit\productcache.db`
- WAL mode enabled at startup
- Tables: Products, Invoices, InvoiceLines, InvoicePayments, SyncMetadata,
  Customers, OrderHeaders, OrderLines, SalesTargets, Users,
  TodayOrderHeaders, TodayOrderLines, OpenOrderHeaders, OpenOrderLines,
  DetailedInvoiceStatusCache

## Project Settings
- `<Nullable>enable</Nullable>` — NRTs active; non-nullable `string` = required by ASP.NET Core model binding
- `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` — Windows only (SAP DI-API is Windows COM)
- `<PublishSingleFile>true</PublishSingleFile>`

## Build
Use VS MSBuild, NOT `dotnet build` — the SAPbobsCOM COM reference requires the .NET Framework MSBuild:
`& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" SapReplitAPI.csproj /t:Build /p:Configuration=Debug`

## Event-driven Outbox Pipeline (Phase 1 — Steps 1–13 complete as of 2026-08-26)
- **SapEventOutbox** on MolasIntegration SQL Server — outbox table for SAP SP notifications
- **OutboxClaimService** — singleton; SqlClient per method; claim/mark-done/mark-failed/reset-orphaned
- **OutboxPollerService** — singleton BackgroundService; 1-second poll; IServiceScopeFactory per event
- **Handlers**: InvoiceEventHandler (13/A), CreditMemoEventHandler (14/A), IncomingPaymentEventHandler (24/A+C)
- **NeonEventWriteService** — targeted Neon writes (single tx per call); MUST NOT touch SyncMetadata or NeonMirror watermarks
- **Registration guard**: OutboxPoller only registers when `ConnectionStrings:MolasIntegration` is non-empty; handlers only register when both MolasIntegration AND NeonDb are present
- **`ConnectionStrings:MolasIntegration`** placeholder (`""`) in appsettings.json; real value via `ConnectionStrings__MolasIntegration` env var on host — never commit real credentials
- **Steps 14–17 pending**: SQL scripts deployment, SP update (C_UPDATE_SP.sql), app deploy, runtime tests
