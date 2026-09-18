# Memory Index

- [Gate 2 Shadow State](gate2-shadow-state.md) — Gate 2 technical deployment done; shadow ended early by business decision 2026-09-10
- [Gate 3 Activation State](gate3-activation-state.md) — Gate 3 Tiered activation COMPLETE 2026-09-10T09:16:21Z; AllocationMode=Tiered live; first order hold point PENDING
- [Gate Today Orders Event Refresh](gate-today-orders-event-refresh.md) — COMPLETE 2026-09-10T18:29 EAT; event-driven fast path deployed; SAP→Neon now seconds via TodayOrderEventRefreshService
- [Gate PickList Event Refresh](gate-picklist-event-refresh.md) — COMPLETE 2026-09-10T20:20 EAT; IPickListEventRefreshService + 3 trigger seams deployed; 309/309 tests; commit 9804abb
- [Gate Credit Memo Cache Fast Path](gate-credit-memo-cache-fast-path.md) — COMPLETE 2026-09-13; ORIN 81 in SQLite+Neon; Path B (BaseType=234000031) verified; master b246a64
- [Gate Invoice Event-First Sync](gate-invoice-event-first-sync.md) — GATES 2/6/8/5+7+10+12/16 complete 2026-09-16; InvoiceMirrorRefreshService, GATE 8 targeted delete, InvoiceDriftDetectionJob, IF01-IF18 passing; GATE 18 (deploy) pending
- [Gate Warehouse Bin Pick](gate-warehouse-bin-pick.md) — Select Bin & Pick workflow COMPLETE 2026-09-18; WB01-WB18 passing; commit 809d849; AbsEntry 134/161 root cause = NO_BIN_ALLOCATION (DftBinEnfd=N)
- [Gate Invoice Base Refs](gate-invoice-base-refs.md) — INV1 BaseType/BaseEntry/BaseLine mirrored end-to-end COMPLETE 2026-09-18; BR01-BR15 passing; backfill endpoint POST /api/invoices/backfill-base-refs; commit bad1420
