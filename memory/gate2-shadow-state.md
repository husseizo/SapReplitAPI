---
name: gate2-shadow-state
description: Gate 2 shadow deployment state — technical deployment DONE, shadow observation ONGOING since 2026-09-10
metadata:
  type: project
---

Gate 2 technical deployment completed 2026-09-10 (commit 1a7c914 on feature/tiered-zone-allocation).

**Why:** Tiered zone allocation shadow deployment gate — run Tiered engine alongside Legacy (read-only) to collect allocation comparison evidence before Gate 3 activation.

**How to apply:** Do not begin Gate 3 without explicit authorization. AllocationMode=Legacy must remain. Check shadow-evidence.jsonl before compiling the interim report.

## Technical Status (complete)
- DDL executed: `sql/J_TIERED_ZONE_ALLOCATION.sql` against MolasIntegration at 2026-09-10 11:05 EAT
  - Created: `dbo.OriginWarehousePriority` (20 seed rows, exact approved matrix)
  - Added 7 columns (all NULL, additive): FulfillmentRequest.OriginWhsCode, FulfillmentOrchestration.(OriginWhsCode, EffectiveOrigin, AllocationTier, AllocationReason), AllocationFragment.SourceTier, PickListRecord.PickedAtUtc
  - Legacy ZoneWarehousePriority: 8 rows unchanged
- Backup taken before DDL: `C:\Backup migration\MolasIntegration_Gate2_20260910_110422.bak`
- Build: 209/209 tests passing (Release)
- Service deployed: `C:\SAPAPI\SapReplitAPI.exe` (11:07:12, 26.6 MB publish)
- Service: SapReplitAPI, Production environment, AllocationMode=Legacy (class default, no override)

## Shadow Observation
- Observation start: 2026-09-10 11:08 EAT
- Checkpoint: 2026-09-15 (3 business days: Sep 11, 12, 15)
- Eligible orders so far: 0 (volume ~4-5/week)
- Evidence collector: Scheduled task `Gate2ShadowCollector` (every 10 min)
- Evidence file: `C:\SAPLogs\shadow-evidence.jsonl`
- Shadow log: `[ZF-SHADOW-DIFF]` entries in `C:\SAPLogs\app*.log`

## Hard Rules (all still in force)
- AllocationMode = Legacy (NEVER change to Tiered without Gate 3)
- Offline V2 = Disabled
- TIERED-DRIVEN SAP MUTATIONS = 0
- Do NOT create artificial orders for coverage
- Do NOT backfill historical PickedAtUtc
- Do NOT modify frozen reconciliation/delivery/invoice files
- Do NOT grant BACKUP DATABASE to SapReplitOutboxApp (least-privilege principle)
- Do NOT merge feature/tiered-zone-allocation to master yet

## Next Action
At end-of-day 2026-09-15 (3-business-day checkpoint):
1. Read `C:\SAPLogs\shadow-evidence.jsonl` (all checkpoints since baseline)
2. Read recent `C:\SAPLogs\app*.log` for [ZF-SHADOW-DIFF] entries
3. Query MolasIntegration for new orchestrations since 2026-09-10 08:08 UTC
4. Compile and return GATE 2 — INTERIM SHADOW EVIDENCE REPORT
5. STOP — do not begin Gate 3

[[decisions]]
