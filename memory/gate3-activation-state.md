---
name: gate3-activation-state
description: Gate 3 Tiered production activation — COMPLETE as of 2026-09-10T09:16:21Z; awaiting first live Tiered order verification
metadata:
  type: project
---

Gate 3 controlled Tiered production activation completed 2026-09-10T09:16:21Z (12:16:21 EAT).

**Why:** Business elected to end shadow observation early for project handoff/go-live. Explicit authorization received 2026-09-10.

**How to apply:** AllocationMode = Tiered is now authoritative. First live order with `[ZF-TIERED]` log entry and AllocationTier populated in dbo.FulfillmentOrchestration will complete Section 13 hold point.

## Activation Record
- Config changed: `C:\SAPAPI\appsettings.Production.json` → `"AllocationMode": "Tiered"`
- Source tree also updated: `SapReplitAPI/appsettings.Production.json` (gitignored, production-only)
- Service restarted: 2026-09-10T09:16:21Z
- Post-restart log: `C:\SAPLogs\app20260910_003.log`
- API health: HTTP 200 on ZF pickers endpoint
- Quartz reconciliation: firing every 30s confirmed
- SAP connection: Live (reconciliation reading OPKL status from SAP)
- MolasIntegration: Healthy (all tables accessible)
- SQLite: `F:\AutohubCaches\productcache.db` — 44 MB, operational
- Historical orchestrations (12 rows): all AllocationTier=NULL (Legacy baseline)

## Rollback Procedure
- Edit `C:\SAPAPI\appsettings.Production.json`
- Change `"AllocationMode": "Tiered"` → `"AllocationMode": "Legacy"` (or remove the key)
- Restart SapReplitAPI Windows service
- No code change required. No DB change required. No SAP SQL required.
- Also update source tree `SapReplitAPI/appsettings.Production.json` to match.

## Rollback Triggers (per spec Section 21)
Wrong warehouse selected, unnecessary split, line split when 1 WHS available, min-WHS rule violated, incorrect EffectiveOrigin, incorrect priority, shortage creates SAP doc, duplicate ORDR/OPKL/ODLN/OINV, reconciliation regression, unexpected Tiered exception, material latency regression, data integrity inconsistency.

## Gate 3C — COMPLETE (2026-09-10T16:15Z)
- OriginWhsCode propagation fix deployed (controller effectiveReq S3)
- ZfAllocationPolicy shared service created (S5-9)
- /plan endpoint Tiered-mode aligned with origin/tier metadata in response (S10)
- 246/246 tests PASS (37 new: O01-O10 origin propagation + PL01-PL15 plan alignment)
- Release build SHA256: 86CF4D9E95926B4E68EFB1DEFFCAD219ECB7AFF187D944459D82FBB01195F9BA
- /plan production verification PASS: origin004→EffectiveOrigin=004, Tier1, WHS004, no mutations
- Next: S21 — wait for next legitimate Cluster-side origin004 order (do NOT fabricate)

## First Live Order Hold Point (GATE 3B — INCORRECT ALLOCATION)
Section 13 hold point — first legitimate ZF order after activation. Must capture and record:
- RequestId, DeliveryLocation, ReceivedOriginWhsCode, EffectiveOrigin
- Stock snapshot by ItemCode/WhsCode
- AllocationTier, AllocationReason, Fragments
- Verify allocated qty = requested qty for every line
- ORDR verified, OPKL count, picker, reconciliation, ODLN, OINV, duplicates

## Shadow Waiver Record
- Shadow sample target (30 orders): NOT COMPLETED
- Natural sample size at waiver: 0 orders eligible during observation window
- Business authorization: "Proceed to controlled Tiered production activation for project handoff"
- Observation started: 2026-09-10 11:08 EAT
- Observation ended by business decision: 2026-09-10 ~12:16 EAT (~68 minutes)
- Gate 2 evidence file: `C:\SAPLogs\shadow-evidence.jsonl` (2 checkpoints, 0 shadow comparisons)

[[gate2-shadow-state]]
