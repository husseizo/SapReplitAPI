---
name: gate-warehouse-bin-pick
description: Warehouse App Select Bin & Pick workflow — WB01-WB18 passing, commit 809d849
metadata:
  type: project
---

Implementation complete 2026-09-18 on claude/explore-project-w2myQ (commit 809d849).

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

**Confirm pick flow:**
1. Load fresh OIBQ candidates per line (pre-mutation recheck)
2. Validate selections via PickBinValidator
3. Call UpdateZoneFulfillmentPickList (existing DI API path)
4. If fail: record error; SAP state unchanged; no fake Picked
5. If any success: RefreshAsync(absEntry) — non-fatal

**After confirm:**
ZF-PICK-RECONCILE detects OPKL.Status R→Y within ≤30s → triggers ODLN/OINV automation
as before. Warehouse App ends responsibility at successful SAP pick confirmation.

**Safety freeze verified:**
- Tiered allocation: NOT touched (no ZoneAllocationEngine in WarehouseBinPickService)
- ODLN/OINV: NOT created by warehouse app
- ZF-PICK-RECONCILE behavior: unchanged

**Test results:**
- WB01-WB18: 18 new tests, all passing
- Total: 557 | Passed: 513 | Failed: 24 (CM* pre-existing) | Skipped: 20
- New failures: 0

**Why:** OPKL.Status=R + PKL2=0 + DftBinEnfd=N is a legitimate "awaiting manual bin selection"
state, not an automation failure. The UI must present this explicitly as "Select Bin & Pick".

**How to apply:** Deploy from claude/explore-project-w2myQ commit 809d849. Live proof:
use AbsEntry 134 or 161 (proven state). After picker confirms, cache refresh fires and
ZF reconciliation picks up within 30s.
