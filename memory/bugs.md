# Known Bugs & Fixes

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

---

## BUILD FIX — NeonEventWriteService missing using (2026-08-26)
`GetDbConnection()` is an EF Core extension on `DatabaseFacade`. `NeonEventWriteService.cs` was missing `using Microsoft.EntityFrameworkCore;`. Added. Build passes clean with VS MSBuild (`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`). Note: `dotnet build` fails on this project because `ResolveComReference` (SAPbobsCOM COM ref) is not supported by .NET Core MSBuild — always use VS MSBuild.

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

## RECURRING PATTERNS TO WATCH

- **SQLite table rebuild strips column defaults** — whenever a migration drops/alters
  a column, verify all NOT NULL columns still have their defaults after rebuild.
- **Raw SQL UPSERT drift** — when new columns are added to an entity, always update
  the corresponding raw SQL INSERT in the service file.
- **NRT + ASP.NET validation** — any new `string` property on a request DTO that can
  legitimately be null/omitted must be declared as `string?`.
