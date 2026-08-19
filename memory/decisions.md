# Technical Decisions

## EF Core Migrations

### Manual migrations must include a Designer.cs file
**Date:** 2026-03-11
**Decision:** Any manually created migration `.cs` file must also have a matching
`.Designer.cs` partial class that embeds the full model snapshot via `BuildTargetModel`.
**Rationale:** Without a designer file, EF Core cannot validate the migration chain.
The `[Migration("...")]` attribute belongs ONLY on the designer partial class — putting it
on both causes `CS0579: Duplicate attribute`.

### Never add [Migration] attribute to main migration .cs file when a designer exists
**Date:** 2026-03-11
**Rationale:** Both files are `partial class` of the same type. Attribute on both = CS0579.

### SQLite column default lost on table rebuild
**Date:** 2026-03-11
**Decision:** When EF Core rebuilds a SQLite table (e.g. to drop a column), it uses the
model's column definitions. If a column has `DEFAULT ''` in the DB but NOT `HasDefaultValue("")`
in `OnModelCreating`, the rebuilt table loses the default.
**Fix:** Always add `HasDefaultValue(string.Empty)` in `OnModelCreating` for any string
column that should have a DB-level default. Then create a migration to rebuild the table.

### Raw SQL UPSERTs must list every NOT NULL column explicitly
**Date:** 2026-03-11
**Decision:** The raw SQL INSERT in `UpsertOrdersAndLinesAsync` (and similar services) must
include every NOT NULL column. SQLite will throw Error 19 if any NOT NULL column with no
DEFAULT is omitted from an INSERT statement.

---

## API Design

### ODOO field mapping — city → region, address1 → address
**Date:** 2026-03-11
**Decision:** ODOO sends `city` for the SAP region value and `address1` for the street.
`CreateCustomerDto` accepts both the canonical names (`Region`, `Address`) and ODOO names
(`City`, `Address1`). `SapService.CreateCustomer` prefers the canonical field; falls back
to the ODOO field.
**Rationale:** Backward-compatible — existing callers using `Region`/`Address` still work.

### VIN fields are optional (string?)
**Date:** 2026-03-11
**Decision:** `VIN1`, `VIN2`, `VIN3` on `CreateCustomerDto` are `string?`.
**Rationale:** With `<Nullable>enable/>`, non-nullable `string` = required by ASP.NET Core
model binding. ODOO sends `null` for unused VINs which caused 400 errors.

---

## SO → Delivery Bin Allocation

### Bin allocation data flows from SAP → SQLite → PDF via CreateDeliveryResult
**Date:** 2026-08-18
**Decision:** `CreateDeliveryResult` carries `Dictionary<int, List<LineBinAlloc>> LineBinAllocations` (keyed by SO line number, not loop index). `SapService` populates it after `delivery.Add()` by projecting from the private `BinAllocationEntry` list. `SoDeliveryService.BuildLineLogs` serializes it to `BinAllocationsJson` (TEXT NULL) JSON per `SoDeliveryLineLog`. `SoDeliveryReportService` deserializes and formats as "BIN-A: 5.00, BIN-B: 3.00" in the PDF.
**Rationale:** JSON column avoids a separate table while keeping the bin data queryable if needed. Nullable allows pre-feature runs and non-bin warehouses to show "—" cleanly.

### Key by SO line number, not loop index, in LineBinAllocations
**Date:** 2026-08-18
**Decision:** `lineBinAllocs` inside `CreateDeliveryFromSo` is keyed by loop index `i` (into `deliverableLines`). When projecting to `LineBinAllocations`, the key is converted to `deliverableLines[i].LineNum` (RDR1.LineNum). Consumers look up by `l.LineNum`.
**Rationale:** Index-to-LineNum mapping avoids subtle bugs if filtering or ordering ever changes.

---

## Architecture

### OrderCacheService uses raw ADO.NET, not EF tracking
**Date:** Pre-existing
**Decision:** `UpsertOrdersAndLinesAsync` uses raw `SqliteCommand` with `INSERT … ON CONFLICT`
for performance on bulk syncs. EF `SaveChangesAsync` is only used for metadata updates.

### Synthetic LineNum = index in Lines list
**Date:** Pre-existing
**Decision:** `CachedOrderLine.LineNum` is the 0-based index of the line within its parent
order's Lines list — not SAP's native line number. This provides a stable key for the
`(DocEntry, LineNum)` unique index required for ON CONFLICT UPSERT.
