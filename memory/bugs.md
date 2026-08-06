# Known Bugs & Fixes

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

## RECURRING PATTERNS TO WATCH

- **SQLite table rebuild strips column defaults** — whenever a migration drops/alters
  a column, verify all NOT NULL columns still have their defaults after rebuild.
- **Raw SQL UPSERT drift** — when new columns are added to an entity, always update
  the corresponding raw SQL INSERT in the service file.
- **NRT + ASP.NET validation** — any new `string` property on a request DTO that can
  legitimately be null/omitted must be declared as `string?`.
