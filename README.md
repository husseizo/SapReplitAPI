# SapReplitAPI

A .NET 8 Windows Service that bridges **SAP Business One** (COM interop / DI-API) with external client apps. It maintains a local SQLite cache and a Neon (PostgreSQL) cloud mirror, processes SAP domain events in real time via an outbox pipeline, automates order-to-invoice fulfilment, and exposes a REST API consumed by the Sales App and Warehouse Operations App.

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
- [SAP Event Pipeline](#sap-event-pipeline)
- [Customer Returns](#customer-returns)
- [Zone Fulfillment (experimental)](#zone-fulfillment-experimental)
- [SO → Delivery Automation](#so--delivery-automation)
- [Offline Fulfillment V2](#offline-fulfillment-v2)
- [Offline Order Queue (V1)](#offline-order-queue-v1)
- [Invoice Automation](#invoice-automation)
- [Neon PostgreSQL Mirror](#neon-postgresql-mirror)
- [Database Schema — SQLite](#database-schema--sqlite)
- [Project Structure](#project-structure)
- [Known Constraints](#known-constraints)

---

## Architecture

```
SAP Business One (COM / DI-API)
        │                         SQL Server: MolasIntegration
        │ PostTransactionNotice ──► SapEventOutbox (outbox table)
        │                               │
        ▼                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                    SapReplitAPI (.NET 8)                         │
│                                                                 │
│  REST Controllers  ◄── HTTP (Sales App / Warehouse App)         │
│        │                                                        │
│  SapService  ──► SAP DI-API (COM interop, single connection)    │
│        │                                                        │
│  OutboxPollerService ──► EventHandlerRouter                     │
│        │              ├─ InvoiceEventHandler        (13/A)      │
│        │              ├─ CreditMemoEventHandler     (14/A)      │
│        │              ├─ IncomingPaymentEventHandler (24/A+C)   │
│        │              ├─ DeliveryInventoryEventHandler (15/A)   │
│        │              ├─ ReturnInventoryEventHandler   (16/A)   │
│        │              ├─ SalesOrderCommitmentEventHandler (17)  │
│        │              ├─ GoodsReceiptEventHandler     (59/A)    │
│        │              ├─ GoodsIssueEventHandler       (60/A)    │
│        │              └─ StockTransferEventHandler    (67/A+C)  │
│        │                                                        │
│  Quartz Jobs ──► SQLite cache (F:\AutohubCaches\productcache.db)│
│                       │                                         │
│               Neon PostgreSQL (cloud mirror + offline queue)    │
└─────────────────────────────────────────────────────────────────┘
```

- Runs as a **Windows Service** on port **5050** (Kestrel).
- SAP writes/reads go through `SapService` (single COM connection, auto-reconnect).
- SQLite is the primary local cache; Neon is the cloud mirror and offline queue store.
- The event pipeline is the primary freshness path for invoices, payments, inventory, deliveries, pick lists, and credit memos — Quartz jobs are the fallback/reconciliation layer.

---

## Tech Stack

| Component        | Technology                                           |
|------------------|------------------------------------------------------|
| Framework        | ASP.NET Core 8.0 Windows Service                     |
| SAP Integration  | SAPbobsCOM (COM Interop, Windows-only)               |
| Primary Cache    | SQLite via EF Core 9 + Microsoft.Data.Sqlite         |
| Cloud Mirror     | PostgreSQL on Neon (serverless) via Npgsql           |
| Event Outbox     | SQL Server `MolasIntegration.SapEventOutbox` via ADO.NET |
| Job Scheduler    | Quartz.NET 3.14 (`[DisallowConcurrentExecution]`)    |
| Logging          | Serilog (Console + rolling File sink)                |
| API Docs         | Swagger / Swashbuckle                                |

---

## Prerequisites

- **Windows** — SAP COM interop requires Windows
- SAP Business One client installed on the same machine (provides `SAPbobsCOM.dll`)
- .NET 8 SDK
- Visual Studio 2022 — required to build (`dotnet build` cannot resolve COM references)
- SQL Server instance with `MolasIntegration` database (for the event outbox)

---

## Configuration

### Environment Variables (machine-level — never in appsettings)

```
# SAP connection
SAP__Server=<hostname>
SAP__CompanyDB=<database name>
SAP__UserName=<sap username>
SAP__Password=<sap password>
SAP__LicenseServer=<host:30000>
SAP__SLDServer=<host:40000>

# API key (protects inventory, warehouse, bin, system, and accounts endpoints)
ApiSecurity__ApiKey=<strong-random-key>

# SAP event outbox (MolasIntegration SQL Server)
ConnectionStrings__MolasIntegration=Server=localhost;Database=MolasIntegration;...
```

### appsettings.json (non-secret values only)

```json
{
  "ConnectionStrings": {
    "CacheDB": "Data Source=F:\\AutohubCaches\\productcache.db;Mode=ReadWriteCreate;",
    "NeonDb":  "Host=<pooler-endpoint>;Database=MolasAutoHub;Username=...;Password=...;SSL Mode=Require;"
  },
  "ZoneFulfillment": {
    "InvoiceAutomationEnabled": true,
    "AllocationMode": "Tiered"
  }
}
```

> **Critical:** Do NOT add `Cache=Shared` to the SQLite connection string — it causes a silent in-memory fallback on Windows.

> `NeonDb` is optional. If absent, `NeonSyncJob`, `PendingOrderSyncJob`, `PendingCustomerSyncJob`, and `OfflineFulfillmentRecoveryJob` are automatically disabled.

> `ConnectionStrings__MolasIntegration` is optional. If absent, the OutboxPoller and all event handlers are disabled — Quartz jobs become the sole sync path.

---

## Security

| Mechanism | Applies to | How configured |
|-----------|-----------|----------------|
| `X-Api-Key` header | `POST /api/payments/incoming`, all `/api/accounts`, `/api/inventory`, `/api/warehouse-inventory`, `/api/bin-inventory`, `/api/system` | `ApiSecurity__ApiKey` env var |
| `X-Zone-Experimental: true` header | All `/api/zone-fulfillment/experimental/*` endpoints | Hard-coded gate in controller (no env var) |
| SAP credentials | Internal only — never exposed via API | `SAP__UserName` / `SAP__Password` env vars |
| Idempotency keys | Payments (`clientReference`), Orders (`ReplitId`), Return Requests / Credit Memos (`app_ref` / `U_AppRef`) | SQLite `PaymentIdempotencyLogs`; SAP `U_AppRef` UDF |

---

## Running the Application

### As a Windows Service (production)

```powershell
sc create SapReplitAPI binPath="C:\SAPAPI\SapReplitAPI.exe"
sc start SapReplitAPI
# Config files live alongside the exe in C:\SAPAPI\
# SQLite DB: F:\AutohubCaches\productcache.db
```

### Development (Visual Studio)

Open `SapReplitAPI.sln` and press **F5**, or use the `https` launch profile.

### Build (release)

```powershell
# Must use VS MSBuild — dotnet build fails on COM references
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    SapReplitAPI\SapReplitAPI.csproj /t:Build /p:Configuration=Release /p:Platform="Any CPU"
```

### Logs

Written to `C:\SAPLogs\app.log` (rolling daily, 14-day retention).

---

## API Endpoints

Swagger UI: `/swagger` (Development mode only).

---

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
| POST | `/api/customers` | Create new customer in SAP (idempotent on CardCode) |

**Customer creation notes:**
- Valid `U_Customer_Type` values: `Owner`, `Garage`, `Re-Seller`, `Whole-Sale`, `Corporate`
- `VIN1`/`VIN2`/`VIN3` are optional
- If SAP is offline, the request is queued in Neon `PendingCustomers` and retried by `PendingCustomerSyncJob`

---

### Orders — `/api/orders`

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/orders` | Create ORDR in SAP; falls back to offline queue if SAP is unreachable |
| PUT | `/api/orders/{docEntry}` | Update existing SAP order |
| POST | `/api/orders/quotations` | Create quotation in SAP |
| GET | `/api/orders/headers/today` | Today's order headers from SQLite cache |
| GET | `/api/orders/headers/search-open-headers` | Search open order headers |
| GET | `/api/orders/lines/today` | Today's order lines from SQLite cache |
| GET | `/api/orders/lines/search-open-lines` | Search open order lines |
| POST | `/api/orders/sync-full` | Trigger full order cache refresh (background) |

---

### Local (Draft) Orders — `/api/orders/local`

Build an order incrementally before submitting to SAP. Uses Neon `PendingOrders`.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/orders/local` | Create draft order |
| GET | `/api/orders/local/{replitId}` | Get draft with lines |
| PUT | `/api/orders/local/{replitId}/lines` | Upsert a line on the draft |
| DELETE | `/api/orders/local/{replitId}/lines/{lineNum}` | Remove a line |
| POST | `/api/orders/local/{replitId}/submit` | Submit draft → SAP (or queue offline) |
| DELETE | `/api/orders/local/{replitId}` | Cancel/delete draft |
| GET | `/api/orders/local/pending` | List non-draft orders (`?status=Pending\|Failed\|Synced`) |
| POST | `/api/orders/local/pending/{id}/retry` | Reset Failed order back to Pending |

**Status flow:** `Draft → Pending → Synced` (or `Failed` after 8 retries, or `Cancelled`)

---

### Payments — `/api/payments`

`POST /api/payments/incoming` requires `X-Api-Key` header.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/payments/incoming` | ⚠️ API-key protected. Record incoming payment (ORCT) in SAP |
| GET | `/api/payments/invoices` | Paginated invoice list from SQLite cache |
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

Body: `{ "docDueDate": "2026-09-30" }` — used as fallback due date when ODLN has none.

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
| *(+ more)* | | Salesperson breakdowns, top customers, etc. |

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
| GET | `/api/open-orders/headers` | All open (unclosed) order headers |
| GET | `/api/open-orders/lines` | All open order lines |

---

### Deliveries — `/api/deliveries`

Reads from the SQLite Deliveries cache (refreshed event-driven + every 5 min by `DeliveryDeltaSyncJob`).

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/deliveries` | Filtered delivery list (`?status=&canceled=&cardCode=&from=&to=&slpCode=`) |
| GET | `/api/deliveries/open` | Open, non-cancelled deliveries |
| GET | `/api/deliveries/{docEntry}` | Single delivery with lines |
| GET | `/api/deliveries/{docEntry}/lines` | Lines only |

---

### Pick Lists — `/api/pick-lists`

Reads from the SQLite PickLists/PickListLines/PickListBinAllocations cache.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/pick-lists/{absEntry}` | Pick list header from cache |
| GET | `/api/pick-lists/{absEntry}/lines` | Pick list lines from cache |
| GET | `/api/pick-lists/{absEntry}/bins` | Bin allocations from cache |
| GET | `/api/pick-lists/cache/counts` | Row counts in SQLite cache |
| POST | `/api/pick-lists/{absEntry}/refresh` | Force re-fetch from SAP into cache |
| POST | `/api/pick-lists/full-sync` | Full sync all pick lists from SAP |

---

### Inventory — `/api/inventory`

Requires `X-Api-Key`. Reads from SQLite `WarehouseInventory` cache.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/inventory/warehouse/{itemCode}` | Availability across all warehouses |
| GET | `/api/inventory/warehouse/{itemCode}/{whsCode}` | Availability for one (item, warehouse) pair |

---

### Warehouse Inventory (admin sync) — `/api/warehouse-inventory`

Requires `X-Api-Key`.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/warehouse-inventory/sync/full` | Full SAP → SQLite warehouse inventory snapshot |
| POST | `/api/warehouse-inventory/sync/delta` | Delta sync (changed items since last watermark) |

---

### Bin Inventory (admin sync) — `/api/bin-inventory`

Requires `X-Api-Key`.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/bin-inventory/sync/full` | Full SAP → SQLite bin inventory snapshot (OIBQ+OBIN) |
| POST | `/api/bin-inventory/sync/delta` | Delta sync for bin inventory |

---

### SO → Delivery — `/api/so-delivery`

Manual trigger and run history for the nightly SO→ODLN batch.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/so-delivery/run` | Trigger an SO→Delivery run for today's date |
| GET | `/api/so-delivery/runs` | Paginated run history (`?page=&pageSize=&date=&status=`) |
| GET | `/api/so-delivery/runs/latest` | Most recent run with SO-level logs |
| GET | `/api/so-delivery/runs/{runId}` | Single run detail with logs |
| GET | `/api/so-delivery/runs/{runId}/line-logs` | Line-level delivery logs for a run |
| GET | `/api/so-delivery/reports/{date}` | Delivery report for a specific date |
| GET | `/api/so-delivery/reports/{date}/pdf` | PDF delivery report |

> `SoDeliveryJob` cron is currently commented out — runs are triggered manually or at a time controlled outside of Quartz.

---

### Return Requests — `/api/return-requests`

Creates and manages SAP Return Requests (ORRR). All operations are idempotent on `app_ref`.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/return-requests` | Create ORRR from invoice (OINV → ORRR) |
| GET | `/api/return-requests` | List return requests (`?cardCode=&status=&limit=`) |
| GET | `/api/return-requests/list` | Same as GET (alias for frontend contract) |
| GET | `/api/return-requests/{docEntry}` | Single return request detail |
| POST | `/api/return-requests/{docEntry}/cancel` | Cancel an open ORRR |

**Request body (POST):**
```json
{
  "invoice_doc_entry": 28571,
  "app_ref": "srr-<uuid>",
  "lines": [
    { "base_line": 0, "quantity": 5.0, "whs_code": "001" }
  ]
}
```

---

### Returns (Credit Memos) — `/api/returns`

Creates SAP A/R Credit Memos (ORIN) from Return Requests (ORRR). Idempotent on `app_ref`.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/returns` | Create ORIN from ORRR |
| GET | `/api/returns/list` | List credit memos (`?cardCode=&limit=`) |
| GET | `/api/returns/{docEntry}` | Single credit memo detail |

**Request body (POST):**
```json
{
  "return_request_doc_entry": 46,
  "app_ref": "<uuid>",
  "lines": [
    { "base_line": 0, "quantity": 1.0, "whs_code": "001", "bin_abs": 12 }
  ]
}
```

**Return business flow:**
```
OINV (invoice)
  └─► POST /api/return-requests  →  ORRR (return request)  [RRR1.BaseType=13, BaseEntry=OINV]
        └─► POST /api/returns    →  ORIN (credit memo)     [RIN1.BaseType=234000031, BaseEntry=ORRR]
```

**Partial vs. final return:**
- Partial credit memo → ORRR remains Open, `open_qty` decreases
- Final credit memo → ORRR closes, `open_qty` = 0

**Payments boundary:** Returns do **not** create, cancel, or modify incoming payments (ORCT). Refunds remain manual in SAP Accounts.

---

### Offline Fulfillment V2 — `/api/offline-fulfillments`

Returns 503 when `OfflineFulfillment:Enabled=false` (default). State lives in Neon.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/offline-fulfillments` | Capture offline order |
| GET | `/api/offline-fulfillments` | List orders (`?state=&limit=`) |
| GET | `/api/offline-fulfillments/{id}` | Single order detail |
| GET | `/api/offline-fulfillments/{id}/status` | Recovery status |
| POST | `/api/offline-fulfillments/{id}/reserve` | Record stock reservation |
| POST | `/api/offline-fulfillments/{id}/pick` | Record pick |
| POST | `/api/offline-fulfillments/{id}/confirm-pick` | Confirm pick → triggers recovery queue |
| GET | `/api/offline-fulfillments/mirrored-stock/{itemCode}` | Stock mirror query |

---

### Zone Fulfillment (experimental) — `/api/zone-fulfillment/experimental`

All endpoints require header `X-Zone-Experimental: true`. Returns 403 without it.

Handles the full warehouse automation lifecycle: SO → Pick Lists → Delivery → Invoice.

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/zone-fulfillment/experimental/orders` | Create ORDR with tiered zone allocation |
| GET | `/api/zone-fulfillment/experimental/orders/{requestId}/status` | Full orchestration state |
| POST | `/api/zone-fulfillment/experimental/plan` | Dry-run allocation plan (no SAP mutation) |
| POST | `/api/zone-fulfillment/experimental/orders/{requestId}/pick-lists` | Create OPKL(s) for SO |
| POST | `/api/zone-fulfillment/experimental/orders/{requestId}/pick-lists/repick` | Repick a closed OPKL line |
| GET | `/api/zone-fulfillment/experimental/orders/{requestId}/pick-lists/{absEntry}/state` | Read-only pre-pick gate snapshot |
| POST | `/api/zone-fulfillment/experimental/orders/{requestId}/pick-lists/{absEntry}/pick` | Execute pick (full qty only) |
| POST | `/api/zone-fulfillment/experimental/orders/{requestId}/delivery` | Manual delivery trigger |
| GET | `/api/zone-fulfillment/experimental/delivery-gate/diagnostics` | Read-only delivery gate diagnostics |
| POST | `/api/zone-fulfillment/experimental/orders/{requestId}/invoice` | Manual invoice trigger |
| GET | `/api/zone-fulfillment/experimental/orders/{requestId}/whs-change/diagnostics` | WHS-change read-only diagnostics |
| POST | `/api/zone-fulfillment/experimental/orders/{requestId}/whs-change` | Warehouse reassignment |
| GET | `/api/zone-fulfillment/experimental/reports` | Zone fulfillment reports list |
| GET | `/api/zone-fulfillment/experimental/reports/{id}` | Single report detail |

**Automated flow (after pick completes):**
```
POST pick → all picks complete?
  → YES → ZoneFulfillmentAutomationService
       → ExecuteDeliveryAsync()  → ONE ODLN
       → ExecuteInvoiceAsync()   → ONE OINV  (gated by ZoneFulfillment:InvoiceAutomationEnabled)
  → NO  → WaitingForOtherPicks (returns pendingWarehouses list)
```

**Tiered allocation (AllocationMode=Tiered):**
- Tier 1–2: HOME warehouses only (origin-side cluster)
- Tier 3: First FALLBACK warehouse with full qty
- Tier 4: Split cascade HOME → FALLBACK

---

### System — `/api/system`

Requires `X-Api-Key`.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/system/event-pipeline-health` | Event outbox health: pending/processing/failed counts, oldest pending age |

---

### Sync (manual trigger) — `/api/sync`

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/sync/neon` | Manually trigger a full NeonSyncJob run |

---

### Users — `/api/users`

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/users` | All users |
| POST | `/api/users` | Create user |
| PUT | `/api/users/{id}` | Update user |
| DELETE | `/api/users/{id}` | Delete user |
| POST | `/api/users/login` | Authenticate |

---

## Background Jobs

All jobs use `[DisallowConcurrentExecution]`. All cron expressions use Quartz syntax (second minute hour day-of-month month day-of-week) in **EAT (UTC+3)** timezone.

| Job | Schedule | Description |
|-----|----------|-------------|
| `InvoiceDeltaSyncJob` | every 5 min `:00` | SAP OINV → SQLite delta sync |
| `SyncTodayOrdersJob` | every 3 min `:00` | Today's orders SAP → SQLite |
| `OrderDeltaSyncJob` | every 5 min `:02` | Recent orders SAP → SQLite |
| `SyncOpenOrdersJob` | every 10 min `:04` | Open orders SAP → SQLite |
| `ProductDeltaSyncJob` | every 15 min `:03` | Changed products SAP → SQLite |
| `AccountStatementSyncJob` | every 5 min `:00` | GL journal entries SAP → SQLite |
| `DeliveryDeltaSyncJob` | every 5 min `:01` | ODLN delta SAP → SQLite |
| `PickListDeltaSyncJob` | every 5 min `:03` | OPKL delta SAP → SQLite |
| `WarehouseInventoryDeltaSyncJob` | every 15 min `:08` | OITW delta SAP → SQLite |
| `BinInventoryDeltaSyncJob` | every 15 min `:13` | OIBQ delta SAP → SQLite |
| `ZoneFulfillmentPickReconciliationJob` | every 30 s | Detects SAP-native OPKL confirmations; unblocks delivery automation |
| `InvoiceFromDeliveryJob` | hourly 06:00–20:00 | Auto-invoice all open ODLN deliveries |
| `CustomerFullSyncJob` | daily 01:00 | Full customer resync SAP → SQLite |
| `ProductFullSyncJob` | daily 02:00 | Full product resync |
| `WarehouseInventoryFullSyncJob` | daily 02:30 | Full OITW snapshot SAP → SQLite |
| `BinInventoryFullSyncJob` | daily 03:00 | Full OIBQ snapshot SAP → SQLite |
| `OrderFullSyncJob` | daily 03:00 | Full order history resync |
| `DeliveryFullSyncJob` | daily 04:30 | Full ODLN snapshot SAP → SQLite |
| `InvoiceFullSyncJob` | daily 04:00 | Full invoice history resync |
| `PickListFullSyncJob` | daily 05:45 | Full OPKL snapshot SAP → SQLite |
| `InvoiceStatusCacheJob` | daily 05:15 | Rebuild invoice-status cache |
| **`NeonSyncJob`** ¹ | every 3 min `:02` | Mirror SQLite → Neon PostgreSQL (watermark-based) |
| **`PendingOrderSyncJob`** ¹ | every 15 s | Retry offline orders against SAP |
| **`PendingCustomerSyncJob`** ¹ | every 15 s | Retry offline customer creates against SAP |
| **`OfflineFulfillmentRecoveryJob`** ¹ | every 30 s | Run Offline V2 SAP recovery (no-op when feature disabled) |

¹ Requires `NeonDb` connection string to be registered.

---

## SAP Event Pipeline

When SAP commits a document, `PostTransactionNotice` fires and the event is written to `MolasIntegration.SapEventOutbox`. `OutboxPollerService` polls every 1 second and routes each event to the correct handler.

**Event routing:**

| ObjectType | TransactionType | Handler | Effect |
|------------|----------------|---------|--------|
| 13 | A | `InvoiceEventHandler` | Create/update Invoice in SQLite+Neon; mark base ODLN closed |
| 14 | A/U/C | `CreditMemoEventHandler` | Refresh inventory (Path A + Path B); update invoice lifecycle; write ORIN to SQLite+Neon credit memo cache |
| 15 | A | `DeliveryInventoryEventHandler` | Refresh OITW+OIBQ for delivery warehouses |
| 16 | A | `ReturnInventoryEventHandler` | Refresh OITW+OIBQ (A/R Return restores stock) |
| 17 | A/U/C | `SalesOrderCommitmentEventHandler` | Refresh IsCommitted/AvailableToSell in WarehouseInventory |
| 24 | A/C | `IncomingPaymentEventHandler` | Record payment; update OINV lifecycle status |
| 59 | A | `GoodsReceiptEventHandler` | Refresh OITW+OIBQ (Goods Receipt increases stock) |
| 60 | A | `GoodsIssueEventHandler` | Refresh OITW+OIBQ (Goods Issue reduces stock) |
| 67 | A/C | `StockTransferEventHandler` | Refresh OITW+OIBQ for both from- and to-warehouses |

**Outbox credentials:** `SapReplitOutboxApp` (SQL Server login, SELECT+UPDATE only on `SapEventOutbox`).

**Registration guard:** OutboxPoller is only started when `ConnectionStrings:MolasIntegration` is non-empty.

---

## Customer Returns

Full OINV → ORRR → ORIN flow. No changes to payments, pick lists, deliveries, or invoice automation.

**Path B:** When an ORIN is created from an ORRR, `RIN1.BaseType=234000031` and `BaseEntry=ORRR.DocEntry`. `CreditMemoEventHandler` follows this chain (ORIN → ORRR → OINV) to resolve the affected invoice.

**Credit Memo cache:** When `14/A` fires for an ORIN, `CreditMemoEventHandler` calls `GetCreditMemoSnapshotByDocEntry` (SAP COM) and writes the result to both `CreditMemoHeaders`/`CreditMemoLines` in SQLite and Neon within seconds. `NeonSyncJob` is not required for credit memo freshness.

**Production verified:**
```
OINV 28571  →  ORRR 46  →  ORIN 81
RRR1: BaseType=13, BaseEntry=28571
RIN1: BaseType=234000031, BaseEntry=46, BaseLine=0
SQLite + Neon: DocEntry=81, DocNum=13, CUS001181, Total=35,000
```

---

## Zone Fulfillment (experimental)

Multi-warehouse order fulfilment automation. All endpoints are prefixed `/api/zone-fulfillment/experimental` and require `X-Zone-Experimental: true`.

State is persisted in `MolasIntegration` (SQL Server) tables: `FulfillmentOrchestration`, `FulfillmentRequestLine`, `AllocationPlan`, `SoLineFragment`, `PickListRecord`, `PickListFragmentRecord`, `DeliveryRecord`, `DeliveryFragmentRecord`, `InvoiceRecord`, `ZoneWarehousePriority`, `OriginWarehousePriority`.

**Full automation lifecycle:**
```
POST /orders  →  ORDR.Add() (SAP SO)
  → AUTO: OPKL.Add() per warehouse fragment
  → Picker: POST pick-lists/{absEntry}/pick
  → AUTO: all picks complete?
       YES → ODLN.Add() (one delivery)
           → OINV.Add() (one invoice, config-gated)
       NO  → WaitingForOtherPicks
  → FALLBACK: ZoneFulfillmentPickReconciliationJob (every 30s) reconciles SAP-native picks
```

**Idempotency:** All mutations are keyed on `requestId` (GUID). Same `requestId` + same payload = 200 OK (no duplicate). Same `requestId` + different payload = 409 Conflict.

**Warehouse reassignment:** `POST .../whs-change` — reassigns a ZF SO's warehouse after allocation, subject to 6.5-gate validation (no active OPKL allowed).

---

## SO → Delivery Automation

`SoDeliveryService` processes all open Sales Orders (ORDR) for today and creates Delivery Notes (ODLN) in SAP.

**Rules:**
- Only processes SOs with `DocStatus=O` and `CANCELED=N`
- Date must equal today's EAT date (historical and future dates are rejected)
- Concurrency guard (DB-level + in-process semaphore) prevents duplicate runs
- Audit log written to `SoDeliveryRuns`/`SoDeliveryLogs`/`SoDeliveryLineLogs` in SQLite
- Bin allocations are recorded per SO line in `BinAllocationsJson` (JSON column)
- PDF delivery reports available via `/api/so-delivery/reports/{date}/pdf`

> The Quartz trigger for `SoDeliveryJob` is commented out — trigger manually via `POST /api/so-delivery/run` or re-enable in Program.cs.

---

## Offline Fulfillment V2

Captures sales orders when the warehouse device has no internet connection. State lives in Neon.

**Workflow:** Capture → Reserve → Pick → ConfirmPick → Recovery (SAP: ORDR + ODLN + OINV)

**Recovery** runs every 30 seconds via `OfflineFulfillmentRecoveryJob`. Each stage is checkpointed — a restart resumes from the last completed SAP mutation.

Controlled by config flag `OfflineFulfillment:Enabled` (default `false`). Returns 503 when disabled.

---

## Offline Order Queue (V1)

When SAP is unreachable, orders submitted via `POST /api/orders` are saved to Neon `PendingOrders` and retried automatically.

```
POST /api/orders
  ├─ SAP available → DocEntry returned immediately
  └─ SAP unavailable → Saved to PendingOrders → 202 Accepted
        └─ PendingOrderSyncJob (every 15s)
              ├─ SAP up → CreateOrder → Status: Synced
              └─ SAP down → Exponential backoff
                   Delays: 30s, 60s, 2m, 5m, 10m, 20m, 30m, 60m
                   After 8 failures → Status: Failed
                   Manual reset: POST /api/orders/local/pending/{id}/retry
```

**ReplitId format:** `OR-` + 12 uppercase hex chars (e.g. `OR-A3F72B1C9E4D`) — stored in SAP order `U_ReplitId` UDF.

---

## Invoice Automation

`InvoiceFromDeliveryJob` runs every hour from 06:00 to 20:00 and converts open ODLN delivery notes into OINV AR invoices.

**Rules:**
- Only processes `DocStatus=O`, `CANCELED=N` deliveries
- Idempotent: a delivery is only invoiced once (`Success` log row in `InvoiceFromDeliveryLogs`)
- Skips Zone Fulfillment deliveries (`U_ZoneRef <> 'ZoneFulfillment'` filter)
- `DocDueDate` inherited from ODLN; falls back to request body if ODLN has none
- Locked documents logged as `Locked` (not `Failed`) and retried next hour

---

## Neon PostgreSQL Mirror

Tables mirrored to Neon (connection via pooler endpoint):

| Table | Primary source | Sync method |
|-------|---------------|-------------|
| `Invoices` | SAP OINV | `InvoiceEventHandler` (event) + `NeonSyncJob` |
| `InvoiceLines` | SAP INV1 | `NeonSyncJob` (watermark) |
| `InvoicePayments` | SAP ORCT | `IncomingPaymentEventHandler` (event) + `NeonSyncJob` |
| `AccountStatements` | SAP JDT1 | `NeonSyncJob` |
| `TodayOrderHeaders` | SQLite cache | `TodayOrderEventRefreshService` (event) + `NeonSyncJob` |
| `TodayOrderLines` | SQLite cache | `NeonSyncJob` |
| `Deliveries` | SAP ODLN | `NeonDeliveryWriteService` (event) + `NeonSyncJob` |
| `DeliveryLines` | SAP DLN1 | `NeonDeliveryWriteService` (event) + `NeonSyncJob` |
| `WarehouseInventory` | SAP OITW | `NeonInventoryWriteCoordinator` (event) + `NeonSyncJob` |
| `BinInventory` | SAP OIBQ | `NeonInventoryWriteCoordinator` (event) + `NeonSyncJob` |
| `CreditMemoHeaders` | SAP ORIN | `NeonCreditMemoWriteService` (event only — not in NeonSyncJob) |
| `CreditMemoLines` | SAP RIN1 | `NeonCreditMemoWriteService` (event only) |
| `PendingOrders` | API (offline queue) | Written directly by `PendingOrderService` |
| `PendingOrderLines` | API (offline queue) | Written directly by `PendingOrderService` |
| `ZoneFulfillmentReports` | MolasIntegration | `ZoneFulfillmentReportRepository` |
| `ZoneFulfillmentReportLines` | MolasIntegration | `ZoneFulfillmentReportRepository` |

> `CreditMemoHeaders` / `CreditMemoLines` freshness is fully event-driven. `NeonSyncJob` is not required.

---

## Database Schema — SQLite

Production file: `F:\AutohubCaches\productcache.db`

Tables created at startup via `CREATE TABLE IF NOT EXISTS` DDL in `Program.cs` (for post-migration tables) and via EF Core `Migrate()` (for migration-managed tables).

| Table | Purpose |
|-------|---------|
| `Products` | SAP item master |
| `Customers` | SAP business partners |
| `Invoices` | SAP AR invoice headers |
| `InvoiceLines` | SAP AR invoice lines |
| `InvoicePayments` | Payments applied to invoices |
| `DetailedInvoiceStatusCache` | Computed invoice lifecycle status |
| `OrderHeaders` | Cached open order headers |
| `OrderLines` | Cached order lines |
| `TodayOrderHeaders` | Today's order headers |
| `TodayOrderLines` | Today's order lines |
| `OpenOrderHeaders` | Currently open orders |
| `OpenOrderLines` | Open order lines |
| `SalesTargets` | Salesperson quarterly targets |
| `AccountStatements` | GL journal entries (JDT1) |
| `Deliveries` | SAP ODLN headers |
| `DeliveryLines` | SAP DLN1 lines |
| `PickLists` | SAP OPKL headers |
| `PickListLines` | SAP PKL1 lines |
| `PickListBinAllocations` | Bin allocations per pick list line |
| `WarehouseInventory` | OITW: OnHand, IsCommitted, AvailableToSell per (item, warehouse) |
| `BinInventory` | OIBQ: quantity per (item, bin) |
| `CreditMemoHeaders` | SAP ORIN headers (event-driven fast path) |
| `CreditMemoLines` | SAP RIN1 lines |
| `SoDeliveryRuns` | SO→Delivery run audit records |
| `SoDeliveryLogs` | Per-SO delivery log entries |
| `SoDeliveryLineLogs` | Per-line delivery log entries |
| `ZoneFulfillmentReports` | ZF daily delivery report headers |
| `ZoneFulfillmentReportLines` | ZF report line items |
| `PaymentIdempotencyLogs` | Prevents duplicate incoming payment posts |
| `InvoiceFromDeliveryLogs` | Audit log for delivery→invoice automation |
| `Users` | App users (auth) |
| `SyncMetadata` | Watermark timestamps per sync type |

---

## Project Structure

```
SapReplitAPI/
├── Controllers/
│   ├── AccountsController.cs          # GL statements (API-key protected)
│   ├── BinInventoryController.cs      # Bin inventory sync admin (API-key)
│   ├── CustomersController.cs
│   ├── DashboardController.cs
│   ├── DeliveriesController.cs        # ODLN cache reads
│   ├── InventoryController.cs         # Warehouse availability reads (API-key)
│   ├── InvoicesController.cs          # Invoice-from-delivery trigger
│   ├── LocalOrdersController.cs       # Draft + offline order queue
│   ├── OfflineFulfillmentController.cs # Offline V2 (feature-flagged)
│   ├── OpenOrdersController.cs
│   ├── OrdersController.cs
│   ├── PaymentsController.cs          # API-key on incoming payment
│   ├── PickListController.cs          # OPKL cache reads
│   ├── ProductsController.cs
│   ├── ReturnRequestsController.cs    # ORRR create/list/cancel
│   ├── ReturnsController.cs           # ORIN create/list
│   ├── SoDeliveryController.cs        # SO→Delivery run + history
│   ├── SyncController.cs              # Manual Neon sync trigger
│   ├── SystemController.cs            # Event pipeline health (API-key)
│   ├── TodayOrdersController.cs
│   ├── UserController.cs
│   ├── WarehouseInventoryController.cs # WH inventory sync admin (API-key)
│   └── ZoneFulfillmentController.cs   # Full ZF lifecycle (X-Zone-Experimental)
├── Filters/
│   └── ApiKeyAuthFilter.cs
├── Jobs/
│   ├── AccountStatementSyncJob.cs
│   ├── BinInventoryDeltaSyncJob.cs
│   ├── BinInventoryFullSyncJob.cs
│   ├── CustomerFullSyncJob.cs
│   ├── DeliveryDeltaSyncJob.cs
│   ├── DeliveryFullSyncJob.cs
│   ├── InvoiceDeltaSyncJob.cs
│   ├── InvoiceFromDeliveryJob.cs      # Hourly 06:00–20:00
│   ├── InvoiceFullSyncJob.cs
│   ├── InvoiceStatusCacheJob.cs
│   ├── NeonSyncJob.cs                 # SQLite → Neon mirror
│   ├── OfflineFulfillmentRecoveryJob.cs # V2 SAP recovery (every 30s)
│   ├── OrderDeltaSyncJob.cs
│   ├── OrderFullSyncJob.cs
│   ├── PendingCustomerSyncJob.cs      # Offline customer retry (every 15s)
│   ├── PendingOrderSyncJob.cs         # Offline order retry (every 15s)
│   ├── PickListDeltaSyncJob.cs
│   ├── PickListFullSyncJob.cs
│   ├── ProductDeltaSyncJob.cs
│   ├── ProductFullSyncJob.cs
│   ├── SoDeliveryJob.cs               # SO→Delivery nightly (manually triggered)
│   ├── SyncOpenOrdersJob.cs
│   ├── SyncTodayOrdersJob.cs
│   ├── WarehouseInventoryDeltaSyncJob.cs
│   ├── WarehouseInventoryFullSyncJob.cs
│   └── ZoneFulfillmentPickReconciliationJob.cs # ZF SAP-native pick reconciliation (every 30s)
├── Migrations/                        # EF Core SQLite migrations
├── Models/
│   ├── Cache/                         # SQLite entity models (incl. CachedCreditMemo/Line)
│   ├── Inventory/
│   ├── Offline/
│   ├── Returns/                       # CreateReturnRequestDto, CreateReturnDto
│   ├── ZoneFulfillment/
│   └── ...
├── Services/
│   ├── Cached Services/
│   │   ├── CacheDbContext.cs          # EF Core SQLite context
│   │   ├── CreditMemoCacheService.cs  # SQLite UPSERT for ORIN/RIN1
│   │   ├── DeliveryCacheService.cs
│   │   ├── InvoiceCacheService.cs
│   │   ├── OrderCacheService.cs
│   │   └── ...
│   ├── Events/
│   │   ├── CreditMemoEventHandler.cs  # 14/A — inventory + cache write
│   │   ├── EventHandlerRouter.cs
│   │   ├── InvoiceEventHandler.cs     # 13/A
│   │   ├── IncomingPaymentEventHandler.cs # 24/A+C
│   │   ├── OutboxClaimService.cs      # Claim/mark-done/mark-failed
│   │   ├── OutboxPollerService.cs     # 1-second poll background service
│   │   └── ...
│   ├── Inventory/
│   │   ├── InventoryEventRefreshService.cs # OITW+OIBQ refresh
│   │   └── ...
│   ├── Neon/
│   │   ├── NeonCreditMemoWriteService.cs  # Neon UPSERT for ORIN/RIN1
│   │   ├── NeonDbContext.cs           # EF Core Neon (PostgreSQL) context
│   │   ├── NeonDeliveryWriteService.cs
│   │   ├── NeonEventWriteService.cs
│   │   └── ...
│   ├── Offline/
│   │   ├── OfflineFulfillmentService.cs
│   │   └── OfflineFulfillmentRecoveryService.cs
│   ├── PickList/
│   │   ├── PickListCacheService.cs
│   │   └── PickListEventRefreshService.cs
│   ├── SoDelivery/
│   │   ├── SoDeliveryService.cs
│   │   ├── SoDeliveryDbService.cs
│   │   └── SoDeliveryReportService.cs
│   ├── TodayOrders/
│   │   └── TodayOrderEventRefreshService.cs
│   ├── ZoneFulfillment/
│   │   ├── TieredZoneAllocationEngine.cs
│   │   ├── ZoneFulfillmentOrchestrationService.cs
│   │   ├── ZoneFulfillmentPickListService.cs
│   │   ├── ZoneFulfillmentDeliveryService.cs
│   │   ├── ZoneFulfillmentInvoiceService.cs
│   │   └── ...
│   ├── InvoiceLifecycleStatusService.cs
│   ├── SapService.cs                  # All SAP COM interop (2000+ lines)
│   └── ...
├── Program.cs                         # DI registration, Quartz, startup DDL
└── appsettings.json
```

---

## Known Constraints

| Constraint | Detail |
|------------|--------|
| **Windows-only** | SAP COM interop (`SAPbobsCOM`) requires Windows |
| **Build tool** | Must use Visual Studio MSBuild — `dotnet build` fails on `ResolveComReference` |
| **SQLite path** | Production DB is `F:\AutohubCaches\productcache.db` (from `C:\SAPAPI\appsettings.Production.json`). The source-tree `SapReplitAPI\productcache.db` is a dev artifact, not used by the running service |
| **No `Cache=Shared`** | Causes silent SQLite in-memory fallback on Windows |
| **`EnsureCreated` is a no-op** | Tables added after the initial DB creation must be created via explicit `CREATE TABLE IF NOT EXISTS` in Program.cs startup, not `EnsureCreated()` or `Migrate()` |
| **SAP COM single thread** | `SapService._company` is cached; reconnects on `COMException`. Never call SAP COM from multiple threads without the built-in lock |
| **ORRR ObjType = 234000031** | SAP Return Request `BoObjectTypes` constant is `(BoObjectTypes)234000031`, not a named enum value |
| **No `Document_Lines.BinCode` on credit notes** | DI API does not expose `BinCode` on `oReturns` lines — bin validation happens before `orin.Add()` but bin is not set on the line |
| **Neon free tier** | Use the `-pooler.` connection pooler endpoint; Neon serverless requires it |
| **CORS** | Currently `AllowAll` — tighten to known origins before wider exposure |
| **Passwords** | User table passwords are currently stored as plaintext — hash with BCrypt before production hardening |
