# Architecture

## High-Level Overview

```
┌─────────────────────────────────────────────────────────────┐
│                        HTTP Clients                         │
│           (mobile app, web dashboard, Postman, etc.)        │
└───────────────────────────┬─────────────────────────────────┘
                            │ HTTPS (ngrok tunnel)
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                 ASP.NET Core 8 (port 5050)                  │
│                                                             │
│  ┌─────────────┐   ┌──────────────┐   ┌─────────────────┐  │
│  │ Controllers │   │   Services   │   │   Quartz Jobs   │  │
│  │  (8 total)  │──▶│  SapService  │   │   (12 total)    │  │
│  └─────────────┘   │  Cache Svcs  │◀──│                 │  │
│                    └──────┬───────┘   └────────┬────────┘  │
│                           │                    │           │
│                    ┌──────▼───────┐            │           │
│                    │ CacheDbCtx   │◀───────────┘           │
│                    │  (EF Core)   │                        │
│                    └──────┬───────┘                        │
│                           │                                │
│                    ┌──────▼───────┐   ┌────────────────┐  │
│                    │    SQLite    │   │  NeonDbContext  │  │
│                    │  (primary)   │   │  (PostgreSQL)  │  │
│                    └──────────────┘   └────────────────┘  │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐  │
│  │                  SAPbobsCOM (COM)                    │  │
│  │          SAP Business One DI API (Windows)           │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

---

## Key Design Patterns

### 1. Cache-First Architecture
SAP Business One is accessed via COM interop (`SapService`), which is slow — each query involves a full COM round-trip. All data is therefore **synced into SQLite** by background jobs and controllers read exclusively from the cache. The live SAP COM layer is only used for writes (create order, create customer) and job syncs.

### 2. Delta + Full Sync
Each entity uses a two-job pattern:
- **FullSync** — periodically truncates and rebuilds the entire cache table.
- **DeltaSync** — runs frequently, queries only records changed since `LastSyncedAt` (stored in `SyncMetadata`), and upserts them.

Delta jobs use a `SemaphoreSlim` lock to skip runs while a full sync is in progress.

### 3. Background Task Queue
Manual sync triggers from controllers (e.g., `POST /api/customers/sync`) enqueue work onto `IBackgroundTaskQueue`. A `QueuedHostedService` drains the queue on a single background thread. This prevents controller requests from blocking on long SAP operations.

### 4. Neon Mirror (Optional)
`NeonSyncJob` runs every 5 minutes and mirrors the SQLite cache to a PostgreSQL database on Neon. If `NeonDb` connection string is absent at startup, the job is never registered.

---

## Data Flow: Write Path (e.g., Create Order)

```
POST /api/orders
     │
     ▼
OrdersController
     │
     ▼
SapService.CreateOrder()
     │  (COM interop)
     ▼
SAP Business One
     │
     ▼
SAP assigns DocEntry → returned to client
     │
     ▼ (next delta sync cycle, ≤ 5 min)
OrderDeltaSyncJob
     │
     ▼
SQLite cache (CachedOrder / CachedOrderLine)
```

## Data Flow: Read Path (e.g., Get Orders)

```
GET /api/orders/lines/today
     │
     ▼
OrdersController
     │
     ▼
OrderCacheService.GetCachedOrderLines()
     │
     ▼
SQLite (in-process, <5ms)
     │
     ▼
JSON response
```

---

## Service Layer

### `SapService`
The sole entry point for SAP COM. Manages connection lifecycle (connect/disconnect per-call), executes SQL via SAP's `Recordset` object, and maps results to DTOs. Decorated with `#pragma warning disable` for nullable warnings due to the nature of COM interop.

### Cache Services (under `Services/Cached Services/`)

| Service | Responsibility |
|---|---|
| `ProductCacheService` | Read/write `CachedProduct` (SQLite) |
| `InvoiceCacheService` | Full + delta sync of invoices, payments; watermark management |
| `OrderCacheService` | Read cached orders and lines |
| `CustomerCacheService` | Customer cache read/write, phone search |
| `TodayOrderCacheService` | Today's orders (separate table, frequent refresh) |
| `OpenOrderCacheService` | Open/pending orders snapshot |
| `UserCacheService` | App-user CRUD on SQLite `Users` table |

### `DashboardService`
Aggregates invoice and sales data from SQLite for dashboard KPIs. Uses raw `SqliteConnection` for some operations (bulk inserts with PRAGMA control).

---

## Database Schema

### SQLite (`CacheDbContext`)

| Table | Key Columns |
|---|---|
| `Products` | `ItemCode` (unique index), stock per warehouse |
| `Invoices` | `DocEntry`, `DocDate`, `SalesEmployeeCode`, `GroupNum` |
| `InvoiceLines` | `DocEntry`, `LineNum` |
| `InvoicePayments` | `DocEntry`, payment details |
| `OrderHeaders` | `DocEntry`, `DocNum`, `SlpCode`, `CancellationStatus` |
| `OrderLines` | `DocEntry`, `LineNum`, item/price/warehouse |
| `TodayOrderHeaders` | Snapshot of today's sales orders |
| `TodayOrderLines` | Lines for today's orders |
| `OpenOrderHeaders` | Unfulfilled orders |
| `OpenOrderLines` | Lines for open orders |
| `CachedCustomers` | `CardCode`, `CardName`, phone numbers |
| `SalesTargets` | `SlpCode`, `Year`, `Month`, target amount |
| `SyncMetadata` | `Type` (entity name), `LastSyncedAt` watermark |
| `Users` | App users with `Role`, `SlpCode` |
| `DetailedInvoiceStatusCache` | Pre-computed invoice status report rows |

### Neon PostgreSQL (`NeonDbContext`)
Mirrors a subset of SQLite tables. Schema must be kept in sync manually when new columns are added to SQLite models (see [SYNC_JOBS.md](SYNC_JOBS.md)).

---

## Dependency Injection Lifetimes

| Service | Lifetime | Reason |
|---|---|---|
| `SapService` | Scoped | Connects fresh per scope; auto-recovers if SAP drops |
| `CacheDbContext` | Scoped | EF Core best practice |
| `NeonDbContext` | Scoped | Npgsql best practice |
| All Cache Services | Scoped | Depend on scoped `CacheDbContext` |
| `IBackgroundTaskQueue` | Singleton | Shared channel across all requests |
| `QueuedHostedService` | Singleton (IHostedService) | Single consumer |

---

## Concurrency Model

- **Quartz** runs each job on its own thread from a thread pool. `[DisallowConcurrentExecution]` prevents overlapping executions of the same job.
- **SQLite WAL mode** is enabled at startup to allow concurrent reads during writes.
- **SemaphoreSlim locks** in `InvoiceCacheService` and `OrderCacheService` prevent delta and full syncs from colliding.
- **Per-SlpCode `SemaphoreSlim`** in `DashboardService` prevents concurrent writes for the same salesperson.
