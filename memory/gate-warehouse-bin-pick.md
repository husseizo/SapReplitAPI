---
name: gate-warehouse-bin-pick
description: Warehouse App Select Bin & Pick workflow COMPLETE — WB01-WB18 + WC01-WC20 passing; 4-check closure commit 77c2129; merged to master
metadata:
  type: project
---

Implementation complete 2026-09-18 on claude/explore-project-w2myQ (commit 809d849).
4-Check Closure complete 2026-09-18 (commit 77c2129, merged to master).

**Root cause addressed:**
AbsEntry 134/161 stuck because DftBinEnfd=N for WHS=003/002 — SAP does NOT pre-allocate
a bin to PKL2 at pick list creation when enforcement is off. Picker must manually choose
the source bin. The automation (ZF-PICK-RECONCILE) was correctly waiting; no automation
bug existed.

**State detection:**
`PickListUiStateComputer.Compute(sapStatus, canceled, allocatedBinCount, liveCandidateCount)`
- R + bins=0 + OIBQ candidates > 0 → `AwaitingBinSelection` → "Select Bin & Pick"
- R + bins=0 + no candidates       → `NoStock`              → no action
- R + bins>0                        → `ReadyAllocated`       → "Confirm Pick"
- Y/P/D/C or Canceled=Y             → Picked/Partial/Delivered/Closed

**Bin candidates:**
`SapService.QueryWarehouseBinCandidates(itemCode, whsCode)`
- OIBQ JOIN OBIN WHERE Disabled='N' AND WhsCode=@whs AND OnHandQty>0
- Returns `List<BinCandidateDto>(BinAbsEntry, BinCode, WhsCode, AvailableQty)`

**Validation:**
`PickBinValidator.ValidateLine(desiredPickedQty, candidates, selections)`
- Candidate lookup: selected bin must be in fresh candidate list (rejects wrong-WHS, disabled)
- Per-bin qty check: sel.Qty <= candidate.AvailableQty
- Total check: sum(selections) == desiredPickedQty
- Partial pick allowed: desiredPickedQty < releasedQty is valid

**API surface:**
- GET  /api/warehouse/pick-lists/{absEntry}/state
- GET  /api/warehouse/pick-lists/{absEntry}/bin-candidates?pickEntry={pe}
- POST /api/warehouse/pick-lists/{absEntry}/confirm-pick

**Confirm pick flow (after 4-check closure):**
1. Load fresh OIBQ candidates per line (pre-mutation recheck)
2. Validate selections via PickBinValidator (DesiredFinalPickQty = cumulative, not delta)
3. Read live SAP state: ReadWarehouseLiveSapPickState(absEntry, pickEntry)
   → Reject (HTTP 409, SapRc=-2, ErrorType=StaleConflict) if: not Released, Canceled,
     line already Picked, or DesiredFinalPickQty ≤ CurrentPickQtty
4. Call UpdateZoneFulfillmentPickList (existing DI API path)
5. If fail: record error; SAP state unchanged; no fake Picked
6. If any success: RefreshAsync(absEntry) — non-fatal

**ConfirmPickLineDto.DesiredFinalPickQty semantics (CHECK 3):**
Cumulative desired final SAP PickQtty. NOT additive delta. SAP DI API
pl.Lines.PickedQuantity is desired-state assignment. Bin allocations must sum to it.

**HTTP response codes (CHECK 4):**
200 = all success; 207 = partial (Lines[].Success to distinguish); 409 = all stale-conflict;
400 = all validation; 422 = all SAP error.

**LineConfirmResultDto new fields:** ItemCode, ErrorType ("Validation"|"SapError"|"StaleConflict").
**ConfirmPickResponseDto new field:** OverallStatus ("Success"|"PartialSuccess"|"Failure").

**Bin available qty formula (CHECK 1):**
UsableQty = OIBQ.OnHandQty. OBBQ has no CommQtty in SAP B1 PL18 (batch/serial only).
OITW.IsCommited is warehouse-level, not distributed per bin. SAP DI API enforces at pl.Update().

**After confirm:**
ZF-PICK-RECONCILE detects OPKL.Status R→Y within ≤30s → triggers ODLN/OINV automation
as before. Warehouse App ends responsibility at successful SAP pick confirmation.

**Safety freeze verified:**
- Tiered allocation: NOT touched (no ZoneAllocationEngine in WarehouseBinPickService)
- ODLN/OINV: NOT created by warehouse app
- ZF-PICK-RECONCILE behavior: unchanged

**Test results:**
- WB01-WB18 (original): 18 tests, all passing
- WC01-WC20 (4-check closure): 20 new tests, all passing
- Full suite: 572 passed, 0 failed, 20 skipped (Release build)
- New failures from any change: 0

**Why:** OPKL.Status=R + PKL2=0 + DftBinEnfd=N is a legitimate "awaiting manual bin selection"
state, not an automation failure. The UI must present this explicitly as "Select Bin & Pick".

**How to apply:** On master at commit 77c2129. Live proof:
use AbsEntry 134 or 161 (proven state). After picker confirms, cache refresh fires and
ZF reconciliation picks up within 30s.
