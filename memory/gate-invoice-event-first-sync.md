---
name: gate-invoice-event-first-sync
description: Invoice Event-First + Drift Detection Optimization — all gates implemented, IF01-IF18 passing
metadata:
  type: project
---

Implementation complete as of 2026-09-16 on branch feature/tiered-zone-allocation.

**GATE 2 — UDF field pipeline fixed:**
- `InvoiceDto.cs` / `InvoiceLineDto`: added `U_MDLTsT (string?)` (was missing)
- `SapService.GetInvoiceByDocEntryAsync` + `GetInvoices`: both queries now SELECT U_MDLTsT (OITM), U_ItemName (INV1), U_Manufacturer (INV1); readers map all three
- `InvoiceMirrorRefreshService.MapToLines` + `InvoiceCacheService.UpsertInvoicesAndLinesAsync`: both flat-mapping paths now set all 5 UDF fields
- WhsCode: NOT in CachedInvoiceLine — reported as out-of-scope, not silently added

**GATE 6 — InvoiceMirrorRefreshService created:**
- File: `Services/Events/InvoiceMirrorRefreshService.cs`
- `RefreshAsync(int docEntry, CancellationToken ct) → RefreshResult`
- Contains canonical `MapToHeader`, `MapToLines`, `ComputeLegacyStatusDisplay` (all `internal static`)
- `RefreshResult` record: Ok, Error, DocEntry, DocNum, LineCount, SapReadMs, SqliteMs, NeonMs, TotalMs, Dto, Header, Lines
- `InvoiceEventHandler` refactored: constructor now takes `InvoiceMirrorRefreshService` instead of `InvoiceCacheService` + `NeonEventWriteService`; all side effects (OINM, ZF, inventory, delivery, cancellation-pair) remain in handler; cancellation-pair now calls `RefreshAsync` too
- `[INVOICE-FASTPATH]` log milestones preserved (SAP_READ_DONE, SQLITE_DONE, NEON_DONE, DONE)
- `Program.cs`: `AddScoped<InvoiceMirrorRefreshService>()` in the Neon-guarded block, before InvoiceEventHandler registration

**GATE 8 — NeonSyncJob incremental path fixed:**
- `ReconcileInvoicesAsync(false)`: replaced `TRUNCATE InvoiceLines` with `DELETE FROM InvoiceLines WHERE DocEntry = ANY(@des)`
- Only removes lines for DocEntries currently present in SQLite; lines from event fast-path for not-yet-synced DocEntries are preserved
- Full path (`full=true`) unchanged: still TRUNCATE + RecomputeSql

**GATE 5+7+10+12 — InvoiceDriftDetectionJob created:**
- File: `Jobs/InvoiceDriftDetectionJob.cs`
- `SapService.GetChangedInvoiceDocEntries(DateTime since)` added — queries OINV WHERE UpdateDate >= since
- WatermarkKey = "InvoiceDrift" (SyncMetadata)
- MaxRepairBatch = 100 per run
- Logs `[INVOICE-DRIFT] START`, `[INVOICE-DRIFT] Found=`, `[INVOICE-DRIFT] DONE` with repair stats
- Schedule: "0 0/15 * * * ?" — every 15 min
- `Program.cs`: `AddCronJobAndTrigger<InvoiceDriftDetectionJob>` in Neon-guarded Quartz block; `AddScoped<InvoiceDriftDetectionJob>` in Neon-guarded service block

**GATE 16 — Tests:**
- `SapReplitAPI.Tests/Invoice/InvoiceMirrorFreshnessTests.cs` — IF01-IF18
- 18/18 passed, 0 failed; 24 pre-existing CreditMemoEventCacheTests failures unaffected

**Why:** GATE 0 hard constraint preserved — zero changes to OINV creation, ODLN automation, ZF, Tiered, pick reconciliation, Customer Returns ORRR/ORIN, Credit Memo creation, ORCT, customer creation, inventory rules, frontend API field names, Offline V2.

**How to apply:** GATE 18 (VS MSBuild + deploy) remains pending; use `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe /t:Build /p:Configuration=Release` for the main project.
