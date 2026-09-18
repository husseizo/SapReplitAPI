---
name: gate-invoice-event-first-sync
description: Invoice Event-First + Drift Detection + SQLite Qty Parity — all gates proven, IF01-IF18, DD01-DD05, SQ01-SQ10 passing
metadata:
  type: project
---

Implementation complete as of 2026-09-17 on master (commit 095f842).

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
- `SapReplitAPI.Tests/Invoice/InvoiceDriftCycleTests.cs` — DD01-DD05
- `SapReplitAPI.Tests/Invoice/InvoiceSqliteQuantityTests.cs` — SQ01-SQ10 (SQLite in-memory, EF Core SQLite)

**GATE 18 — Production build verified:**
- Release|x64: `MSBuild.exe SapReplitAPI.csproj /t:Build /p:Configuration=Release /p:Platform=x64`
- Output: `bin\x64\Release\net8.0\win-x64\SapReplitAPI.dll`
- Commit `095f842` on master — local HEAD == origin/master HEAD

**Test results (commit 095f842):**
- Total: 539 | Passed: 495 | Failed: 24 (CM* pre-existing baseline) | Skipped: 20
- New tests: IF01-IF18 + DD01-DD05 + SQ01-SQ10 = 33 new, all passing
- New failures: 0

**SQLite quantity preservation (already in code at lines 642-643 of UpsertSingleInvoiceAsync):**
- `ReturnedQuantityMirror.PreserveAsync` called before DELETE+INSERT
- `PendingReturnQuantityMirror.PreserveAsync` called before DELETE+INSERT
- SQ01-SQ10 prove the invariant with real SQLite in-memory backend
- NOTE: previous session summary incorrectly reported this as missing — it was already implemented

**Deployment 2026-09-17:**
- Binary: SapReplitAPI.exe 148KB / SapReplitAPI.dll 3625KB, ts: 9/17/2026 9:48 PM
- Backup: C:\SAPAPI\backups\pre_sqlite_qty_parity_20260917_220532
- Service: Running, PID=24248, Port 5050 owned by PID 24248 ✓
- Environment: Production
- First drift cycle post-deploy (22:15): Changed=0, TotalMs=11,146ms ✓

**Why:** GATE 0 hard constraint preserved — zero changes to OINV creation, ODLN automation, ZF, Tiered, pick reconciliation, Customer Returns ORRR/ORIN, Credit Memo creation, ORCT, customer creation, inventory rules, frontend API field names, Offline V2.

**How to apply:** Deploy from master commit `095f842`. Use Release|x64 MSBuild path above.

**Live 13/A proof pending:** Last invoice DocEntry=28678 (17:15 EAT on 9/17) was processed by prior binary. Next legitimate business 13/A event will be the live proof for commit 095f842.
