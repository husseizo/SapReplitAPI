---
name: gate-invoice-base-refs
description: Invoice INV1 base-document references (BaseType/BaseEntry/BaseLine) added end-to-end; BR01-BR15 all passing; commit bad1420
metadata:
  type: project
---

INVOICE BASE-REFERENCE MIRROR COMPLETE 2026-09-18.

Commit: **bad1420**
Branch: claude/explore-project-w2myQ
Tests: BR01-BR15 all Passed; full suite 552 passed, 0 failed, 20 skipped.

## What was added

New columns `BaseType` (int NOT NULL DEFAULT 0), `BaseEntry` (int nullable), `BaseLine` (int nullable) on `InvoiceLines` in both SQLite and Neon.

**Why:** Frontend hub contract requires knowing whether each invoice line was created from a delivery (BaseType=15), a sales order (BaseType=17), or entered manually (BaseType=-1). SAP source: `INV1.BaseType / BaseEntry / BaseLine`.

## Write paths updated

| Path | Location |
|------|----------|
| SAP bulk read | `SapService.GetInvoices` — INV1 SELECT extended |
| SAP single read | `SapService.GetInvoiceByDocEntryAsync` — same |
| New backfill read | `SapService.GetInvoiceLineBaseRefs(docEntry)` |
| Event mapping | `InvoiceMirrorRefreshService.MapToLines` |
| SQLite targeted write | `InvoiceCacheService.UpsertSingleInvoiceAsync` (15-col INSERT) |
| SQLite bulk write | `InvoiceCacheService.UpsertInvoicesAndLinesAsync` flat-line mapping |
| Neon event write | `NeonEventWriteService.UpsertInvoiceAsync` (17-col INSERT) |
| Neon bulk write | `NeonSyncJob.InsertInvoiceLinesBatchedAsync` (17-col INSERT) |

## Schema migrations

- SQLite: `Migrations/20260918000001_AddBaseRefsToInvoiceLines.cs` — additive `ADD COLUMN`
- Neon: `ALTER TABLE "InvoiceLines" ADD COLUMN IF NOT EXISTS` in `Program.cs` startup block + `EnsureReturnsSchemaForMaintenanceAsync`

## Backfill

`InvoiceBaseRefBackfillService.RunAsync()` — reads all distinct DocEntry from SQLite, calls `GetInvoiceLineBaseRefs` for each, issues targeted `UPDATE … SET BaseType/BaseEntry/BaseLine WHERE DocEntry AND LineNum`. Idempotent. Never touches Quantity, Price, ReturnedQty, PendingReturnQty.

Admin endpoint: `POST /api/invoices/backfill-base-refs` → returns `{doc_entries_scanned, lines_updated, doc_entries_with_errors, errors}`.

## Safety constraints observed

- OINV creation, ODLN creation, payments, returns, Tiered ZF, pick reconciliation, warehouse logic, frontend API contract: all unchanged
- No SAP business-document writes
- ReturnedQty / PendingReturnQty authority: untouched (SQ01-SQ10 + BR11-BR12 still pass)

## How to apply

When extending InvoiceLines in future, follow the same 7-path pattern:
DTO → CacheModel → CacheDbContext config → NeonDbContext config → SapService query → event mapper → SQLite write → Neon write → NeonSyncJob bulk write → startup migration (SQLite + Neon).
