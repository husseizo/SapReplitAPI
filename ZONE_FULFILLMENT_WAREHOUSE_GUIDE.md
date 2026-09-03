# Zone Fulfillment — Warehouse User Guide

## Your Role

You physically pick items and press **Confirm Pick**.
That is the only action required of warehouse staff.

You do **not**:
- Create a Pick List
- Create a Delivery
- Dispatch anything in SAP
- Touch any other system

---

## Step 1 — Open Your Assigned Pick List

When a sales order is placed for your zone, the system creates a Pick List for your warehouse automatically and assigns it to you.

Your screen shows:

| Field | Description |
|-------|-------------|
| Pick List # | SAP OPKL AbsEntry |
| Warehouse | Your warehouse code (e.g., 003) |
| Assigned Picker | Your name |
| Sales Order | SAP ORDR DocEntry |
| Zone / Delivery Location | e.g., Mikocheni-side |
| Reference | U_ReplitId (short order reference) |

For each item on the list:

| Field | Description |
|-------|-------------|
| Item Code | SAP item code |
| Required Qty | Quantity to pick |
| Bin | The specific bin to pick from |

---

## Step 2 — Physically Collect the Items

Go to the specified bin.
Take the specified quantity.
Count before confirming.

---

## Step 3 — Press CONFIRM PICK

Press the **CONFIRM PICK** button.

That is the last warehouse action.

---

## What You Will See After Confirming

### A — Other Warehouses Are Still Picking

```
Pick confirmed.
Waiting for warehouse 003.
```

This is normal. The system is waiting for the other warehouses in the order to finish their picks.
No action required from you.

---

### B — All Picks Are Complete, Delivery Being Created

```
All warehouse picks are complete.
Delivery is being created automatically.
```

The system detected that all warehouses have confirmed. It is now creating the delivery automatically.
No action required from you.

---

### C — Delivery Created Successfully

```
Delivery created successfully.
ODLN 30561.
```

The delivery is done. No further action required.

---

### D — Delivery Could Not Be Created

```
Pick confirmed, but Delivery could not be created automatically.
No action required from picker.
Please contact your supervisor.
```

Your pick was recorded correctly. A system or data issue prevented automatic delivery.
**Do not attempt to create a delivery manually in SAP.**
Contact your supervisor. They will resolve it.

---

## What You Must NOT Do

| Action | Why Not |
|--------|---------|
| Create a Pick List in SAP | System creates them automatically |
| Create a Delivery in SAP | System creates it automatically |
| Dispatch a delivery in SAP | There is no dispatch step |
| Press Confirm Pick a second time for the same order | Not needed; pick is recorded |
| Contact IT for normal waiting state (A above) | Waiting is normal — another warehouse is still picking |

---

## Who To Contact

| Situation | Contact |
|-----------|---------|
| Cannot find items in the bin | Supervisor / Inventory team |
| Items in bin do not match required qty | Supervisor |
| Confirm Pick returns an error | Supervisor / IT |
| Delivery Could Not Be Created (state D) | Supervisor |
| System is down or unresponsive | IT |
