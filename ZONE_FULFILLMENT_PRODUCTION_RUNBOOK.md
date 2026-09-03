# Zone Fulfillment — Production Operations Runbook

## Production Contract (Immutable)

```
Sales User   → POST /orders  → [AUTO] ORDR created → [AUTO] OPKLs created per WHS
Warehouse    → CONFIRM PICK  → [AUTO] WaitingForOtherPicks (if other WHS pending)
                               [AUTO] Delivery Preflight (when all WHS picked)
                               [AUTO] ODLN created (consolidated, one per order)
                               [AUTO] 15/A event → SQLite → Neon inventory update
```

**No Dispatch User. No manual Pick List creation in normal flow. No manual Delivery creation in normal flow.**

---

## Endpoint Reference

### Normal Flow (Sales User)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/zone-fulfillment/experimental/orders` | POST | Create ZF order. Returns ORDR DocEntry. Requires `X-Zone-Experimental: true`. |
| `/api/zone-fulfillment/experimental/orders/{id}/status` | GET | Full workflow state: state, pickLists, pendingWarehouses, deliveryDocEntry. |

### Normal Flow (Warehouse User)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/zone-fulfillment/experimental/orders/{id}/pick-lists/{absEntry}/state` | GET | Pre-pick state: PLR, PKL1, bins, OIBQ. |
| `/api/zone-fulfillment/experimental/orders/{id}/pick-lists/{absEntry}/pick` | POST | Confirm Pick. Returns automationStatus, pendingWarehouses, deliveryDocEntry. |

### ADMIN / RECOVERY ONLY — do not expose in warehouse UI

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/zone-fulfillment/experimental/orders/{id}/pick-lists` | POST | Manually create pick lists (recovery for AutoPickListFailed). |
| `/api/zone-fulfillment/experimental/orders/{id}/pick-lists/repick` | POST | Create repick OPKL for a closed pick (REPICK_REQUIRED scenario). |
| `/api/zone-fulfillment/experimental/orders/{id}/delivery` | POST | Manually trigger delivery (admin recovery only). |
| `/api/zone-fulfillment/experimental/orders/{id}/reconcile-cancelled` | POST | Reconcile a cancelled SAP order against ghost orchestration. |
| `/api/zone-fulfillment/experimental/orders/{id}/delivery/preflight` | GET | Read-only gate snapshot. |
| `/api/zone-fulfillment/experimental/orders/{id}/delivery/recovery-preview` | GET | Per-fragment recovery action plan. |
| `/api/zone-fulfillment/experimental/plan` | POST | Dry-run allocation (non-reserving). |

---

## Automation State Model

| State (automationStatus in pick response) | Meaning |
|------------------------------------------|---------|
| `WaitingForOtherPicks` | This pick confirmed; other warehouses still pending. Normal. |
| `DeliveryCreated` | All picks done; ODLN created automatically. |
| `DeliveryBlocked` | All picks done but delivery gate failed. Supervisor action required. |
| `AutomationError` | Unexpected exception in automation layer. Check logs. |
| `OrchestrationNotFound` | RequestId not in MolasIntegration. |

| FulfillmentOrchestration State | Meaning |
|-------------------------------|---------|
| `Accepted` | ORDR created. Pick lists may or may not be created yet. |
| `Delivered` | ODLN created successfully. |
| `Canceled` | SAP ORDR was cancelled; orchestration reconciled. |
| `Failed` | Definitive failure (stock, SAP, validation). |
| `UnknownOutcome` | SAP ORDR state uncertain; retry or check status. |

| FailureKind | Meaning |
|-------------|---------|
| `PickListAutoCreationFailed` | ORDR committed but auto OPKL creation failed. State=Accepted. Recover via admin POST /pick-lists. |
| `InsufficientStock` | Stock check failed before ORDR.Add(). |
| `SapDefinitiveFailure` | SAP returned non-zero rc on ORDR.Add(). |
| `SapUnknownOutcome` | SAP .Add() outcome uncertain. |

---

## Scenario Runbooks

### Normal Flow

1. Sales user calls `POST /orders`. Returns `HTTP 201`, `soDocEntry=XXXXX`, `state=Accepted`.
2. Verify `GET /status`: `pickListsCreated=true`, `pickListCount=N` (one per warehouse).
3. Warehouse 1 calls `POST .../pick`. Returns `automationStatus=WaitingForOtherPicks`.
4. Remaining warehouses call `POST .../pick`. Last picker gets `automationStatus=DeliveryCreated`, `deliveryDocEntry=XXXXX`.
5. Done. 15/A event fires automatically; inventory updated in SQLite/Neon.

---

### AutoPickListFailed

**Symptom**: ORDR created but no OPKLs. `GET /status` returns `failureKind=PickListAutoCreationFailed`, `pickListsCreated=false`.

**Log event**: `[ZF-AUTO] AutoPickListFailed RequestId={Rid} OrchId={Oid} exceptionType={T} error={Msg}`

**Action (Supervisor / IT)**:
1. Check logs for `[ZF-AUTO] AutoPickListFailed` to understand root cause.
2. Call admin `POST .../pick-lists` to create missing OPKLs.
3. Service is idempotent — existing OPKLs are preserved; only missing ones are created.
4. Verify `GET /status`: `pickListsCreated=true`.

**Do NOT**: Cancel ORDR, create a new order, or attempt manual SAP pick list creation outside this endpoint.

---

### WaitingForOtherPicks (Normal State)

**Log event**: `[ZF-AUTO] WaitingForOtherPicks RequestId={Rid} completed=[...] pending=[...]`

This is NOT an error. The system is waiting for other warehouses to pick.

**Threshold guidance**: If a warehouse has not confirmed pick within the normal warehouse shift time (recommended: flag to supervisor after 4 hours), check that the picker has their OPKL available.

**Action**: None, unless a picker has not received their OPKL or is unable to pick.

---

### DeliveryBlocked

**Symptom**: All picks confirmed but no ODLN. `POST .../pick` returns `automationStatus=DeliveryBlocked`, `gateErrors=[...]`.

**Log event**: `[ZF-AUTO] DeliveryBlocked RequestId={Rid} soDocEntry={De} verdict={V} gateErrors=[...]`

**Action (Supervisor / IT)**:
1. Read `gateErrors` from the pick response or from `GET .../delivery/preflight`.
2. Identify root cause: stale SAP state, RDR1 OpenQty mismatch, PKL1 status mismatch, ORDR status issue.
3. Fix root cause first.
4. Call admin `POST .../delivery` to retry delivery.
5. Verify ODLN created and `GET /status` returns `deliveryDocEntry`.

**Do NOT**: Reverse the pick (`pl.Update()` with zero qty). Do NOT ask picker to pick again unless `gateErrors` explicitly says `REPICK_REQUIRED`. Do NOT manually create a delivery in SAP native UI.

---

### Canceled SAP Order (ReconcileCancelledOrderAsync)

**Symptom**: Sales order cancelled in SAP; orchestration still shows `Accepted`.

**Action (Supervisor / IT)**:
1. Call `POST .../reconcile-cancelled`.
2. Returns: `newOrchState=Canceled`, PLRs closed, `physicalPickExceptions` list.
3. If `physicalPickExceptions` is non-empty: items were physically picked but never delivered. Supervisor to verify stock is physically returned to bin.

**Log event**: `[ZF-AUTO] OrderCanceled` (set in reconciliation service).

---

### Repick Required (REPICK_REQUIRED)

**Symptom**: After a delivery cancellation, `GET .../delivery/preflight` shows `requiresRepick=true` for a fragment.

**Action (Supervisor / IT)**:
1. Call `GET .../delivery/recovery-preview` to identify which fragments need repick.
2. For each `REPICK_REQUIRED` fragment, call admin `POST .../pick-lists/repick` with the `soLineNum`.
3. Assigned picker picks again using the new OPKL.
4. After all repick OPKLs are confirmed, auto-delivery resumes normally.

---

### SAP Native Picking Detected (Wrong Path)

**Symptom**: Picker used SAP native OPKL screen instead of Confirm Pick API. PKL1 shows PickStatus=Y but no PickListRecord in MolasIntegration.

**Action**: 
- Read PLR status via `GET .../pick-lists/{absEntry}/state`.
- If MolasIntegration is behind SAP, recovery path is to update PLR manually via admin endpoint or reconcile.
- Do not call `pl.Update()` again — that would reset the pick.

---

### 15/A Event Delayed

**Symptom**: ODLN created (deliveryDocEntry present) but SQLite/Neon inventory not updated.

**Check**: Query `SapEventOutbox` for the ODLN DocEntry: `SELECT * FROM SapEventOutbox WHERE DocEntry=XXXXX AND ObjectType=15`.

| Status | Action |
|--------|--------|
| `Pending` or `Processing` | Outbox poller is processing. Wait. |
| `Failed` | Check OutboxPoller logs for error. Fix root cause; reset Status to Pending to retry. |
| Row missing | OutboxPoller may not have captured the event. Check SAPbobsCOM event listener logs. |

**Threshold guidance**: Flag to IT if Status≠Done after 10 minutes.

---

### Service Restart / Recovery

After `SapReplitAPI` service restart, in-flight orchestrations are safe:
- ORDR already committed in SAP — persisted
- PLRs already persisted in MolasIntegration
- Delivery records persisted in MolasIntegration

On restart, the next API call to any endpoint will re-read live state from MolasIntegration and SAP. No data is lost.

**Outbox poller** (OutboxPollerHostedService) restarts automatically with the service.

---

### Idempotent Retry

All endpoints are idempotent:
- `POST /orders` with same RequestId + same payload → returns existing orchestration (HTTP 200)
- `POST .../pick` with same absEntry after pick already applied → returns `alreadyApplied=true` (HTTP 200)
- `POST .../delivery` when ODLN already exists → returns `ALL_FRAGMENTS_FULLY_DELIVERED` (HTTP 200)

Safe to retry any endpoint on timeout or network failure.

---

## Monitoring Queries (MolasIntegration MSSQL)

Run these periodically or on demand to detect stale states.

```sql
-- Orchestrations in recoverable failure states
SELECT fo.Id, fo.RequestId, fo.State, fo.FailureKind, fo.ErrorMessage, fo.UpdatedAtUtc
FROM dbo.FulfillmentOrchestration fo
WHERE fo.State = 'Accepted'
  AND fo.FailureKind = 'PickListAutoCreationFailed'
ORDER BY fo.UpdatedAtUtc;

-- Delivered orchestrations with no DeliveryRecord
SELECT fo.Id, fo.RequestId, fo.State, fo.SoDocEntry, fo.UpdatedAtUtc
FROM dbo.FulfillmentOrchestration fo
WHERE fo.State = 'Delivered'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.DeliveryRecord dr
      WHERE dr.OrchestrationId = fo.Id AND dr.Status = 'Created'
  );

-- Orchestrations WaitingForPicks (Accepted + PLRs exist but not all Picked)
SELECT fo.RequestId, fo.SoDocEntry, fo.DeliveryLocation,
       COUNT(plr.Id) AS TotalPLRs,
       SUM(CASE WHEN plr.Status = 'Picked' THEN 1 ELSE 0 END) AS PickedCount,
       MIN(plr.UpdatedAtUtc) AS OldestPickUpdate
FROM dbo.FulfillmentOrchestration fo
JOIN dbo.PickListRecord plr ON plr.OrchestrationId = fo.Id
WHERE fo.State = 'Accepted' AND fo.FailureKind IS NULL
GROUP BY fo.RequestId, fo.SoDocEntry, fo.DeliveryLocation
HAVING SUM(CASE WHEN plr.Status = 'Picked' THEN 1 ELSE 0 END) < COUNT(plr.Id);

-- 15/A events not Done (stuck outbox)
SELECT se.Id, se.DocEntry, se.ObjectType, se.TransactionType, se.Status,
       se.CreatedAtUtc, se.UpdatedAtUtc, se.ErrorMessage
FROM dbo.SapEventOutbox se
WHERE se.ObjectType = 15 AND se.Status <> 'Done'
ORDER BY se.CreatedAtUtc;

-- Canceled SAP orders with non-terminal Molas orchestration
-- (Requires cross-database or SAP-side MSSQL access for ORDR.CANCELED)
-- Run separately against SAP MSSQL:
-- SELECT fo.RequestId, fo.SoDocEntry, fo.State
-- FROM MolasIntegration.dbo.FulfillmentOrchestration fo
-- JOIN SAPDatabase.dbo.ORDR o ON o.DocEntry = fo.SoDocEntry
-- WHERE o.CANCELED = 'Y' AND fo.State NOT IN ('Canceled', 'Delivered', 'Failed');
```

---

## Failure Ownership Matrix

| Failure | Responsible | Action |
|---------|-------------|--------|
| Items not in expected bin | Warehouse Supervisor / Inventory | Physical investigation |
| Items qty mismatch at bin | Warehouse Supervisor / Inventory | Physical count, escalate |
| Confirm Pick returns error (API/SAP error) | IT | Check logs, fix SAP integration |
| `WaitingForOtherPicks` (normal) | Nobody (normal state) | Wait |
| `WaitingForOtherPicks` beyond threshold | Warehouse Supervisor | Check picker has OPKL |
| `AutoPickListFailed` | IT / Sales Operations | Call admin /pick-lists endpoint |
| `DeliveryBlocked` | IT / Sales Operations | Diagnose gateErrors, retry delivery |
| `REPICK_REQUIRED` | IT / Sales Operations | Issue repick OPKL |
| SAP order cancelled | Sales Operations | Call reconcile-cancelled endpoint |
| 15/A event stuck | IT | Check OutboxPoller, reset Pending |
| Invoice creation | NOT authorized — separate financial gate | N/A |

---

## Invoice Hard Stop

```
MUTATION_ENABLED = false in ZoneFulfillmentInvoiceService
```

No OINV will be created by the Zone Fulfillment system.
Do not modify `MUTATION_ENABLED`.
Do not create OINV manually for ZF deliveries while invoice phase is disabled.
ZF deliveries are excluded from `InvoiceFromDeliveryJob` by:

```sql
ISNULL(T0.U_ZoneRef,'') <> 'ZoneFulfillment'
```

Do not remove or modify this filter.

Invoice creation is a separate financial authorization gate — not yet authorized.

---

## SLA / Threshold Guidance

| Condition | Threshold | Action |
|-----------|-----------|--------|
| `WaitingForOtherPicks` | Informational up to end of warehouse shift | Flag to supervisor after normal shift time |
| `AutoPickListFailed` | Immediate | Supervisor / IT — call admin /pick-lists |
| `DeliveryBlocked` | Immediate | IT — diagnose gateErrors |
| 15/A event not Done | 10 minutes | IT — check OutboxPoller |
| Confirm Pick taking >30s | Immediate | IT — SAP DI API latency |

These are recommended configurable thresholds. Actual SLAs are defined by business operations.

---

## Protected Historical Documents (Do Not Touch)

| Document Type | AbsEntries / DocEntries |
|--------------|------------------------|
| ORDR | 28419, 28421, 28431, 28433, 28451, 28486 |
| OPKL | 6, 8, 9, 10, 11, 12, 13, 18, 19, 20 |
| ODLN | 30504, 30511, 30516 |

These are read-only historical documents. Do not cancel, reopen, update, or copy. Do not run SQL UPDATE against their SAP tables.
