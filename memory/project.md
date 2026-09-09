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

## Zone Fulfillment (ZF) — end-to-end flow (verified against master 5be7d4c, 2026-09-09)
Route prefix `api/zone-fulfillment/experimental`; every action requires header `X-Zone-Experimental: true`
(returns 403 via `StatusCode(403)`, never `Forbid()`). State lives in MolasIntegration (SQL Server), not SQLite.

| Step | Trigger | Service → SAP object | Persistence (MolasIntegration) |
|------|---------|----------------------|--------------------------------|
| 1. Order | `POST /orders` | `ZoneFulfillmentOrchestrationService` → `ZoneAllocationEngine` (OITW, zone priority) → `SapService.CreateZoneFulfillmentOrder` (ORDR, one RDR1 line per fragment, UDFs U_ZoneRef/U_ReplitId/U_DeliveryLocation) | FulfillmentOrchestration, FulfillmentRequestLine, AllocationPlan, SoLineFragment |
| 2. Pick lists | automatic after ORDR.Add (`AUTO_PICK_LIST_ENABLED`), or admin `POST /orders/{id}/pick-lists` | `ZoneFulfillmentPickListService` → `PickerResolutionService` (OwnerCode) → `CreateZoneFulfillmentPickListMultiLine` (one OPKL per WhsCode) | PickListRecord (PLR) + PickListFragmentRecord (PLFR), Status=Created |
| 3. Confirm pick | `POST .../pick-lists/{absEntry}/pick` (full qty only) | `ExecutePickAsync` → OIBQ bins → `UpdateZoneFulfillmentPickList` (pl.Update, BinAllocations, OBBQ recheck) | PLR/PLFR PickedQty + Status=Picked |
| 4. Delivery | automatic from `ZoneFulfillmentAutomationService` when every fragment's latest PLR is Picked; admin `POST /orders/{id}/delivery` | `ZoneFulfillmentDeliveryService` (preflight → Pending record → live recheck → `CreateZoneFulfillmentDelivery`, ONE ODLN, durable PKL2 bins) | DeliveryRecord, DeliveryFragmentRecord(+Bins), SoLineFragment.DeliveredQty; orchestration → Delivered |
| 5. Invoice | automatic right after DELIVERY_CREATED; admin `POST /orders/{id}/invoice` | `ZoneFulfillmentInvoiceService` (7-gate preflight, SAP-first OINV search, `CreateZoneFulfillmentInvoice` BaseType=15) — gated by `ZoneFulfillment:InvoiceAutomationEnabled` + startup probe | InvoiceRecord (Pending → Created/Failed) |
| Fallback | Quartz `ZoneFulfillmentPickReconciliationJob` every 30 s | `ZoneFulfillmentPickReconciliationService`: Accepted orchestrations with PLR≠Picked >90 s → read OPKL/PKL1; if SAP says picked, mark PLR Picked and run step 4/5 | same as 3–5 |

Concurrency: `OrderAllocationCoordinator` (global) around allocate+ORDR.Add; `ZoneFulfillmentDeliveryCoordinator`
(per RequestId) around delivery and pick reconciliation; static per-ODLN semaphore in the invoice service.
Legacy jobs skip ZF docs: `GetOpenDeliveries` filters `U_ZoneRef <> 'ZoneFulfillment'` so `InvoiceFromDeliveryJob` never invoices a ZF ODLN.
