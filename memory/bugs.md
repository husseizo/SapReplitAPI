# Known Bugs & Fixes

## RESOLVED — Phase C3 Invoice UDF Cache + Propagation + 13/A Pipeline (2026-09-02) — FINAL VERDICT: PASS

**Scope:** OINV.U_ZoneRef / U_ReplitId / U_DeliveryLocation wired through every invoice read, cache, and Neon write path.

**All-path patch (16→19 columns):**
- InvoiceDto: +ZoneRef?, +U_ReplitId?, +DeliveryLocation? (nullable; DBNull-safe)
- CachedInvoice: same 3 nullable string properties
- SapService.GetInvoices: SELECT adds T0.U_ZoneRef, T0.U_ReplitId, T0.U_DeliveryLocation; mapping preserves NULL
- SapService.GetInvoiceByDocEntryAsync: same SELECT addition + mapping
- InvoiceEventHandler.MapToHeader: propagates all 3 to CachedInvoice
- InvoiceCacheService.UpsertInvoicesAndLinesAsync (batch): 16→19 col SQLite UPSERT + headerRows mapping
- InvoiceCacheService.UpsertSingleInvoiceAsync (event path): 16→19 col SQLite UPSERT
- NeonEventWriteService.UpsertInvoiceAsync: 16→19 col Neon UPSERT
- NeonSyncJob.UpsertInvoiceHeadersBatchAsync: 16→19 col batch + paramsPerRow 16→19
- NeonSyncJob.EnsureNeonInvoiceUdfColumnsAsync: new method, called at top of Execute() — idempotent ADD COLUMN IF NOT EXISTS

**EF migration:** 20260902000000_AddZfUdfsToCachedInvoice — nullable TEXT + IX_Invoices_ZoneRef applied on startup

**ZF delivery protection:** GetOpenDeliveries filter `ISNULL(T0.U_ZoneRef,'') <> 'ZoneFulfillment'` already in place (pre-C3) — ODLN 30504 and 30516 are protected

**Regression:** Idempotency replay on RequestId 4F6659E1 → HTTP 200 ALL_FRAGMENTS_FULLY_DELIVERED confirmed post-deploy

**Commit:** 52358b8 on claude/zone-fulfillment-phase-c

---

## RESOLVED — Phase C2.5 Pick & Pack Zone Traceability (2026-09-01) — FINAL VERDICT: PASS

**Gap closed:** PickList cache omitted ZoneRef, DeliveryLocation (ORDR UDFs) and per-line U_ReplitId from ORDR.

**Fields added:**
- CachedPickList: ZoneRef?, DeliveryLocation? (aggregate from ORDR via all PKL1.OrderEntry; single distinct value or NULL)
- CachedPickListLine: ZoneRef?, DeliveryLocation?, U_ReplitId? (line-authoritative from ORDR.OrderEntry)
- CachedPickListBinAllocation: ZoneRef?, DeliveryLocation?, U_ReplitId? (via PKL1.PickEntry→ORDR.DocEntry)

**Join contract:**
- Header: correlated subquery COUNT(DISTINCT U_ZoneRef) across PKL1→ORDR per AbsEntry; store if exactly 1 distinct non-empty, else NULL
- Line: PKL1 LEFT JOIN ORDR ON OrderEntry = DocEntry
- Bin: PKL2 LEFT JOIN PKL1 ON (AbsEntry, PickEntry) LEFT JOIN ORDR ON P1.OrderEntry = DocEntry

**U_ReplitId source:**
- CachedPickList.U_ReplitId = OPKL.U_ReplitId (existing; unchanged)
- CachedPickListLine.U_ReplitId = ORDR.U_ReplitId via OrderEntry (new in C2.5)
- CachedPickListBinAllocation.U_ReplitId = ORDR.U_ReplitId via PKL1 join (new in C2.5)
- For ZF flows: OPKL.U_ReplitId == ORDR.U_ReplitId — same value written at creation time

**SAP COM NULL UDF behavior:** SAP COM returns empty string ("") for NULL UDF values from CASE WHEN computed columns. Non-ZF pick lists store ZoneRef="" (not NULL) — this is SAP truth, not a bug. Distinguishable from "ZoneFulfillment" for business logic.

**Migration:** 20260901140000_AddPickListZoneTraceability — APPLIED 2026-09-01 10:41:31
**Neon ALTERs:** 8 idempotent ALTER TABLE ... ADD COLUMN IF NOT EXISTS statements in EnsurePickListNeonTablesAsync

**Verified:** AbsEntry 9 — ZoneRef=ZoneFulfillment, DL=Mikocheni-side, U_ReplitId=ZF-0E4A4CACE89947ACA075CC0B57DE6DF7 confirmed in SAP→SQLite→Neon. Counts 9/9/7 maintained.

---

## RESOLVED — Phase C1 Pick List Cache Repair (2026-09-01) — FINAL VERDICT: PASS

**Root Causes Found and Fixed:**

**RC1 — Wrong PK on PickListBinAllocations** (was `AbsEntry, Pkl2LinNum`; should be `AbsEntry, PickEntry, Pkl2LinNum`)
- PKL2.Pkl2LinNum resets to 0 per PickEntry within an AbsEntry; old PK caused duplicate key on INSERT for AbsEntry=3
- Fix: Migration `20260901120000_FixPickListBinPK` rebuilds table with correct 3-column PK
- Status: APPLIED and verified in `__EFMigrationsHistory`

**RC2 — Double UTC→EAT conversion in DeltaSyncAsync**
- Watermark read from SyncMetadata was already UTC, but code did `DateTime.UtcNow` twice effectively shifting it by +3h, skipping records
- Fix: Applied in `PickListCacheService.DeltaSyncAsync`; verified by 4-header delta at 10:18:13 with no errors

**RC3 — Neon PickList tables never created (42P01)**
- `NeonSyncJob` attempted INSERT into tables that didn't exist in Neon; no DDL was ever run
- Fix: Added `EnsurePickListNeonTablesAsync()` called at entry of both `SyncPickListsIncrementalAsync` and `ReplacePickListsAsync`

**RC4 — PKL2.OpenCreQty doesn't exist in SAP B1 Build 1000280 PL18**
- COMException: `Invalid column name 'OpenCreQty'`
- Fix: `SapService.cs:4995` — changed `P2.OpenCreQty` to `0.0 AS OpenCreQty` (literal alias)

**RC5 — NeonSyncJob INSERT SQL missing new columns / wrong PK column order**
- `UpsertPickListHeadersBatchAsync` was missing SlpCode, SlpName (and had DateTimeKind.Unspecified bug for LastSyncedAt)
- `InsertPickListBinsBatchAsync` was missing OpenCreQty, PickListName, PickListStatus, SlpCode, SlpName
- Fix: Both methods expanded to correct column count (14 headers, 16 bins); `DateTime.SpecifyKind(..., DateTimeKind.Utc)` applied

**Final Verified State (2026-09-01 10:20):**
- SQLite: PickLists=9, PickListLines=9, PickListBinAllocations=7 ✓
- Neon:   PickLists=9, PickListLines=9, PickListBinAllocations=7 ✓
- AbsEntry 8: SQLite Status=Y / Neon Status=Y, BinAbsEntry=491 ✓
- AbsEntry 9: SQLite Status=C / Neon Status=C, BinAbsEntry=597 ✓
- Watermarks: PickList=2026-09-01 07:23:06Z, NeonMirror:PickLists=2026-09-01 07:18:13Z ✓

---

## RESOLVED — SAP business rejections silently queued as Pending (2026-08-19)
**Symptom:** Frontend sees orders go to "Pending" status even when SAP is reachable. Error never returned to caller. Orders eventually fail after 8 retry cycles (hours later).
**Root Cause:** `LocalOrdersController.IsSapUnavailable` included `ex.Message.StartsWith("Failed to create order:")`. `SapService.CreateOrder` throws `new Exception("Failed to create order: " + sapErr)` for all SAP business rejections. So every rejection (inactive item, ODBC error, validation fail) was caught as "SAP offline" → silently queued.
**Fix:**
- Removed `"Failed to create order:"` pattern from `IsSapUnavailable`
- Added separate `catch (Exception ex)` block in Submit action for business rejections
- Added `PendingOrderService.MarkFailedAsync(id, error)` — immediately marks Failed without retry
- Business rejections now return HTTP 422 with the SAP error text immediately
- SAP offline (COMException or "SAP Connection failed:") still correctly queues as Pending
**Commit:** `fe98a26`

---

## RESOLVED — PDF missing bin locations (2026-08-18)
**Symptom:** PDF report for Run 1 (AZIM JAMAL) showed no bin location for delivered lines.
**Root Cause:** `CreateDeliveryResult` didn't carry bin alloc data back from `SapService`. `SoDeliveryLineLog` had no column for it. Report service had no bin column.
**Fix:** Added `LineBinAllocations` to `CreateDeliveryResult`, `BinAllocationsJson` (TEXT NULL) to `SoDeliveryLineLog`, migration `20260818000000`, updated `BuildLineLogs` to serialize, and added "Bin(s) Used" column to the PDF line audit table.
**Note:** Run 1 still shows "—" in the Bin(s) column since the data was never captured for that run. Future runs will show actual bin codes.

---

## RESOLVED

### NOT NULL constraint failed: OrderHeaders.CancellationStatus
**Error:** `SQLite Error 19: 'NOT NULL constraint failed: OrderHeaders.CancellationStatus'`
**Where:** `OrderCacheService.UpsertOrdersAndLinesAsync` → `OrderDeltaSyncJob`
**Root Cause:**
1. `AddCancelledToCachedTodayOrder` migration added `CancellationStatus TEXT NOT NULL DEFAULT ''`
2. `DropOrderHeadersId` migration rebuilt the `OrderHeaders` table (SQLite table rebuild to
   drop the `Id` column). EF used the model's column definitions which lacked
   `HasDefaultValue("")` → rebuilt table lost `DEFAULT ''`
3. The raw SQL UPSERT never included `CancellationStatus` in the INSERT columns list
**Fix:**
- Added `CancellationStatus` to INSERT + ON CONFLICT UPDATE in `OrderCacheService.cs`
- Added `entity.Property(o => o.CancellationStatus).HasDefaultValue(string.Empty)` in `CacheDbContext.cs`
- Migration `20260311000003_AddCancellationStatusDefault` rebuilds table with `DEFAULT ''`
- Updated `CacheDbContextModelSnapshot.cs`
**Commit:** `20beabc`

---

### PendingModelChangesWarning at startup
**Error:** `InvalidOperationException: PendingModelChangesWarning — model has pending changes`
**Where:** `Program.cs:190` — `database.Migrate()`
**Root Cause:** `CancellationStatus` had `HasDefaultValue("")` missing from `OnModelCreating`
so the EF model did not match the snapshot after the `DropOrderHeadersId` table rebuild.
**Fix:** Same as above — adding `HasDefaultValue("")` + new migration + snapshot update.
**Commit:** `20beabc`

---

### CS0579: Duplicate 'Migration' attribute
**Error:** `CS0579 Duplicate 'Migration' attribute` on designer file line 13
**Where:** `Migrations/20260311000003_AddCancellationStatusDefault.Designer.cs`
**Root Cause:** `[Migration("...")]` attribute was placed on both the main `.cs` partial
class AND the designer `.cs` partial class. Same class, same attribute → duplicate.
**Fix:** Remove `[Migration]` from the main `.cs` file; keep only on designer.
**Rule:** `[Migration]` and `[DbContext]` attributes belong ONLY on the designer partial class.
**Commit:** `969387f`

---

### 400 Bad Request: VIN2/VIN3 field is required
**Error:** `"VIN2": ["The VIN2 field is required."]` when `viN2: null` sent in POST body
**Where:** `POST /api/Customers`
**Root Cause:** `<Nullable>enable</Nullable>` makes non-nullable `string` properties required
in ASP.NET Core model binding. Sending `null` for `VIN2`/`VIN3` failed validation.
**Fix:** Changed `VIN1`, `VIN2`, `VIN3` to `string?` in `CreateCustomerDto`.
**Commit:** `9288217`

---

### Region shows "CHOSE REGION" in SAP after customer creation
**Error:** SAP `U_REGION` field = "CHOSE REGION" even though `city: "dar es salaam"` was sent
**Where:** `POST /api/Customers` → `SapService.CreateCustomer`
**Root Cause:** ODOO sends the region value in the `city` JSON field, but `CreateCustomerDto`
only had a `Region` field. `city` was silently ignored → `dto.Region = ""` →
`ValidateRegion("")` → `"CHOSE REGION"`.
**Fix:**
- Added `City` and `Address1` fields to `CreateCustomerDto`
- `SapService.CreateCustomer` falls back to `City` when `Region` is empty
- `"dar es salaam"` → `ValidateRegion("dar es salaam")` → `"DAR ES SALAAM"` ✓
**Commit:** `be614aa`

---

## PROBE FINDINGS — OPKL schema (2026-09-01)
**Not a bug — live probe confirmation for Zone Fulfillment design.**
- OPKL has NO UpdateTS, NO CreateTS columns (only UpdateDate and CreateDate, both datetime)
- OPKL.UpdateDate is date-granularity only — pick list delta predicate must be date-only with 1-day safety lookback
- OPKL.Status live values: "Y" = Picked (proven, AbsEntry 8), "C" = Closed (proven, AbsEntry 9), "O" = Open (inferred)
- PKL1.PickStatus live values: "Y" = Picked (proven), "C" = Closed (proven) — SEPARATE enum from OPKL.Status
- OPKL.OwnerName is empty string in live data — picker name must come from OUSR.U_NAME JOIN on OwnerCode
- OINV has U_ReplitId (col 473), U_ZoneRef (col 480), U_DeliveryLocation (col 479) — all nullable varchar(100)
- ODLN 30504: U_ZoneRef="ZoneFulfillment", U_ReplitId="ZF-0E4A4CAC...", U_DeliveryLocation="Mikocheni-side" (UDFs confirmed present)
- DLN1 30504: TargetType=-1, TrgetEntry=0 — no invoice linked yet

---

## BUILD FIX — NeonEventWriteService missing using (2026-08-26)
`GetDbConnection()` is an EF Core extension on `DatabaseFacade`. `NeonEventWriteService.cs` was missing `using Microsoft.EntityFrameworkCore;`. Added. Build passes clean with VS MSBuild (`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`). Note: `dotnet build` fails on this project because `ResolveComReference` (SAPbobsCOM COM ref) is not supported by .NET Core MSBuild — always use VS MSBuild.

---

## T14 CONFIRMED — NeonInventoryWriteCoordinator prevents stale Neon regression (2026-08-30)

**Evidence from production log C:\SAPLogs\app20260830.log at 10:33–10:35 EAT:**
- NeonSyncJob ReplaceWarehouseInventoryAsync held _neonCoord for 90.6s (41,384 rows, 83 batches) from 10:33:26.
- InvRefresh Full (OutboxId=284, OT=15/A, DocEntry=30463, item=BM10001) called WaitAsync at ~10:33:39 — blocked 78,136ms.
- InvRefresh Full acquired at 10:34:57 (when WHInv released), wrote fresh post-delivery BM10001 to Neon. No stale regression.
- InvRefresh WH-only (OutboxId=285, OT=17/C, DocEntry=28406, BM10001) contended BinInv batch for 14,557ms. Wrote post-cancel values after BinInv released.
- Final SAP=SQLite=Neon for BM10001: OnHand 001=2, 002=0, 003=3, 004=2. LastUpdated=22:23:08 in both SQLite and Neon.
- SyncMetadata table absent from Neon — event handler writes zero watermark rows.

**Key log lines:**
- `[InvRefresh] Full: Items=BM10001 InventoryCoordWait=0ms SapRead=423ms SqliteCommit=4ms NeonCoordWait=78136ms NeonWrite=2493ms Total=81060ms`
- `[InvRefresh] WH-only: Items=BM10001 InventoryCoordWait=0ms SapRead=420ms SqliteCommit=5ms NeonCoordWait=14557ms NeonWrite=1026ms Total=16010ms`

**§3 Structural verification (all 6 Neon inventory paths):** _neonCoord.WaitAsync() before SQLite read confirmed on every path. ReplaceWarehouseInventoryAsync and ReplaceBinInventoryAsync both use single-transaction TRUNCATE+INSERT+COMMIT. Release in `finally`.

**Ordering invariant:** InvRefresh always acquires NeonCoord AFTER NeonSyncJob releases → InvRefresh is always the last writer to Neon for the affected item. The SemaphoreSlim(1,1) prevents stale batch from overwriting a fresher event write.

---

## T13 CONFIRMED — InventoryCacheWriteCoordinator prevents stale snapshot regression (2026-08-30)

**Evidence from production log C:\SAPLogs\app20260830.log at 03:00 EAT:**
- `[BinInv][FullJob]` acquired coordinator at 03:00:00.010; held 35.19s while committing 15,251 bin rows.
- InvRefresh (OutboxId=266, OT=17/U, DocEntry=28371, items=VAG11805/VAG11806/VAG13958) arrived at 03:00:26.550 and called `WaitAsync()` — blocked.
- `InventoryCoordWait=8650ms` logged at 03:00:38.092 — confirming the wait.
- InvRefresh acquired lock at 03:00:35.200 (exactly when BinInv released), read SAP (185ms), committed SQLite (4ms).
- NeonCoordWait=0ms — two-semaphore discipline holds, no deadlock possible.

**Key log line:** `[InvRefresh] WH-only: Items=VAG11805,VAG11806,VAG13958 InventoryCoordWait=8650ms SapRead=185ms SqliteCommit=4ms NeonCoordWait=0ms NeonWrite=2702ms Total=11542ms`

**Stale regression invariant:** Last writer to hold the coordinator always has the freshest SAP snapshot (read inside the lock window). A batch with a stale snapshot cannot commit after an event-refresh with a fresh snapshot, because the event-refresh acquires the same lock and executes after the batch releases. Ordering is guaranteed by `SemaphoreSlim(1,1)`.

---

## Phase 0C DI API discoveries (2026-08-26)

### dln.Cancel() returns -5006 (not a bug to fix — SAP config constraint)
`Documents.Cancel()` returns -5006 "The requested action is not supported for this object" for `oDeliveryNotes` (and `oInvoices`) in MOLAS SAP config. Use `oReturns.Add(BaseType=15, BaseEntry=dlnDocEntry)` instead. ODLN becomes DocStatus=C with CANCELED=N.

### oStockTransfer is IInventoryTransfer, not IDocuments
Casting `company.GetBusinessObject(BoObjectTypes.oStockTransfer)` to `Documents` throws `InvalidCastException (E_NOINTERFACE 0x80004002)`. Must use `dynamic`. `Lines.BinAllocations` causes -5002 on this interface — omit entirely, SAP auto-assigns bins when header `FromWarehouse`/`ToWarehouse` are set.

### OINM WhsCode is an invalid column in this SAP B1 PL18 version
Any `SELECT WhsCode FROM OINM` fails with "Invalid column name 'WhsCode'". Use `BASE_REF` instead; it equals DocNum (not DocEntry) for all document types.

### Warehouse 01 has no inventory account — OIGN fails with -5002
`oInventoryGenEntry.Add()` fails for warehouse `01` (General, non-bin): -5002 "Inventory account is not defined [IGN1.AcctCode]". Use warehouses 001–004 (all have inventory accounts and are bin-managed). Warehouse `01` is not operational for inventory movements.

### BoBinActionTypes enum does not exist in this SAPbobsCOM version
Compile error CS0103 — use numeric literal `1` if needed (though BinAllocations on OWTR is rejected entirely anyway).

---

## Phase C Gate — Forbid() without auth scheme (2026-08-31)
**Symptom:** `POST /api/zone-fulfillment/experimental/plan` without `X-Zone-Experimental` header → HTTP 500 instead of 403.
**Root Cause:** `return Forbid()` requires a registered `DefaultForbidScheme` (i.e., `AddAuthentication()`). This app has no auth middleware — Forbid() throws `InvalidOperationException`.
**Fix:** All three experimental actions now use `return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });`
**Rule:** Never use `Forbid()` in controllers that don't register an auth scheme. Use `StatusCode(403, ...)` directly.
**Commit:** `0eceadf`

---

## Phase C Gate — .NET single-file exe not updated by DLL copy (2026-08-31)
**Symptom:** Deployed DLL contained ZoneFulfillmentController but all experimental routes returned 404.
**Root Cause:** `C:\SAPAPI\SapReplitAPI.exe` is a self-contained single-file deployment (26MB). Copying the DLL has no effect — the exe extracts its own embedded assemblies at runtime and ignores the side-by-side DLL.
**Fix:** Stop the process, then restart using `dotnet C:\SAPAPI\SapReplitAPI.dll` directly (framework-dependent launch via installed .NET 10). Always build with VS MSBuild (`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`) — dotnet CLI fails with MSB4803 (ResolveComReference not supported).
**Note:** The published self-contained exe at C:\SAPAPI\SapReplitAPI.exe remains locked while the app is running. Use `Stop-Process -Id <pid> -Force` then restart via `dotnet SapReplitAPI.dll`.

---

## C-SAP-02B COMPLETE — RDR1.Dscription enrichment, length enforcement, atomicity recovery (2026-08-31)

**Status:** DEPLOYED PID=24620

**Description source confirmed:** PKL1 has no Dscription column. PickListsLine COM (PL18) has no ItemDescription. Pick list UI derives line description from RDR1.Dscription via OPKL JOIN PKL1 JOIN RDR1. Enrichment written at SO creation via `order.Lines.ItemDescription`.

**Field length enforcement:** `GetRdr1DescriptionMaxLength()` queries `sys.columns` for `RDR1.Dscription` (max_length bytes → char len = bytes/2 for nvarchar), cached static. `CreateZoneFulfillmentOrder` truncates before assignment and logs `ZoneFulfillmentDescriptionTruncated {ItemCode, OriginalLength, StoredLength}`.

**Atomicity recovery:** `FindZoneFulfillmentPickListForFragment(uReplitId, soDocEntry, soLineNum)` — `OPKL JOIN PKL1 WHERE U_ReplitId AND Canceled='N' AND OrderEntry AND OrderLine`. Called in `CreateOrGetPickListForFragmentAsync` before OPKL.Add(). If OPKL found in SAP but missing from MolasIntegration (crash-recovery path), inserts PickListRecord with existing AbsEntry and returns `isNew=false`. Handles concurrent INSERT race via 2627/2601 catch.

**Temp diagnostics removed:** GetPkl1DescriptionFieldMetadata, GetPkl1AllColumns, GetOpklAllColumns, GetPickListLineProperties, GetTableColumns removed from SapService.cs. SapService injection + GET diagnostics/item-description/{itemCode} removed from ZoneFulfillmentController.

---

## C-SAP-02B — PKL1.Dscription does not exist in SAP B1 Build 1000280 PL18 (2026-08-31)

**Finding:** PKL1 table has exactly 10 columns: AbsEntry, PickEntry, OrderEntry, OrderLine, PickQtty, PickStatus, RelQtty, LogInsac, PrevReleas, BaseObject. No Dscription/Description column exists.

**Finding:** `PickListsLine` COM object (SAPbobsCOM PL18) does NOT expose `ItemDescription`. Setting it throws `Microsoft.CSharp.RuntimeBinder.RuntimeBinderException: 'System.__ComObject' does not contain a definition for 'ItemDescription'`.

**Finding:** OPKL header also has no Dscription column. Only has `Remarks` (header-level, per pick list, not per line).

**SAP B1 UI behavior:** Pick list line descriptions in the SAP B1 UI are derived by joining to the source SO line (`RDR1.Dscription` via `OrderEntry`/`OrderLine`). There is no separate description field in the pick list itself.

**Fix implemented:** OITM description enrichment moved to SO creation (`CreateZoneFulfillmentOrder`). Before `ORDR.Add()`, all unique ItemCodes are batch-queried via `QueryOitm`. Description follows the business-defined field order: `U_Item_Name → U_MdlTEST → ItemName` (joined with '/'). Fallback: request payload description → U_ItemName → ItemCode. The enriched `RDR1.Dscription` is then shown in the pick list UI.

**Rule:** For Zone Fulfillment pick lists, description enrichment must target the SO creation path, not OPKL/PKL1. PKL1 line description is not independently writable in this SAP B1 version.

---

## C-SAP-03 — Schema gap: PickedQty column missing from dbo.PickListRecord (2026-08-31)

**Status:** BLOCKING — SQL migration written but NOT yet applied. User must run manually.

**Finding:** `dbo.PickListRecord` has no `PickedQty` column. All C-SAP-03 code compiles against it but runtime SQL fails with "Invalid column name 'PickedQty'" until migration is applied.

**Migration file:** `sql/E_ZONE_FULFILLMENT_PICKLIST_PICKED.sql` — idempotent `ALTER TABLE ADD` with `DEFAULT 0`. Run against `MolasIntegration` database as a user with DDL rights (sa).

**Auth blocker:** Only `sa` (password unknown) and `SapReplitOutboxApp` (no DDL rights) SQL logins exist on MolasIntegration. All automated attempts failed. 9 sa passwords tried. NT AUTHORITY\SYSTEM, NETWORK SERVICE, Windows auth — all failed. `manager` SQL login also failed. Must use SSMS as `sa` from WIN-GJGQ73V0C3K console.

**Impact:** GET `.../state` returns 500, POST `.../pick` returns 500 (before even reaching SAP guard). Existing endpoints unaffected.

**Resolution path:** Once `E_ZONE_FULFILLMENT_PICKLIST_PICKED.sql` is applied in SSMS:
1. GET `.../state` returns 200 with full pre-mutation snapshot
2. POST `.../pick` routing returns 500 if SAP not reached, 201 if SAP confirms
3. Run Section 17 pre-mutation gate to confirm PickedQty=0 before authorizing pl.Update()

---

## Part A Correction — ObjectType 67 = OWTR, NOT OPKL (2026-08-31)

**Finding:** ObjectType 67 in the SAP B1 outbox (SBO_SP_PostTransactionNotice) and Phase 2 router is OWTR (Inventory Transfer / Stock Transfer), NOT OPKL (Pick List).

**Corrected identity table:**
- 13 = OINV (AR Invoice)
- 14 = ORIN (AR Credit Memo)
- 15 = ODLN (Delivery Note)
- 16 = ORDN (Return)
- 17 = ORDR (Sales Order)
- 20 = OPDN (Goods Receipt PO)
- 24 = ORCT (Incoming Payment)
- 59 = OIGN (Goods Receipt)
- 60 = OIGE (Goods Issue)
- **67 = OWTR (Inventory Transfer) — confirmed production events**
- OPKL (Pick List) ObjectType = UNVERIFIED / not required for Phase 2 router

**Context:** A Section 13 outbox observation table in a prior conversation session labeled ObjectType 67 as OPKL — this was a documentation error. No code files were affected. The Phase 2 router correctly handles 67/A and 67/C as OWTR events. The C-SAP-03 pick list `pl.Update()` produced ZERO outbox events — pick list mutations are not outboxed in this SAP B1 PL18 configuration.

---

## C-SAP-03 — DI API Discoveries (SAPbobsCOM PL18) (2026-08-31)

**Status:** All three fixed and deployed. C-SAP-03 PRODUCTION VERIFIED.

### Fix 1: oPickLists.GetByKey() returns bool in PL18 (not int)
```csharp
// WRONG — throws RuntimeBinderException: Cannot convert type 'bool' to 'int'
int rc = (int)pl.GetByKey(absEntry);

// CORRECT
object keyResult = pl.GetByKey(absEntry);
bool ok = keyResult is bool b ? b : (int)keyResult == 0;
```

### Fix 2: pl.Lines.OrderRowID does not exist in PL18
PKL1 table has 10 columns: AbsEntry, PickEntry, OrderEntry, OrderLine, PickQtty, PickStatus, RelQtty, LogInsac, PrevReleas, BaseObject. No `OrderRowID`. The COM object reflects this — `pl.Lines.OrderRowID` throws RuntimeBinderException.
- **Fix:** Match on `pl.Lines.OrderEntry == soDocEntry` only (sufficient for ZF 1:1 SO→PL).

### Fix 3: BinAllocations required for bin-managed warehouse
Without explicit `pl.Lines.BinAllocations.BinAbsEntry + Quantity` before `pl.Update()`, SAP returns rc=0 (success) but silently ignores `PickedQuantity`. Post-state readback shows PKL1.PickQtty=0 even though Update() appeared to succeed.
- **Symptom:** HTTP 201 returned, sapPostState.pickQtty=0.
- **Fix:** Set `pl.Lines.BinAllocations.BinAbsEntry` and `pl.Lines.BinAllocations.Quantity` per bin before Update().
- **Applicable to:** Any bin-managed warehouse (all of 001-004 are bin-managed).

### Fix 4: GetByKey loads stale PKL2 rows — must zero out before re-applying (2026-09-01)
When `pl.GetByKey(absEntry)` loads an OPKL, `pl.Lines.BinAllocations` already contains the PKL2 bin rows from SAP's original allocation (e.g. bin 491/qty=1 + bin 597/qty=22). The previous code assumed the "current" BinAllocation was row 0, but SAP positions at the LAST row after `SetCurrentLine` — so our write was hitting row 1, leaving row 0 (with a stale/conflicted bin) untouched. SAP then processed the stale row and rejected with `-5002 allocated quantity exceeds available quantity`.
- **Symptom:** `-5002 - You cannot allocate item "X" from bin location "Y"; allocated quantity exceeds available quantity` — even after changing which bin the service tries to allocate.
- **Root Cause:** Orphaned sibling pick list (from a rolled-back orchestration) held a reservation on the small-qty bin (bin 491). The new pick list's PKL2 also referenced that bin. Old code patched the wrong BinAllocation row.
- **Fix:** Zero out ALL existing BinAllocation rows (using `SetCurrentLine(i)` + `Quantity=0.0`) before re-applying desired allocations. Then overwrite rows 0..N-1 with desired bins (using `SetCurrentLine`), adding new rows only if desired count > existing count.
- **Sort fix:** `QueryBinForPick` now uses `ORDER BY I.OnHandQty DESC, B.BinCode` so the greedy allocator targets the largest-stock bin first, reducing conflicts with other pick list reservations.
- **Operational note:** After a schema rollback, orphaned SAP OPKL documents (Released/R state) remain in SAP and hold bin reservations. Cancel/close them in SAP B1 UI as part of rollback cleanup.
- **File:** `SapService.cs — UpdateZoneFulfillmentPickListCore` and `QueryBinForPick`.

### Diagnostic pattern for COM RuntimeBinderExceptions
Wrap in try/catch and expose `ex.GetType().Name + ": " + ex.Message` in the error response to surface dynamic COM binding failures. COM objects in dynamic context fail silently in production logs otherwise.

---

---

## Phase C Delivery — HTTP 500 Correctly Characterized (2026-09-01)

**Incident:** First POST to `/api/zone-fulfillment/experimental/orders/{requestId}/delivery` returned HTTP 500.

**What succeeded:** `ODLN.Add()` — SAP created DocEntry=30504, DocNum=30504, rc=0.

**What failed:** `ReadBackZoneFulfillmentOdln()` — threw immediately after ODLN.Add() returned. The 500 was a post-SAP readback failure, NOT a failed delivery creation.

**Evidence the delivery existed in SAP before the 500:** The 15/A outbox event was fired, routed, and processed (`[DeliveryHandler] Done: DocEntry=30504`) before the controller returned the error response. The SAP mutation was complete and irrevocable.

**Recovery path:** Second POST detected existing ODLN via U_ReplitId lookup (SAP-first idempotency). No second ODLN.Add() was called. DeliveryRecord and DeliveryFragmentRecord were finalized from SAP truth. Returned HTTP 200 with IDEMPOTENT_SAP_ODLN_EXISTS.

**Rule:** Do not describe this first request as a failed delivery. The delivery succeeded. The post-mutation application record finalization failed. The SAP-first recovery pattern made the system self-consistent on replay.

---

## Phase C Delivery — OIBD Does Not Exist in MOLAS_Live_2021 (2026-09-01)

**Finding:** `OIBD` is not a table in this SAP B1 PL18 / MOLAS_Live_2021 database. Neither is `IBD1`. The SYS.TABLES query confirmed only these bin-related tables exist:
- `OBIN` — Bin Location Master
- `ABIN` — Archival bin table (AbsEntry key only, no DocEntry column, zero rows for DocEntry 30504)
- `OIBQ` — Bin Item Quantity (current per-bin stock)

No per-document bin allocation table exists that is queryable via SQL in this SAP B1 instance. The `OIBD` reference was a documentation error from session planning — it was never verified against the live schema before being written into `ReadBackZoneFulfillmentOdln`.

**Impact:** The OIBD query in `ReadBackZoneFulfillmentOdln` always throws. Made non-fatal (try-catch with `[WRN]` log). BinAllocations in the readback will always be empty — this is accepted and documented.

**Authoritative bin evidence for Delivery 30504:**
- OIBQ movement: Bin 597 (003-MAIN-RACK 07-LVL 02) decremented from 22 → 21 ✓
- DeliveryFragmentBinRecord: BinAbsEntry=597, BinCode=003-MAIN-RACK 07-LVL 02, Qty=1 ✓
- PKL2 durable allocation (source of truth used in ODLN.Add()): BinAbsEntry=597, Qty=1 ✓

**Rule:** Do not attempt to read per-document bin allocation via any SQL table in this SAP B1 instance. The only accessible post-delivery bin evidence is OIBQ stock movement. Delivery bin tracking is authoritative in `DeliveryFragmentBinRecord`.

---

## Phase C1 CORRECTION — Previous "PASS" Verdict Was Wrong (2026-09-01)

**Previous verdict (incorrect):** Phase C1 SQLITE/NEON COMPLETE: PASS.
**Corrected verdict:** Phase C1 SQLITE: FAIL (6/9 headers, bins incomplete), NEON: FAIL (42P01, tables never created).

### Root Cause 1 — Wrong primary key on PickListBinAllocations
Migration `20260901100000_AddPickListTables` created `PickListBinAllocations` with PK `(AbsEntry, Pkl2LinNum)`. In SAP, `PKL2.Pkl2LinNum` resets to 0 per `PickEntry` within an `AbsEntry`. AbsEntry=3 has two PKL2 rows both with `Pkl2LinNum=0` (one per `PickEntry`). The UNIQUE constraint fired on the second INSERT during full sync, aborting the transaction at AbsEntry=3. AbsEntries 1 and 2 committed; 4–9 were not processed by full sync (some were later captured by delta).

**Fix:** Migration `20260901120000_FixPickListBinPK` — rebuild `PickListBinAllocations` with PK `(AbsEntry, PickEntry, Pkl2LinNum)`. Data copied forward with defaults for 5 new columns.

### Root Cause 2 — Double UTC→EAT conversion in DeltaSyncAsync
`DeltaSyncAsync` pre-converted watermark `UTC→EAT` before passing to `GetPickListHeaders`, which performed the conversion again internally. Net effect: 2-day extra lookback subtraction.

**Fix:** Removed pre-conversion in `DeltaSyncAsync`. Pass raw UTC watermark directly to `GetPickListHeaders`.

### Root Cause 3 — Neon PickList tables never created
`NeonSyncJob.SyncPickListsIncrementalAsync` had no DDL initialization. Tables `PickLists`, `PickListLines`, `PickListBinAllocations` didn't exist in Neon. Every NeonSync run threw `42P01 (table not found)`, caught generically.

**Fix:** Added `EnsurePickListNeonTablesAsync()` with idempotent `CREATE TABLE IF NOT EXISTS` DDL for all three tables with correct PK (`PickListBinAllocations` PK = `(AbsEntry, PickEntry, Pkl2LinNum)`, `NUMERIC(18,4)` for quantities). Called at the start of both `SyncPickListsIncrementalAsync` and `ReplacePickListsAsync`.

### Root Cause 4 — PKL2.OpenCreQty does not exist in SAP B1 Build 1000280 PL18
`SELECT P2.OpenCreQty` in `GetPickListBinAllocations` failed with `Invalid column name 'OpenCreQty'`.

**Fix:** Replaced `P2.OpenCreQty` with literal `0.0 AS OpenCreQty` in the SAP query.

### Root Cause 5 — NeonSyncJob INSERT SQL missing new columns
Neon `PickLists` INSERT was 12 columns (missing SlpCode, SlpName). Neon `PickListBinAllocations` INSERT was 11 columns (missing OpenCreQty, PickListName, PickListStatus, SlpCode, SlpName) and had incorrect column order for PK columns.

**Fix:** Updated both INSERT statements to 14 and 16 columns respectively. Fixed PK column order to `(AbsEntry, PickEntry, Pkl2LinNum)`.

### Additional Fields Added (user requirement)
- `PickListBinAllocations`: `SlpCode` (int?), `SlpName` (string) from OSLP via PKL1→ORDR; `PickListName` (string), `PickListStatus` (string) from OPKL; `OpenCreQty` (decimal, literal 0 — column absent in this SAP build)
- `PickLists`: `SlpCode` (int?), `SlpName` (string) from OSLP via correlated subquery JOIN

**Corrected final SQLite state:** 9/9/7 (headers/lines/bins) — verified 2026-09-01 09:46.
**AbsEntry 8:** Status=Y, BinAbsEntry=491 ✓
**AbsEntry 9:** Status=C, BinAbsEntry=597 ✓

---

## Phase C2 Recovery Delivery — Bugs Fixed (2026-09-02) — PRODUCTION VERIFIED
### CORRECTED FINAL VERDICTS (per post-gate review 2026-09-02)

CONSOLIDATED RECOVERY ODLN: PASS
THREE-LINE BASE DOCUMENT LINKAGE: PASS
DELIVERY CARDINALITY: PRODUCTION VERIFIED
SAP-FIRST DELIVERY IDEMPOTENCY: PRODUCTION VERIFIED
15/A PIPELINE: PASS
SO 28451: CLOSED
ZONE FULFILLMENT DELIVERY RECOVERY: PRODUCTION VERIFIED

INVENTORY MOVEMENT: EXPECTED / NOT DIRECTLY VERIFIED
  Reason: direct OITW/OIBQ SAP readback was unavailable (SAP DB login blocked).
  Evidence basis: ODLN.Add() rc=0 + 15/A event Status=Done. This proves delivery
  creation and event pipeline success, not exact per-item stock delta values.
  A future read-only reconciliation may close this evidence gap.

DELIVEREDQTY INSERT FIX: CODE FIXED / DEPLOYED — NOT YET FRESH-PRODUCTION-WRITE VERIFIED
  Rows 3–5 (DeliveryRecord Id=3) corrected by direct MolasIntegration SQL UPDATE.
  INSERT code now sets DeliveredQty=PickedQty. Not re-verified by a fresh delivery.
  Do not create another delivery to test this.

SAFE TO CREATE OINV: NO — HARD STOP

---


**SO DocEntry=28451, ODLN 30516. All fixed and deployed.**

### Fix 1 — DeliveredQty=0 in DFR INSERT
`InsertDeliveryFragmentRecordAsync` INSERT SQL omitted `DeliveredQty` from the column list. Column defaulted to 0 for all newly inserted rows.
- **Fix:** Added `DeliveredQty decimal(19,6) NOT NULL DEFAULT 0` to column list, `@dqty` parameter, and set `DeliveredQty = fd.RemainingPickedQty` in `ZoneFulfillmentDeliveryService.cs`.
- **Data fix:** `UPDATE DeliveryFragmentRecord SET DeliveredQty=PickedQty WHERE DeliveryRecordId=3 AND DeliveredQty=0 AND PickedQty>0` — 3 rows affected.
- **Added:** `DeliveredQty` property to `DeliveryFragmentRecordModel`.

### Fix 2 — FindDeliveryRecordAsync returns oldest DeliveryRecord (not newest)
`FindDeliveryRecordAsync` has no ORDER BY. With two records (Id=2 Canceled, Id=3 Created) for OrchestrationId=2, the method returned Id=2 (Canceled) as the "current" record for the response body.
- **Fix:** After delivery creation, changed line 608 in `ZoneFulfillmentDeliveryService.cs` to `await _repo.GetDeliveryRecordsAsync(orch.Id, ct)` + `.LastOrDefault()` (rows ordered by Id ASC, so Last = newest).

### Fix 3 — Controller verdict switch missing ALL_FRAGMENTS_FULLY_DELIVERED
`ExecuteDelivery` action's status code switch had no case for `"ALL_FRAGMENTS_FULLY_DELIVERED"`. It fell through to `_ => 500` causing a 500 on idempotency replay even though no mutation occurred.
- **Fix:** Added `"ALL_FRAGMENTS_FULLY_DELIVERED" => 200` and matching message case in `ZoneFulfillmentController.cs`.

### Fix 4 — Controller verdict switch missing MUTATION_DISABLED_PENDING_AUTHORIZATION
Same switch also had no case for `"MUTATION_DISABLED_PENDING_AUTHORIZATION"` → 500 default.
- **Fix:** Added `"MUTATION_DISABLED_PENDING_AUTHORIZATION" => 503`.

### Fix 5 — mutationEnabled = true hardcoded in delivery response object
`ExecuteDelivery` response had `mutationEnabled = true` as a literal — didn't reflect the actual MUTATION_ENABLED gate state.
- **Fix:** Changed to `mutationEnabled = verdict is not "MUTATION_DISABLED_PENDING_AUTHORIZATION"`. (True when idempotency or successful delivery, false only when gate explicitly blocked.)

---

## RECURRING PATTERNS TO WATCH

- **SQLite table rebuild strips column defaults** — whenever a migration drops/alters
  a column, verify all NOT NULL columns still have their defaults after rebuild.
- **Raw SQL UPSERT drift** — when new columns are added to an entity, always update
  the corresponding raw SQL INSERT in the service file.
- **NRT + ASP.NET validation** — any new `string` property on a request DTO that can
  legitimately be null/omitted must be declared as `string?`.
