# SapReplitAPI

A .NET 8 Web API middleware layer between **SAP Business One** (COM interop / DI-API) and external clients. It maintains a local SQLite cache of SAP data, mirrors selected tables to a cloud PostgreSQL database (Neon), queues orders offline when SAP is unreachable, and automates invoice creation from open deliveries.

---

## Table of Contents

- [Architecture](#architecture)
- [Tech Stack](#tech-stack)
- [Prerequisites](#prerequisites)
- [Configuration](#configuration)
- [Security](#security)
- [Running the Application](#running-the-application)
- [API Endpoints](#api-endpoints)
- [Background Jobs](#background-jobs)
- [Offline Order Queue](#offline-order-queue)
- [Invoice Automation](#invoice-automation)
- [Neon PostgreSQL Mirror](#neon-postgresql-mirror)
- [Database Schema (SQLite)](#database-schema-sqlite)
- [Project Structure](#project-structure)
- [Known Constraints](#known-constraints)

---

## Architecture

```
SAP Business One (COM / DI-API)
          │
          ▼
  ┌─────────────────────────────────────────────────────┐
  │                 SapReplitAPI (.NET 8)                │
  │                                                     │
  │  Controllers  ◄── HTTP clients (Replit / Frontend)  │
  │       │                                             │
  │  SapService (COM interop)                           │
  │       │                                             │
  │  Quartz Jobs ──► SQLite Cache (primary store)       │
  │                       │  (optional)                 │
  │               Neon PostgreSQL (cloud mirror)        │
  └─────────────────────────────────────────────────────┘
          │
          ▼
     ngrok tunnel → public HTTPS URL
```

- Runs on **port 5050** (Kestrel), exposed via **ngrok**.
- Can also run as a **Windows Service**.
- All SAP writes/reads go through `SapService` (single COM connection, auto-reconnect on COMException).
- SQLite is the primary cache; Neon is an optional cloud mirror.
- If SAP is unreachable when an order is submitted, the order is saved to Neon (`PendingOrders`) and retried automatically every 15 seconds by `PendingOrderSyncJob`.

---

## Tech Stack

| Component        | Technology                                    |
|------------------|-----------------------------------------------|
| Framework        | ASP.NET Core 8.0                              |
| SAP Integration  | SAPbobsCOM (COM Interop, Windows-only)        |
| Primary Cache    | SQLite via EF Core 9 + Microsoft.Data.Sqlite  |
| Cloud Mirror     | PostgreSQL on Neon (serverless) via Npgsql    |
| Job Scheduler    | Quartz.NET 3.14 (`[DisallowConcurrentExecution]`) |
| Logging          | Serilog (Console + rolling File sink)         |
| API Docs         | Swagger / Swashbuckle                         |
| Tunnel           | ngrok                                         |

---

## Prerequisites

- **Windows** — SAP COM interop is Windows-only
- SAP Business One client installed on the same machine (provides `SAPbobsCOM.dll`)
- .NET 8 SDK
- SAP B1 license server accessible from the host machine
- Visual Studio 2022 (required to build — `dotnet build` cannot resolve COM references)

---

## Configuration

### Environment Variables (machine-level — never in appsettings)

```
# SAP connection
SAP__Server=<hostname>
SAP__CompanyDB=<database name>
SAP__UserName=<sap username>          ← MUST be machine-level env var
SAP__Password=<sap password>          ← MUST be machine-level env var
SAP__LicenseServer=<host:30000>
SAP__SLDServer=<host:40000>

# API key (protects /api/payments/incoming and all /api/accounts endpoints)
ApiSecurity__ApiKey=<strong-random-key>
```

### appsettings.json (non-secret values only)

```json
{
  "ConnectionStrings": {
    "CacheDB": "Data Source=F:\\AutohubCaches\\productcache.db;Mode=ReadWriteCreate;",
    "NeonDb":  "Host=...;Database=...;Username=...;Password=...;SSL Mode=Require;"
  }
}
```

> **Critical:** Do NOT add `Cache=Shared` to the SQLite connection string — it causes a silent in-memory fallback on Windows, resulting in "no such table" errors at runtime.

> `NeonDb` is optional. If absent, `NeonSyncJob`, `PendingOrderSyncJob`, and the Neon mirror are automatically disabled.

### ngrok

Edit `ngrok.yml` with your authtoken and desired hostname, then:

```bash
ngrok.exe start sapmiddleware
```

---

## Security

| Mechanism         | Applies to                                              | How configured                        |
|-------------------|---------------------------------------------------------|---------------------------------------|
| API Key (`X-Api-Key` header) | `POST /api/payments/incoming`<br>All `/api/accounts` endpoints | `ApiSecurity__ApiKey` env var          |
| SAP credentials   | Internal only (never exposed via API)                   | `SAP__UserName` / `SAP__Password` env vars |
| Idempotency keys  | Payments (`clientReference`), Orders (`ReplitId`)       | SQLite `PaymentIdempotencyLogs` table  |

> `appsettings.Development.json` must NOT contain an `ApiSecurity` section — empty strings override production values.

---

## Running the Application

### Development

Open in Visual Studio 2022 and press **F5**, or use the `https` launch profile:

```bash
# Requires VS MSBuild (not dotnet CLI) due to COM references
# Use launchSettings.json profile: "https"
```

### As a Windows Service

```powershell
sc create SapReplitAPI binPath="C:\path\to\SapReplitAPI.exe"
sc start SapReplitAPI
```

### Logs

Written to `C:\SAPLogs\app.log` (rolling daily, 14-day retention).

---

## API Endpoints

Swagger UI available at `/swagger` in Development mode.

### Products — `/api/products`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/products` | Paged product list from SQLite cache |
| GET | `/api/products/search` | Search by keyword, item code, manufacturer |
| GET | `/api/products/sync-status` | Last sync timestamp |
| POST | `/api/products/sync` | Trigger full product sync from SAP |

---

### Customers — `/api/customers`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/customers` | All cached customers |
| GET | `/api/customers/search` | Search by CardCode, CardName, phone |
| GET | `/api/customers/{cardCode}` | Single customer detail |
| POST | `/api/customers` | Create new customer in SAP |

---

### Orders — `/api/orders`

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/orders` | Create order in SAP; falls back to PendingOrders if offline |
| PUT | `/api/orders/{docEntry}` | Update existing SAP order |
| POST | `/api/orders/quotations` | Create quotation in SAP |
| GET | `/api/orders/headers/today` | Today's order headers from cache |
| GET | `/api/orders/headers/search-open-headers` | Search open order headers |
| GET | `/api/orders/lines/today` | Today's order lines from cache |
| GET | `/api/orders/lines/search-open-lines` | Search open order lines |
| POST | `/api/orders/sync-full` | Trigger full order cache refresh (background) |

---

### Local (Draft) Orders — `/api/orders/local`

Build an order incrementally before submitting to SAP. Uses Neon `PendingOrders` table.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/orders/local` | Create draft order (status: Draft) |
| GET | `/api/orders/local/{replitId}` | Get draft with lines |
| PUT | `/api/orders/local/{replitId}/lines` | Upsert a line on the draft |
| DELETE | `/api/orders/local/{replitId}/lines/{lineNum}` | Remove a line from the draft |
| POST | `/api/orders/local/{replitId}/submit` | Submit draft → SAP (or queue as Pending if offline) |
| DELETE | `/api/orders/local/{replitId}` | Cancel/delete draft |
| GET | `/api/orders/local/pending` | List all non-draft orders (`?status=Pending\|Failed\|Synced`) |
| POST | `/api/orders/local/pending/{id}/retry` | Reset a Failed order back to Pending |

**Status flow:** `Draft → Pending → Synced` (or `Failed` after 8 retries, or `Cancelled`)

---

### Payments — `/api/payments`

`POST /api/payments/incoming` requires `X-Api-Key` header.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/payments/incoming` | ⚠️ API-key protected. Record incoming payment in SAP |
| GET | `/api/payments/invoices` | Paginated invoice list from cache |
| GET | `/api/payments/invoices/{docEntry}` | Single invoice detail |
| GET | `/api/payments/invoices/{docEntry}/lines` | Invoice line items |
| GET | `/api/payments/invoices/{docEntry}/payments` | Payments applied to an invoice |
| GET | `/api/payments/customers/{cardCode}/advance-balance` | Customer advance payment balance |

**Payment channels (`paymentChannel` field):**

| Value | GL Account | Description |
|-------|-----------|-------------|
| `CashOnHand` | 100000 | Physical cash |
| `MPesaLipa` | 164000 | M-Pesa Lipa Na M-Pesa |
| `TigoLipa` | 165000 | Tigo Pesa |
| `CRDB` | 166000 | CRDB bank transfer |
| `AALNMB` | 167000 | NMB bank transfer |
| `AdvanceCustomerPayments` | 202010 | Customer advance/deposit |

---

### Accounts (GL Statements) — `/api/accounts`

All endpoints require `X-Api-Key` header.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/accounts/statement` | GL account statement (`?account=163000&from=&to=`) |
| GET | `/api/accounts/balance` | Balance summary for an account |

Valid GL accounts: `163000`, `164000`, `165000`, `166000`, `167000`, `202010`, `202011`

---

### Invoices — `/api/invoices`

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/invoices/from-deliveries` | Manually trigger invoice creation from all open ODLN deliveries |

Body:
```json
{ "docDueDate": "2026-08-31" }
```
`docDueDate` is required. It is used as the fallback payment due date when the delivery itself has no `DocDueDate`; otherwise the delivery's own `DocDueDate` takes precedence.

---

### Dashboard — `/api/dashboard`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/dashboard/mtd-sales` | Month-to-date sales total |
| GET | `/api/dashboard/sales-range` | Sales total for a date range |
| GET | `/api/dashboard/cash-vs-credit` | Cash vs credit split |
| GET | `/api/dashboard/average-order-value` | Average order value MTD |
| GET | `/api/dashboard/unpaid-orders` | Open/unpaid invoice count and total |
| GET | `/api/dashboard/revenue-trend` | Daily revenue for last N days |
| GET | `/api/dashboard/open-docs` | Open sales orders and deliveries count |
| *(+ 8 more)* | | Slp/team breakdowns, top customers, etc. |

---

### Today's Orders — `/api/today-orders`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/today-orders/headers` | All order headers for today |
| GET | `/api/today-orders/lines` | All order lines for today |

---

### Open Orders — `/api/open-orders`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/open-orders/headers` | All open (un-closed) order headers |
| GET | `/api/open-orders/lines` | All open order lines |

---

### Users — `/api/users`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/users` | All users |
| POST | `/api/users` | Create user |
| PUT | `/api/users/{id}` | Update user |
| DELETE | `/api/users/{id}` | Delete user |
| POST | `/api/users/login` | Authenticate (returns user info) |

---

## Background Jobs

All jobs use `[DisallowConcurrentExecution]`. Cron uses Quartz syntax (second minute hour …).

| Job | Cron | Description |
|-----|------|-------------|
| `InvoiceDeltaSyncJob` | `0 0/5 * * * ?` | Sync new/changed invoices SAP → SQLite (every 5 min) |
| `SyncTodayOrdersJob` | `0 0/3 * * * ?` | Sync today's orders SAP → SQLite (every 3 min) |
| `OrderDeltaSyncJob` | `0 2/5 * * * ?` | Sync recent orders SAP → SQLite (every 5 min) |
| `AccountStatementSyncJob` | `0 0/5 * * * ?` | Sync GL account entries SAP → SQLite (every 5 min) |
| `SyncOpenOrdersJob` | `0 4/10 * * * ?` | Sync open orders SAP → SQLite (every 10 min) |
| `ProductDeltaSyncJob` | `0 3/15 * * * ?` | Sync changed products SAP → SQLite (every 15 min) |
| `NeonSyncJob` | `0 2/3 * * * ?` | Mirror SQLite cache → Neon PostgreSQL (every 3 min) |
| **`PendingOrderSyncJob`** | `0/15 * * * * ?` | Retry Pending offline orders against SAP (every 15 sec) |
| **`InvoiceFromDeliveryJob`** | `0 0 6-20 * * ?` | Auto-invoice all open ODLN deliveries (hourly 06:00–20:00) |
| `CustomerFullSyncJob` | `0 0 1 * * ?` | Full customer resync SAP → SQLite (daily 01:00) |
| `ProductFullSyncJob` | `0 0 2 * * ?` | Full product resync (daily 02:00) |
| `OrderFullSyncJob` | `0 0 3 * * ?` | Full order history resync (daily 03:00) |
| `InvoiceFullSyncJob` | `0 0 4 * * ?` | Full invoice history resync (daily 04:00) |
| `InvoiceStatusCacheJob` | `0 15 5 * * ?` | Rebuild invoice-status cache (daily 05:15) |

> `NeonSyncJob` and `PendingOrderSyncJob` are only registered when `NeonDb` connection string is present.

---

## Offline Order Queue

When SAP is unreachable (COMException or connection error), orders are not lost — they are saved to the Neon `PendingOrders` table and retried automatically.

```
POST /api/orders  or  POST /api/orders/local/{id}/submit
          │
          ├─ SAP available → DocEntry returned → Status: Synced
          │
          └─ SAP unavailable → Saved to Neon PendingOrders → 202 Accepted
                    │
                    └─ PendingOrderSyncJob (every 15s)
                              │
                              ├─ SAP up → CreateOrder → Status: Synced
                              │
                              └─ SAP still down → Exponential backoff
                                   Delays: 30s, 60s, 2m, 5m, 10m, 20m, 30m, 60m
                                   After 8 failures → Status: Failed
                                   Manual reset: POST /api/orders/local/pending/{id}/retry
```

**ReplitId** format: `OR-` + 12 uppercase hex chars (e.g. `OR-A3F72B1C9E4D`) — used as idempotency key in SAP order U_ReplitId field.

---

## Invoice Automation

`InvoiceFromDeliveryJob` runs every hour from **06:00 to 20:00** and converts all open delivery notes (ODLN) in SAP into AR invoices (OINV).

**Rules:**
- Only processes deliveries with `DocStatus = 'O'` and `CANCELED = 'N'`
- **Idempotency:** a delivery is only invoiced once — subsequent runs skip it if a `Success` log row exists
- **DocDueDate:** inherited from `ODLN.DocDueDate`; if null on the delivery, uses the value from the API request body; skipped with status `Skipped` if both are null
- **Currency:** inherited from the delivery (`DocCur`) — invoice currency always matches delivery currency
- **Locked documents:** detected by error message; logged as `Locked` (not `Failed`) and retried next hour
- **Numbering series:** uses SAP default for OINV (no series override)
- **Audit log:** every attempt (success or failure) is written to `InvoiceFromDeliveryLogs` (SQLite)

**SAP document flow:**
```
Sales Order (ORDR) → Delivery Note (ODLN) → AR Invoice (OINV)
                                                    ↑
                                         InvoiceFromDeliveryJob
                                    (BaseType=15, BaseEntry, BaseLine)
```

---

## Neon PostgreSQL Mirror

Tables synced to Neon (cloud PostgreSQL, molasuatohub schema):

| Table | Source | Sync method |
|-------|--------|-------------|
| `InvoiceHeaders` | SAP OINV | `NeonSyncJob` (watermark-based) |
| `InvoiceLines` | SAP INV1 | `NeonSyncJob` |
| `InvoicePayments` | SAP ORCT/OVPM | `NeonSyncJob` |
| `AccountStatements` | SAP JDT1 | `NeonSyncJob` |
| `PendingOrders` | Local (offline queue) | Written directly by API and `PendingOrderSyncJob` |
| `PendingOrderLines` | Local (offline queue) | Written directly by API |

Connection: pooler endpoint (`-pooler.` in hostname) — required for Neon serverless free tier.

---

## Database Schema (SQLite)

All tables are created at startup via `CREATE TABLE IF NOT EXISTS` DDL in `Program.cs`. No EF migrations are used for manually-managed tables.

| Table | Purpose |
|-------|---------|
| `Products` | Cached SAP item master |
| `Invoices` | Cached SAP AR invoice headers |
| `InvoiceLines` | Cached SAP AR invoice lines |
| `InvoicePayments` | Payments applied to invoices |
| `OrderHeaders` | Cached open order headers |
| `OrderLines` | Cached order lines |
| `TodayOrderHeaders` | Today's order headers (fast refresh) |
| `TodayOrderLines` | Today's order lines |
| `OpenOrderHeaders` | Currently open orders |
| `OpenOrderLines` | Lines for open orders |
| `Customers` | Cached SAP business partners |
| `AccountStatements` | GL journal entries (JDT1) |
| `PaymentIdempotencyLogs` | Prevents duplicate incoming payment posts |
| `InvoiceFromDeliveryLogs` | Audit log for delivery → invoice automation |
| `SyncMetadata` | Watermark timestamps per sync type |
| `SalesTargets` | Salesperson quarterly targets |
| `Users` | App users (auth) |

---

## Project Structure

```
SapReplitAPI/
├── Controllers/
│   ├── AccountsController.cs        # GL statements (API-key protected)
│   ├── CustomersController.cs
│   ├── DashboardController.cs
│   ├── InvoicesController.cs        # Invoice-from-delivery trigger
│   ├── LocalOrdersController.cs     # Draft + offline order queue
│   ├── OpenOrdersController.cs
│   ├── OrdersController.cs
│   ├── PaymentsController.cs        # API-key protected for incoming
│   ├── ProductsController.cs
│   ├── TodayOrdersController.cs
│   └── UsersController.cs
├── Filters/
│   └── ApiKeyAuthFilter.cs
├── Jobs/
│   ├── AccountStatementSyncJob.cs
│   ├── CustomerFullSyncJob.cs
│   ├── InvoiceFromDeliveryJob.cs    # Hourly 06:00–20:00
│   ├── InvoiceDeltaSyncJob.cs
│   ├── InvoiceFullSyncJob.cs
│   ├── InvoiceStatusCacheJob.cs
│   ├── NeonSyncJob.cs
│   ├── OrderDeltaSyncJob.cs
│   ├── OrderFullSyncJob.cs
│   ├── PendingOrderSyncJob.cs       # Every 15s — offline order retry
│   ├── ProductDeltaSyncJob.cs
│   ├── ProductFullSyncJob.cs
│   ├── SyncOpenOrdersJob.cs
│   └── SyncTodayOrdersJob.cs
├── Models/
│   ├── Auth/
│   ├── Cache/                       # SQLite entity models
│   ├── Invoicing/                   # OpenDeliveryDto, InvoiceFromDeliveryLog
│   ├── Orde_Models/
│   ├── Payments/
│   └── Pending/                     # PendingOrder, PendingOrderLine
├── Services/
│   ├── Cached Services/
│   │   └── CacheDbContext.cs        # EF Core SQLite context
│   ├── Neon/
│   │   └── NeonDbContext.cs         # EF Core Neon (PostgreSQL) context
│   ├── Queue/
│   │   └── IBackgroundTaskQueue.cs
│   ├── InvoiceFromDeliveryService.cs
│   ├── PendingOrderService.cs
│   ├── SapCustomerService.cs
│   ├── SapProductService.cs
│   └── SapService.cs               # All SAP COM interop (1700+ lines)
├── Middleware/
├── Migrations/                      # EF Core SQLite migrations
├── Program.cs                       # DI, Quartz, SQLite DDL, startup
├── QuartzExtensions.cs
└── appsettings.json
```

---

## Known Constraints

| Constraint | Detail |
|------------|--------|
| **Windows-only** | SAP COM interop (`SAPbobsCOM`) requires Windows |
| **Build tool** | Must use Visual Studio MSBuild — `dotnet build` fails with `ResolveComReference` error |
| **No `Cache=Shared`** | Causes silent SQLite in-memory fallback on Windows — never add to connection string |
| **SAP COM single thread** | `SapService._company` is cached; reconnects on `COMException` |
| **OmniSharp COM false positives** | IDE shows red squiggles on COM types in new methods; MSBuild compiles correctly |
| **Neon free tier** | Use the `-pooler.` connection pooler endpoint; avoid `Pooling=False` without it |
| **Passwords** | User table passwords should be hashed (BCrypt) — currently plaintext |
| **CORS** | Currently `AllowAll` — scope to known origins before wider deployment |
