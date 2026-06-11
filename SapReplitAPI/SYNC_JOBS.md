# Sync Jobs

This document reflects the active Quartz.NET scheduling in `Program.cs`.

## Active Quartz Jobs

| Priority | Job | Cron Expression | Human Schedule | Purpose | Data Source | Data Destination | Concurrency Protection | Manual Trigger Endpoint | Expected Freshness |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `InvoiceDeltaSyncJob` | `0 0/5 * * * ?` | Every 5 minutes at `:00, :05, :10...` | Refresh invoice headers, lines, and payments incrementally | SAP | SQLite `Invoices`, `InvoiceLines`, `InvoicePayments` | `[DisallowConcurrentExecution]` + `InvoiceCacheService` static lock | `POST /api/payments/sync/manual` | 0-5 minutes |
| 2 | `OrderDeltaSyncJob` | `0 2/5 * * * ?` | Every 5 minutes at `:02, :07, :12...` | Refresh changed orders incrementally | SAP | SQLite `OrderHeaders`, `OrderLines` | `[DisallowConcurrentExecution]` + `OrderCacheService` static lock | `POST /api/orders/sync-full` for manual full sync only | 0-5 minutes |
| 3 | `SyncTodayOrdersJob` | `0 1/3 * * * ?` | Every 3 minutes at `:01, :04, :07...` | Refresh today's orders snapshot | SAP | SQLite `TodayOrderHeaders`, `TodayOrderLines` | `[DisallowConcurrentExecution]` + `TodayOrderCacheService` static lock | `POST /api/today-orders/sync` | 0-3 minutes |
| 4 | `SyncOpenOrdersJob` | `0 4/10 * * * ?` | Every 10 minutes at `:04, :14, :24...` | Refresh open orders snapshot | SAP | SQLite `OpenOrderHeaders`, `OpenOrderLines` | `[DisallowConcurrentExecution]` + `OpenOrderCacheService` static lock | `POST /api/open-orders/sync-manual` | 0-10 minutes |
| 5 | `ProductDeltaSyncJob` | `0 3/15 * * * ?` | Every 15 minutes at `:03, :18, :33, :48` | Refresh changed products and stock | SAP | SQLite `Products` | `[DisallowConcurrentExecution]` + `ProductCacheService` static lock | `POST /api/products/sync-products-delta` | 0-15 minutes |
| 6 | `NeonSyncJob` | `0 9/10 * * * ?` | Every 10 minutes at `:09, :19, :29...` | Mirror SQLite cache to Neon | SQLite | Neon PostgreSQL | `[DisallowConcurrentExecution]` | None | Up to 10 minutes after source cache changes |
| 7 | `CustomerFullSyncJob` | `0 0 1 * * ?` | Daily at 01:00 | Reconcile customer cache, delete stale rows | SAP | SQLite `Customers` | `[DisallowConcurrentExecution]` + `CustomerCacheService` static lock | `POST /api/customers/sync` | Daily reconciliation |
| 8 | `ProductFullSyncJob` | `0 0 2 * * ?` | Daily at 02:00 | Full product rebuild | SAP | SQLite `Products` | `[DisallowConcurrentExecution]` + `ProductCacheService` static lock | `POST /api/products/sync` | Daily reconciliation |
| 9 | `OrderFullSyncJob` | `0 0 3 * * ?` | Daily at 03:00 | Full order rebuild | SAP | SQLite `OrderHeaders`, `OrderLines` | `[DisallowConcurrentExecution]` + `OrderCacheService` static lock | `POST /api/orders/sync-full` | Daily reconciliation |
| 10 | `InvoiceFullSyncJob` | `0 0 4 * * ?` | Daily at 04:00 | Full invoice/payment rebuild | SAP | SQLite `Invoices`, `InvoiceLines`, `InvoicePayments` | `[DisallowConcurrentExecution]` + `InvoiceCacheService` static lock | `POST /api/payments/sync/manual` | Daily reconciliation |
| 11 | `InvoiceStatusCacheJob` | `0 15 5 * * ?` | Daily at 05:15 | Build invoice status snapshots for dashboard reporting | SAP | SQLite `DetailedInvoiceStatusCache` | `[DisallowConcurrentExecution]` + per-salesperson write lock in `DashboardService` | None | Daily snapshot |

## Customer Freshness

Customer created through frontend:
- Immediate if `POST /api/customers` successfully creates the SAP customer and the follow-up SQLite cache upsert succeeds.
- If the immediate cache upsert cannot acquire the lock or fails, the customer is still created in SAP and will be reconciled by the next full customer sync.
- `CustomerDeltaSyncJob` is intentionally not active because immediate cache upsert gives fresher results with less SAP load than frequent customer full-sync polling.

Implementation note:
- The API route and response shape are unchanged.
- The controller now performs a best-effort immediate SQLite upsert after successful SAP creation.

## Invoice / Payment Freshness

Invoice or payment changes closed in SAP:
- Normally visible in SQLite cache within 0-5 minutes because `InvoiceDeltaSyncJob` runs every 5 minutes.
- The daily `InvoiceFullSyncJob` remains the reconciliation backstop.

## Manual Trigger Behavior

- Existing manual trigger endpoints remain unchanged.
- `POST /api/today-orders/sync` now calls `TodayOrderCacheService.RefreshTodayOrdersFromSAP()` directly instead of calling `SyncTodayOrdersJob.Execute(...)`.
- This keeps the same route, HTTP verb, and JSON response while avoiding a direct bypass of Quartz job execution semantics.

## Job Purpose Details

### `InvoiceDeltaSyncJob`
- Service: `InvoiceCacheService.DeltaSyncInvoicesAsync()`
- Reads `SyncMetadata["Invoice"]`
- Pulls changed invoices and payments from SAP
- UPSERTs invoice headers, lines, and payments into SQLite

### `OrderDeltaSyncJob`
- Service: `OrderCacheService.SyncOrdersDeltaAsync()`
- Reads `SyncMetadata["Order"]`
- Pulls changed orders from SAP
- UPSERTs order headers and lines into SQLite

### `SyncTodayOrdersJob`
- Service: `TodayOrderCacheService.RefreshTodayOrdersFromSAP()`
- Queries today's SAP orders and lines
- Replaces today's snapshot inside a SQLite transaction with retry-on-lock behavior

### `SyncOpenOrdersJob`
- Service: `OpenOrderCacheService.SyncOpenOrdersAsync()`
- Pulls open SAP orders
- Replaces open-order snapshot inside a SQLite transaction with retry-on-lock behavior

### `ProductDeltaSyncJob`
- Service: `ProductCacheService.SyncDeltaFromSAPAsync()`
- Reads `SyncMetadata["Product"]`
- Pulls changed stock/product rows from SAP
- UPSERTs changed products and removes zero-stock products from SQLite

### `NeonSyncJob`
- Service: `NeonSyncJob`
- Uses SQLite `SyncMetadata` watermarks
- Skips unchanged datasets
- Supports forced full reconcile with `NEON_FULL_RECONCILE=true`

### `CustomerFullSyncJob`
- Service: `CustomerCacheService.FullSyncFromSAPAsync()`
- Rebuilds the customer cache from SAP
- Deletes stale cached customers no longer present in SAP

### `ProductFullSyncJob`
- Service: `ProductCacheService.FullSyncFromSAPAsync()`
- Rebuilds product cache from SAP

### `OrderFullSyncJob`
- Service: `OrderCacheService.FullSyncOrdersAsync()`
- Rebuilds order cache in monthly windows

### `InvoiceFullSyncJob`
- Service: `InvoiceCacheService.FullSyncInvoicesAsync()`
- Rebuilds invoices, invoice lines, and payments in monthly windows

### `InvoiceStatusCacheJob`
- Services: `SapService.GetDetailedInvoiceStatusReportAsync()` + `DashboardService.CacheInvoiceStatusReportAsync()`
- Pulls per-salesperson report rows from SAP
- Replaces salesperson/day snapshots in SQLite

## Inactive Job Classes

These classes exist in the repository but are not registered in `Program.cs`, so Quartz does not run them:

| Job Class | Status | Note |
|---|---|---|
| `ProductChangeSyncJob` | Inactive | Not registered |
| `CacheInvoiceStatusJob` | Inactive | Old duplicate invoice-status job; left in codebase but unscheduled |
| `CustomerDeltaSyncJob` | Not implemented | Immediate post-create cache upsert is the active freshness strategy |

## Execution Notes

- Quartz jobs are registered before `builder.Build()`.
- SQLite migration, index creation, Neon schema initialization, and `SyncMetadata` seeding finish before `app.Run()`.
- Quartz jobs only become active after the host starts.
- `WaitForJobsToComplete = true` applies during shutdown only.
- `NeonSyncJob` also supports a forced full reconcile by setting `NEON_FULL_RECONCILE=true`.

## Concurrency Summary

- All active scheduled Quartz jobs use `[DisallowConcurrentExecution]`.
- Service-level locks are still required because some sync services can also be called outside Quartz through existing manual endpoints.
- Snapshot jobs use WAL mode, busy timeout, transactions, and retry handling for `SQLITE_BUSY` / `SQLITE_LOCKED`.
- Scheduled job classes now rethrow after logging failures so Quartz can record failed executions correctly.

## Production Order

1. `InvoiceDeltaSyncJob`
2. `OrderDeltaSyncJob`
3. `SyncTodayOrdersJob`
4. `SyncOpenOrdersJob`
5. `ProductDeltaSyncJob`
6. `NeonSyncJob`
7. `CustomerFullSyncJob`
8. `ProductFullSyncJob`
9. `OrderFullSyncJob`
10. `InvoiceFullSyncJob`
11. `InvoiceStatusCacheJob`
