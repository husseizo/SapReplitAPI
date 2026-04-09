# Background Sync Jobs

All jobs are Quartz.NET jobs registered in `Program.cs`. Each implements `IJob` and is decorated with `[DisallowConcurrentExecution]` to prevent overlapping runs.

---

## Job Schedule Overview

| Job | Interval / Schedule | Mode | Entity |
|---|---|---|---|
| `InvoiceDeltaSyncJob` | Every **1 min** | Delta | Invoices + Payments |
| `SyncTodayOrdersJob` | Every **3 min** | Full (today) | Today's Orders |
| `OrderDeltaSyncJob` | Every **5 min** | Delta | Orders |
| `SyncOpenOrdersJob` | Every **5 min** | Full | Open Orders |
| `NeonSyncJob` | Every **5 min** | Mirror | All → Neon Postgres |
| `CustomerFullSyncJob` | Every **6 min** | Full | Customers |
| `ProductDeltaSyncJob` | Every **70 min** | Delta | Products |
| `OrderFullSyncJob` | Every **5 hrs** | Full | Orders |
| `InvoiceFullSyncJob` | Every **12 hrs** | Full | Invoices + Payments |
| `ProductFullSyncJob` | Daily **2:00 AM** | Full | Products |
| `InvoiceStatusCacheJob` | Daily **5:00 AM** | Compute | Invoice Status Report |
| `CacheInvoiceStatusJob` | Daily **5:00 AM** | Compute | Invoice Status Cache |

---

## Delta vs Full Sync

### Delta Sync
- Reads `SyncMetadata.LastSyncedAt` watermark for the entity.
- Queries SAP only for records created **or updated** since that timestamp (using the `isDelta: true` flag, which adds `OR UpdateDate >= '{from}'` to the SAP SQL query).
- Upserts changed records into SQLite.
- Advances `LastSyncedAt` to `DateTime.Now` only after a successful write.
- Skips the run (with a warning log) if a full sync is holding the lock.

### Full Sync
- Truncates the entire cache table.
- Fetches all records from SAP within the relevant date range.
- Bulk-inserts into SQLite.
- Runs less frequently to avoid overwhelming the SAP connection.

---

## Individual Job Details

### `InvoiceDeltaSyncJob`
- **Interval**: 1 minute
- **Service**: `InvoiceCacheService.DeltaSyncInvoicesAsync()`
- **Lock timeout**: 45 seconds (skips if full sync is running)
- **Window**: `LastSyncedAt` → end of today
- **Latency profile**: Best ~0s, Avg ~30s, Worst ~1min
- Syncs both invoice headers/lines and invoice payments in the same window.

### `InvoiceFullSyncJob`
- **Interval**: 12 hours
- **Service**: `InvoiceCacheService.FullSyncInvoicesAsync()`
- Truncates and rebuilds the entire `Invoices`, `InvoiceLines`, and `InvoicePayments` tables.

### `OrderDeltaSyncJob`
- **Interval**: 5 minutes
- Queries SAP for orders changed since `LastSyncedAt`.
- Upserts `CachedOrder` (headers) and `CachedOrderLine` (lines).

### `OrderFullSyncJob`
- **Interval**: 5 hours
- Full rebuild of `OrderHeaders` and `OrderLines`.

### `SyncTodayOrdersJob`
- **Interval**: 3 minutes
- Fetches today's sales orders from SAP and refreshes `TodayOrderHeaders` + `TodayOrderLines`.
- Uses a separate table from the main order cache.

### `SyncOpenOrdersJob`
- **Interval**: 5 minutes
- Full refresh of `OpenOrderHeaders` + `OpenOrderLines` (open/pending orders only).
- Uses `ExecuteSqlRawAsync("DELETE FROM ...")` for truncation instead of EF `RemoveRange`.

### `CustomerFullSyncJob`
- **Interval**: 6 minutes
- Full refresh of `CachedCustomers` table.
- Fetches all customers from SAP; no delta mode for customers.

### `ProductDeltaSyncJob`
- **Interval**: 70 minutes
- Queries SAP for products changed since `LastSyncedAt`.
- Upserts changed `CachedProduct` records.

### `ProductFullSyncJob`
- **Schedule**: Daily at 2:00 AM
- Full rebuild of the `Products` SQLite table.
- Uses `ON CONFLICT(ItemCode) DO UPDATE` upsert syntax.
- Triggered manually via durably-stored Quartz job key (can be triggered via API).

### `NeonSyncJob`
- **Interval**: 5 minutes
- **Only runs if `NeonDb` connection string is configured.**
- Mirrors SQLite data to Neon PostgreSQL in one transaction per entity type.
- Uses `TRUNCATE ... RESTART IDENTITY CASCADE` then bulk-insert per table.
- Covers: Products, Customers, Invoices, InvoiceLines, InvoicePayments, OrderHeaders, OrderLines.
- Each entity sync is wrapped in `RunSafe()` so a failure in one entity does not block others.

### `InvoiceStatusCacheJob` / `CacheInvoiceStatusJob`
- **Schedule**: Daily at 5:00 AM
- Pre-compute and cache detailed invoice status reports into `DetailedInvoiceStatusCache`.
- ⚠️ Both are currently scheduled at the same time — one may be redundant.

---

## SyncMetadata Watermark

The `SyncMetadata` table has one row per entity type (e.g., `"Invoice"`, `"Order"`, `"Product"`):

```
Type        | LastSyncedAt
------------|---------------------------
Invoice     | 2026-04-10 01:08:14.000
Order       | 2026-04-10 01:05:33.000
Product     | 2026-04-09 23:12:00.000
```

Delta jobs read `LastSyncedAt` as their `from` window and write a new value only after a successful upsert. This prevents data loss if SAP or the DB is temporarily unavailable.

---

## Adding a New Sync Job

1. Create `Jobs/MyEntitySyncJob.cs` implementing `IJob` with `[DisallowConcurrentExecution]`.
2. Implement the sync logic in the corresponding cache service.
3. Register the job in `Program.cs`:
   ```csharp
   q.AddJobAndTrigger<MyEntitySyncJob>("MyEntitySyncJob", TimeSpan.FromMinutes(5));
   ```
4. Register for DI:
   ```csharp
   builder.Services.AddScoped<MyEntitySyncJob>();
   ```
5. If the job adds a new column to a Neon table, update `NeonSyncJob.cs` to include the column in the INSERT statement.

---

## Neon Schema Maintenance

When you add a column to a SQLite model (and add a migration), you **must also**:
1. Run an `ALTER TABLE` on the Neon PostgreSQL database to add the matching column.
2. Update the `INSERT` statement in `NeonSyncJob.cs` to include the new column and its parameter.

Failure to do step 2 will cause a `NOT NULL constraint` violation in NeonSyncJob (see [CHANGELOG.md](CHANGELOG.md)).
