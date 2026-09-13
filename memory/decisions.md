# Technical Decisions

## Customer Creation Fix — ONNM Counter + IsSapOffline Scope (2026-09-12)

### ONNM.AutoKey is the authoritative DocEntry counter for Customers — NOT SQL IDENTITY
**Decision:** IDENT_CURRENT('OCRD') is irrelevant (OCRD has no IDENTITY column; returns 0 always). The authoritative next-DocEntry for Customer BPs is ONNM.AutoKey (ObjectCode=2, DocSubType=C). Never use SQL Server IDENTITY to diagnose -2035 collisions on OCRD.

### SAP Restore Numbering File is the only safe way to advance ONNM.AutoKey
**Decision:** Direct UPDATE ONNM is forbidden. The supported repair path is SAP Help → Support Desk → Restore → Restore Numbering File. This is idempotent and SAP-supported.

### IsSapOffline must only match RPC infrastructure HRESULTs
**Decision:** COMExceptions are only "SAP offline" for: RPC_E_DISCONNECTED (0x80010108), RPC_S_SERVER_UNAVAILABLE (0x800706BA), RPC_E_CALL_REJECTED (0x80010001). All other COMExceptions — including field-validation errors (HRESULT=0xFFFFFC14 = SAP -1004) — are business rejections and must reach RecordRejectionAsync. This pattern applies to ANY future job that calls a SAP COM method and needs to distinguish connection failure from validation failure.

### U_Customer_Type valid values
**Decision:** Valid SAP enum values for U_Customer_Type UDF: Owner, Garage, Re-Seller, Whole-Sale, Corporate. "Individual" is NOT valid and throws HRESULT=0xFFFFFC14. Front-end must enforce this enum.

---

## Offline Fulfillment V2 (2026-09-06)

### V1/V2 Isolation — no shared tables, no shared code paths
**Date:** 2026-09-06
**Decision:** V2 uses entirely new tables (OfflineFulfillmentOrders, OrderLines, Picks, Reservations).
PendingOrders/PendingOrderLines are never touched by V2 code. WorkflowVersion="OfflineFulfillmentV2"
discriminator ensures no cross-routing.

### OfflineSapAdapter for SAP mutations (not SapService directly)
**Date:** 2026-09-06
**Decision:** Recovery SAP mutations are in a separate `OfflineSapAdapter` class (stub/NotImplemented).
Rationale: Keeps SapService.cs clean; matches the ZF pattern (ZoneFulfillmentSapOrderService, etc.).
Phase E stubs throw NotImplementedException — unreachable while Enabled=false.

### Recovery stage checkpointing — RecoveryStage persisted before each SAP call
**Date:** 2026-09-06
**Decision:** OfflineFulfillmentRecoveryService persists RecoveryStage to Neon after each SAP mutation
succeeds. On restart, completed stages are skipped. Never duplicates SAP documents.

### Stale claim release before each batch
**Date:** 2026-09-06
**Decision:** RecoveryJob calls ReleaseStaleClaimsAsync before RecoverBatchAsync each cycle.
Claims older than RecoveryClaimLeaseSeconds (default 120s) are returned to WaitingForRecovery.

### Physical pick truth immutable after OfflineConfirmPick
**Date:** 2026-09-06
**Decision:** IsConfirmed=true on OfflineFulfillmentPick is permanent. SAP recovery MUST use
recorded WhsCode/BinAbsEntry/PickedQty — no silent reallocation. Mismatch → ReconciliationRequired.

---

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

## Phase 0 — SAP Event Probe (PostTransactionNotice)

### Phase 0C complete — confirmed event surface for inventory architecture
**Date:** 2026-08-26
**Decision:** EventOutbox poller must filter on exactly these ObjectType/TransactionType pairs:

**INCLUDE (inventory-moving events):**
- `15/A` — ODLN Add (delivery reduces OnHand) → refresh DLN1 → OITW/OIBQ
- `16/A` — ORDN Add (A/R Return restores OnHand) → refresh RDN1 → OITW/OIBQ
- `59/A` — OIGN Add (Goods Receipt increases OnHand) → refresh IGN1 → OITW/OIBQ
- `60/A` — OIGE Add (Goods Issue reduces OnHand) → refresh IGE1 → OITW/OIBQ
- `67/A` — OWTR Add (Stock Transfer moves between warehouses) → refresh OWTR+WTR1 → OITW/OIBQ×2

**EXCLUDE (confirmed noise):** `10000013/*`, `10000011/*`, `410000000/*`, `17/*`, `24/*`, `321/*`, `20/*`

**Rationale:** Phase 0A+0B+0C probe confirmed these are the only events that move OITW.OnHand in this SAP B1 PL18 installation. All other events are trade-doc companions, heartbeats, or A/R-only operations.

---

### Phase 0D complete — cancel semantics confirmed, routing contract corrected
**Date:** 2026-08-26
**Decision:** The Phase 0C exclude list was WRONG about 17/*, 24/*, 13/*, 14/*. They are domain-specific events, not noise. The final 4-domain routing contract is:

#### PHYSICAL INVENTORY DOMAIN (refreshes OITW + OIBQ)
- `15/A` — ODLN Add → DLN1
- `16/A` — ORDN Add → RDN1
- `59/A` — OIGN Add → IGN1
- `60/A` — OIGE Add → IGE1
- `67/A` — OWTR Add → OWTR+WTR1, refresh both FromWarehouse and ToWarehouse
- `67/C` — OWTR Cancel → read original OWTR, refresh both warehouses (SAP creates a reversal OWTR doc; its 67/A does NOT fire — only 67/C fires for the original DocEntry)
- `14/A` — ORIN Add → conditional: query `OINM WHERE TransType=14 AND BASE_REF=<CM DocNum>`; if rows exist → refresh OITW/OIBQ; if no rows → skip physical, proceed to Invoice domain only

#### COMMITMENT DOMAIN (refreshes WarehouseInventory only — NOT BinInventory)
- `17/A` — ORDR Add → refresh IsCommited, OnOrder, AvailableToSell
- `17/U` — ORDR Update → same
- `17/C` — ORDR Cancel → commitment released, same refresh

#### PAYMENT DOMAIN (refreshes InvoicePayments + InvoiceLifecycleStatus)
- `24/A` — ORCT Add → record payment; update linked OINV status
- `24/C` — ORCT Cancel → remove payment; reopen linked OINV (NOT noise)

#### INVOICE DOMAIN (refreshes Invoice + InvoiceLines)
- `13/A` — OINV Add → create/update Invoice; mark base ODLN closed via INV1.BaseEntry
- `14/A` — ORIN Add → update reversal state on base invoice via RIN1.BaseEntry (also in Physical domain)

#### EXCLUDE (confirmed noise only)
- `10000011/*` — SAP internal billing companion (captured by 13/A or 24/A)
- `10000013/*` — SAP internal trade companion (captured by 17/*, 15/A, 16/A)
- `410000000/*` — APLUS heartbeat
- `321/*` — Reconciliation (OITR), financial ledger only
- `59/C` — DI API returns -5006; never fires in this config; no handler needed
- `60/C` — DI API returns -5006; never fires in this config; no handler needed

### Delivery closure is silent in PostTransactionNotice
**Date:** 2026-08-26
**Decision:** ODLN never fires `15/U` or `15/C` when it becomes DocStatus=C — whether closed by A/R Return or by A/R Invoice. The EventOutbox must NOT wait for a `15/C` to detect cancelled deliveries. Delivery closure is detected via `16/A` (Return created) or `13/A` (Invoice created against it).

### Delivery cancellation via A/R Return, not dln.Cancel()
**Date:** 2026-08-26
**Decision:** `Documents.Cancel()` returns -5006 for `oDeliveryNotes` (and `oInvoices`) in MOLAS SAP config. Correct DI API path: create `oReturns` (ORDN) with `BaseType=15, BaseEntry=dlnDocEntry`. This fires `16/A` and creates OINM TransType=16.

### OWTR requires dynamic dispatch, no BinAllocations
**Date:** 2026-08-26
**Decision:** `oStockTransfer` returns `IInventoryTransfer`, not `IDocuments`. Casting to `Documents` throws `InvalidCastException`. `Lines.BinAllocations` causes -5002 on `IInventoryTransfer`. Correct pattern: `dynamic tr = company.GetBusinessObject(BoObjectTypes.oStockTransfer)`, set header `FromWarehouse`/`ToWarehouse` only — SAP auto-assigns default bins. No `Lines.WarehouseCode`, no `Lines.BinAllocations`.

### OIGN/OIGE/OWTR fire no companion events; trade docs always pair with 10000013
**Date:** 2026-08-26
**Decision:** Inventory-only documents (OIGN=59, OIGE=60, OWTR=67) produce a single clean `*/A` event. Trade documents (ODLN=15, ORDN=16, OINV=13, ORDR=17) always pair with an internal companion (`10000013/A` for ODLN/ORDN/ORDR; `10000011/A` for OINV/ORCT). This is a reliable discriminator.

### OINM valid columns in this SAP version
**Date:** 2026-08-26
**Decision:** Valid OINM query columns: `ItemCode, TransType, InQty, OutQty, BASE_REF, DocDate, TransNum`. `WhsCode` is invalid in this SAP B1 PL18 version (causes SQL error). `BASE_REF` = DocNum (not DocEntry) for all document types. OINM `TransType` = PostTransactionNotice `ObjectType` (same numeric codes).

---

## Customer Returns / Credit Memo Backend Alignment (2026-09-13)

### ORRR ObjType = 234000031 (not 234000030)
**Date:** 2026-09-13
**Decision:** SAP Return Request (ORRR) uses `(BoObjectTypes)234000031`. Confirmed from live DB: `SELECT ObjType FROM ORRR` → 234000031. This constant is declared in both `CreditMemoEventHandler` and `SapService` as `private const int OrrrObjectType = 234000031`.

### RIN1.BaseType = 234000031 when ORIN is created from ORRR (Path B)
**Date:** 2026-09-13
**Decision:** When an A/R Credit Memo (ORIN) is created from a Return Request (ORRR), the RIN1 line stores `BaseType=234000031` and `BaseEntry=<ORRR.DocEntry>`. The old `CreditMemoEventHandler` only checked `BaseType==13` and silently skipped all ORRR-path credit memos. Fixed by adding Path B resolution: read RRR1 where `DocEntry=ORRR.DocEntry AND BaseType=13` to get the underlying OINV DocEntry.

### CreditMemoEventHandler inventory/delivery refresh is unconditional
**Date:** 2026-09-13
**Decision:** Inventory and delivery refresh in `CreditMemoEventHandler.HandleAsync` must run regardless of whether any invoices were resolved. Old code returned early if `affectedInvoiceDocEntries.Count == 0`, skipping inventory updates for ORRR-path credit memos. Fixed: refresh steps moved before the no-invoice guard.

### LoadCreditMemoEvidence Path B SQL — join via RRR1
**Date:** 2026-09-13
**Decision:** `InvoiceLifecycleStatusService.LoadCreditMemoEvidence` must count ORRR-path credit memos toward invoice PaidToDate. The Path B SQL joins `RIN1 → ORIN → RRR1` where `RIN1.BaseType=234000031` and `RRR1.BaseType=13`. Verified live: OINV 28571 — Path A returns 0 rows; Path B returns CreditMemoCount=2, CreditMemoTotal=70,000. Without Path B, invoices paid via ORRR→ORIN never close in SQLite/Neon.

### U_AppRef idempotency — ORRR uses "srr-" prefix, ORIN uses raw GUID
**Date:** 2026-09-13
**Decision:** Observed from live SAP: ORRR.U_AppRef starts with "srr-" (e.g. "srr-c951ea5489784dcbaed4f8358b30d95e"). ORIN.U_AppRef is a plain GUID (e.g. "4d3a06cf-5033-496c-ad94-a2a69c95a91f"). `CreateReturnRequest` and `CreateCreditMemoFromReturnRequest` both check U_AppRef before calling Add() — idempotent on same app_ref.

### DI API Document_Lines has no BinCode property for credit notes
**Date:** 2026-09-13
**Decision:** `Document_Lines.BinCode` does not exist on credit note lines in this SAP DI API version. Bin validation is done via `ValidateBinBelongsToWhs` (Gate 7) before `orin.Add()`, but the actual bin is NOT set on `orin.Lines` — SAP uses the `WarehouseCode` default bin. Removing the DI API bin-set call was the correct fix (not a workaround).

### dotnet build fails for COM-referencing projects — use VS MSBuild
**Date:** 2026-09-13
**Decision:** `dotnet build` uses .NET Core MSBuild which does not support `ResolveComReference`. Use `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe /t:Build /p:Configuration=Release /p:Platform="Any CPU"` for the main project. `dotnet test` works for the test project (no COM references).

---

## D1 HOME-ZONE-FIRST Tiered Allocation (2026-09-11)

### Strict HOME/FALLBACK zone separation in Tiered mode
**Date:** 2026-09-11  
**Branch:** claude/d1-home-zone-first (commit bf60146)  
**Decision:** Tier 1 and Tier 2 search HOME warehouses only. Fallback enters at Tier 3+ (whole-line) or Tier 4 (split cascade HOME→FALLBACK).

**Zone member rules:**
- Cluster-side origin 001/002/004: HOME={001,002,004}, FALLBACK={003}
- Cluster-side origin 003 (A1 special): ALL four WHS in HOME, FALLBACK=empty (employee physically at 003)
- Mikocheni-side: HOME={003} only, FALLBACK=cluster WHS
- Legacy (no OriginWarehousePriority rows): all zone WHS as HOME, FALLBACK=empty

**Engine allocation phases (Tier 3+ path):**
- Phase A: classify lines as home-coverable (any HOME WHS >= qty) or not
- Phase B: minimum HOME subset for home-coverable lines (SourceTier=2)
- Phase C: non-home-coverable lines → first FALLBACK WHS with full qty (SourceTier=3)
- Phase D: remaining → HOME cascade then FALLBACK cascade (SourceTier=4)
- Phase B reject fix: competing home-coverable lines that Phase B can't place together fall to Phase D (Tier 4 split) rather than being silently dropped

**Production /plan verified:** origin=004, allocationTier=2, NO fragment→003

**Tests added:** D101–D118 (engine) + R-D1-01 to R-D1-10 (resolver). Total: 369 passed, 0 failed.

---

## Zone Fulfillment — Production Automation Authorization

### Full end-to-end automation enabled (Phase C, 2026-09-02)
**Date:** 2026-09-02
**Decision:** Both mutation gates enabled. `PICK_LIST_MUTATION_ENABLED = true` in `ZoneFulfillmentPickListService`. `MUTATION_ENABLED = true` in `ZoneFulfillmentDeliveryService`.

**Invoice gate (superseded 2026-09-02, commit 6396336; re-verified 2026-09-09):** the hard-coded `MUTATION_ENABLED=false` in `ZoneFulfillmentInvoiceService` no longer exists. OINV.Add() is gated by config key `ZoneFulfillment:InvoiceAutomationEnabled` (default `false`; appsettings are gitignored so the live value is only visible on the host) AND by the startup probe `ZoneFulfillmentInvoiceStartupHealth.InvoiceRecordAvailable` (dbo.InvoiceRecord must be queryable at boot). When both pass, `ZoneFulfillmentAutomationService` calls `ExecuteInvoiceAsync` automatically right after `DELIVERY_CREATED`.

**Authorized flow:**
```
Sales User → POST /orders (ORDR.Add())
→ SYSTEM: auto OPKL.Add() from ZoneFulfillmentOrchestrationService.AutoCreatePickListsAsync()
→ Picker: POST /pick-lists/{absEntry}/pick (Confirm Pick = LAST human action)
→ SYSTEM: ZoneFulfillmentAutomationService evaluates all-picks-complete
→ IF pending warehouses: return WaitingForOtherPicks (no delivery)
→ IF all complete: ZoneFulfillmentDeliveryService.ExecuteDeliveryAsync() → ONE ODLN
→ IF DELIVERY_CREATED: orchestration → Delivered; ZoneFulfillmentInvoiceService.ExecuteInvoiceAsync() → ONE OINV (config-gated)
→ Fallback: ZoneFulfillmentPickReconciliationJob (every 30 s) detects OPKLs confirmed natively in SAP B1 (PLR not Picked >90 s), reconciles PLRs, then runs the same automation
```

**Concurrency:** `ZoneFulfillmentDeliveryCoordinator` (per-RequestId `SemaphoreSlim`) prevents duplicate ODLN from concurrent final-pick races.

**No Dispatch User. No manual Create Delivery. No invoice creation.**

**New files/changes:**
- `ZoneFulfillmentAutomationService.cs` — new; post-pick all-picks-complete evaluator
- `ZoneFulfillmentPickListService.cs` — auto-trigger after ExecutePickAsync
- `ZoneFulfillmentOrchestrationService.cs` — auto-create pick lists after ORDR.Add()
- `ZoneFulfillmentDomain.cs` — added `OrchestrationState.Delivered` + `PostPickAutomationResult`
- `Program.cs` — `AddScoped<ZoneFulfillmentAutomationService>()`

**Protected historical docs (permanent hard stops):**
- ORDR 28419/28421/28431/28433, Pick Lists 6/8/9
- ODLN 30504 (uninvoiced, protected), ODLN 30511 (cancelled, protected), ODLN 30516 (recovery, uninvoiced, protected)
- SO 28451 (OrchestrationId=2, the recovery orchestration)

**slpCode=5 required** on all future zone fulfillment orders.

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
