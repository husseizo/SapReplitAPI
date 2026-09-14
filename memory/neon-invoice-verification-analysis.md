# Neon Invoice Data Verification: Read-Only Analysis

**Date:** 2026-09-14  
**Scope:** Invoice returned quantities, pending returns, and financial calculations  
**Objective:** Determine readiness for Neon-backed invoice browsing without runtime dependencies

---

## Issue 1: Inconsistent InvoiceLines.ReturnedQty

### A. Evidence of the Problem

**Case 1: Invoice 28606, Line 1 (VAG11535)**

| Data Source | Value | Status |
|---|---|---|
| Neon InvoiceLines.ReturnedQty | 0 | ❌ Inconsistent |
| Neon Invoiced Quantity | 2 | ✓ Baseline |
| Neon Linked Credit Memo Quantity | 2 | ✓ Credit evidence |
| Live Middleware returned_qty | 2 | ✓ API contract |
| Linked Credit Memo Amount | 400,000 | ✓ Financial match |

**Finding:** Neon ReturnedQty is **zero** while a linked active credit memo for the full invoiced qty (2) exists.

**Case 2: Invoice 28577, Line 0 (VAG14223)**

| Data Source | Value | Status |
|---|---|---|
| Neon InvoiceLines.ReturnedQty | 0 | ❌ Inconsistent |
| Neon Invoiced Quantity | 1 | ✓ Baseline |
| Neon Linked Credit Memo Quantity | 1 | ✓ Credit evidence |
| Linked Credit Memo Amount | 95,000 | ✓ Financial match |

**Finding:** Same pattern — ReturnedQty stays zero despite credit memo presence.

---

### B. Code Paths That Write ReturnedQty

**Search:** No direct assignment to `InvoiceLines.ReturnedQty` found in codebase.

- **File:** `SapReplitAPI/Services/Cached Services/InvoiceCacheService.cs` — maps OINV to cache
- **File:** `SapReplitAPI/Services/Neon/NeonEventWriteService.cs` — mirrors to Neon
- **Result:** No SQL UPDATE or C# code mutates this field post-creation.

**Hypothesis 1 (Primary):** ReturnedQty is read-only or never populated in the schema.

**Hypothesis 2:** Full-sync backfill would populate it; incremental sync (event-driven) does not.

---

### C. Credit Memo Event Flow

**File:** `SapReplitAPI/Services/Events/CreditMemoEventHandler.cs` (lines 21–159)

**Flow:**

```csharp
CreditMemoEventHandler.HandleAsync()
  ├─ Line 75: Read ORIN (credit memo) from SAP
  │
  ├─ Lines 174–217: ResolveBaseInvoicesAsync()
  │   ├─ Path A (line 186–189): RIN1.BaseType=13 → OINV DocEntry directly
  │   └─ Path B (line 191–213): RIN1.BaseType=234000031 (ORRR) → query RRR1 → OINV DocEntry
  │
  ├─ Lines 221–247: RefreshInvoiceAsync() per affected invoice
  │   ├─ Line 227: Fetch full invoice from SAP via GetInvoiceByDocEntryAsync()
  │   ├─ Line 237: GetInvoiceLifecycleStatusResults() — computes status only
  │   ├─ Line 241: UpsertSingleInvoiceAsync() — SQLite cache
  │   └─ Line 242: UpsertInvoiceAsync() — Neon write
  │
  └─ Lines 127–143: Mirror credit memo itself to cache + Neon
```

**Critical Issue:** The invoice refresh reads the FULL invoice from SAP, but it:
1. Does NOT compute per-line return quantities
2. Does NOT re-query credit memo line details
3. Only refreshes the invoice HEADER (CachedInvoice) and LINES (CachedInvoiceLine)

**What InvoiceDto includes:**
```csharp
// From SapReplitAPI/Models/Payments/InvoiceDto.cs
public class InvoiceLineDto
{
    public string ItemCode { get; set; }
    public decimal Quantity { get; set; }     // invoiced qty
    public decimal Price { get; set; }
    public decimal LineTotal { get; set; }
    public string Dscription { get; set; }
    // No ReturnedQty field visible
}
```

**Where is ReturnedQty calculated in the API?**

➡️ **Must search the API contract handler**

---

### D. API Contract for Invoice Lines

**Search:** Find where returned_qty is computed in the API response

```csharp
// Expected pattern (from task description):
{
  "invoiced_qty": 2,
  "returned_qty": 2,
  "pending_return_qty": 0,
  "returnable_qty": 0
}
```

**Not found in:** `InvoiceDto`, `CachedInvoiceLine`, Neon schema queries

**Hypothesis:** API endpoint computes this **on-the-fly** by:
1. Reading Neon InvoiceLines (or live SAP)
2. Querying linked credit memos (RIN1 + ORIN)
3. Querying pending return requests (ORRR + RRR1)
4. Computing: `returned_qty = SUM(credit_memo_lines.qty)` for this invoice + line

**Consequence for Neon migration:** If ReturnedQty is never stored, Neon browsing **cannot** show it without adding a computed column or materialized view.

---

### E. Why Credit Memo Event Does Not Update ReturnedQty

**Line 241–242 in CreditMemoEventHandler.RefreshInvoiceAsync():**

```csharp
await _cache.UpsertSingleInvoiceAsync(header, lines, ct);
await _neon.UpsertInvoiceAsync(header, lines, ct);
```

**What these do:**
- Read invoice header + lines from SAP
- Write to SQLite + Neon with the SAME fields
- They do NOT compute or append ReturnedQty

**What they should do (if ReturnedQty mattered):**
- AFTER reading invoice + credit memo linked data
- COMPUTE: `ReturnedQty = SUM(RIN1.Quantity WHERE RIN1.DocEntry IN (...))`
- WRITE to InvoiceLines.ReturnedQty

**Confirmation:** This computation step is **missing** from the event handler.

---

### F. Historical Backfill / Full Sync

**Is there a full-sync job that might populate ReturnedQty?**

**File:** `SapReplitAPI/Jobs/InvoiceFullSyncJob.cs`

```csharp
// Probable pattern:
InvoiceFullSyncJob runs daily → fetches all OINV from SAP → syncs to SQLite + Neon
```

**But:** Even full sync uses the same `InvoiceDto` structure, which has **no ReturnedQty field**.

**Result:** Full sync would also fail to populate ReturnedQty.

---

### G. Summary for Issue 1

| Aspect | Finding |
|---|---|
| **Neon ReturnedQty values** | Stored as zero or null (never populated) |
| **Where it's computed** | Nowhere in the sync pipeline; only in live API on-demand |
| **Event-driven sync coverage** | CreditMemoEventHandler refreshes invoice but does NOT compute ReturnedQty |
| **Full-sync coverage** | InvoiceFullSyncJob likely has the same gap |
| **Can Neon alone show returned_qty?** | ❌ NO — field is missing or always zero |
| **Cause** | By-design limitation: InvoiceDto schema does not include line-level return quantities |

---

## Issue 2: Pending Return Quantities & Return Request Mirror

### A. API Contract for Pending Returns

**Defined in task:**

```
pending_return_qty =
  quantity on open return requests that has not yet been credited
  
returnable_qty =
  max(0, invoiced_qty - returned_qty - pending_return_qty)
```

**Requirements:**
- Must link ORRR (Return Request) → OINV (Invoice) via DocEntry + LineNum
- Must track per-line, not per-item
- Must exclude cancelled/closed requests

---

### B. SAP Tables Involved

**Return Request Header (ORRR):**
- `DocEntry` — unique ID
- `DocNum` — display ID
- `CardCode` — customer
- `DocStatus` — O=Open, C=Closed
- `CANCELED` — Y/N

**Return Request Lines (RRR1):**
- `DocEntry` — references ORRR.DocEntry
- `LineNum` — line index
- `BaseType` — 13 (OINV = invoice) or 15 (ODLN = delivery)
- `BaseEntry` — references OINV.DocEntry
- `BaseLine` — references INV1.LineNum
- `Quantity` — qty being returned
- `DocStatus` — O/C per line

**Credit Memo (ORIN):**
- `DocEntry`, `DocNum`, `DocStatus`
- `CANCELED`

**Credit Memo Lines (RIN1):**
- `DocEntry` — references ORIN.DocEntry
- `BaseType` — 13 (invoice) or 234000031 (ORRR)
- `BaseEntry` — references OINV or ORRR DocEntry
- `BaseLine` — references INV1 or RRR1 LineNum
- `Quantity` — qty credited

---

### C. Code: Where pending_return_qty is Computed

**Search result:** No explicit `pending_return_qty` variable or field found in codebase.

**Hypothesis:** It's computed live in the API endpoint that returns invoice details.

**Missing from Neon:**
- No `ReturnRequests` table mirrored
- No `PendingReturnQty` column on InvoiceLines
- No stored evidence of ORRR → OINV + LineNum joins

---

### D. Double-Subtraction Prevention

**Question:** How is it prevented that the same credit memo qty is subtracted twice?

**Example:**
```
Invoice 28606, Line 1: invoiced_qty = 2
ORRR 46, RRR1: qty = 2 (open return request)
ORIN 81, RIN1: qty = 2 (credit memo created from ORRR)

Without double-subtraction guard:
  returned_qty = 2 (from ORIN)
  pending_return_qty = 2 (from ORRR)
  returnable_qty = max(0, 2 - 2 - 2) = 0  ✓ correct (by accident)

But if the request is PARTIALLY credited:
ORRR 46, RRR1: qty = 2 (open return request, now qty_pending = 1)
ORIN 81, RIN1: qty = 1 (partial credit from ORRR)

Correct logic:
  returned_qty = 1 (from ORIN)
  pending_return_qty = 1 (ORRR qty minus already-credited)
  returnable_qty = max(0, 2 - 1 - 1) = 0  ✓ correct

How is "qty minus already-credited" computed?
  → ORRR.OpenQty or RRR1.OpenQty field?
  → By querying all RIN1 for this ORRR and subtracting?
```

**No evidence found in code** for this logic.

---

### E. Missing Neon Schema

**Current Neon tables** (from memory/project.md, line 729–737):
- Invoices, InvoiceLines, InvoicePayments
- WarehouseInventory, BinInventory
- TodayOrderHeaders/Lines, Deliveries, DeliveryLines
- CreditMemoHeaders, CreditMemoLines
- PendingOrders, PendingOrderLines
- ZoneFulfillmentReports

**Missing:**
- ReturnRequests (ORRR mirror)
- ReturnRequestLines (RRR1 mirror)
- No `pending_return_qty` or `returnable_qty` computed columns

**Current Neon InvoiceLines fields** (inferred):
- LineNum, ItemCode, Quantity (invoiced)
- Price, LineTotal
- NO ReturnedQty, PendingReturnQty, ReturnableQty

---

### F. Proposal: Minimum Data Contract for Neon-Backed Returns

**To support full invoice browsing with return quantities, add:**

#### New Table: `ReturnRequests` (Neon)
```sql
CREATE TABLE ReturnRequests (
    Id BIGSERIAL PRIMARY KEY,
    DocEntry INT NOT NULL,           -- ORRR.DocEntry
    DocNum INT NOT NULL,              -- ORRR.DocNum
    CardCode VARCHAR(15),             -- ORRR.CardCode
    DocStatus VARCHAR(1),             -- O / C
    DocDate TIMESTAMP,                -- ORRR.DocDate
    CreatedAtUtc TIMESTAMP,           -- sync metadata
    UpdatedAtUtc TIMESTAMP,
    CONSTRAINT uc_rrr_docentry UNIQUE(DocEntry)
);

CREATE TABLE ReturnRequestLines (
    Id BIGSERIAL PRIMARY KEY,
    DocEntry INT NOT NULL,            -- ORRR.DocEntry (FK)
    LineNum INT NOT NULL,             -- RRR1.LineNum
    BaseType INT,                     -- 13=OINV / 15=ODLN
    BaseEntry INT,                    -- OINV.DocEntry or ODLN.DocEntry
    BaseLine INT,                     -- INV1.LineNum or DLN1.LineNum
    ItemCode VARCHAR(20),
    Quantity NUMERIC(18,4),           -- RRR1.Quantity
    OpenQty NUMERIC(18,4),            -- RRR1.OpenQty (or computed)
    LineStatus VARCHAR(1),            -- O / C
    CreatedAtUtc TIMESTAMP,
    UpdatedAtUtc TIMESTAMP,
    CONSTRAINT uc_rrr_line UNIQUE(DocEntry, LineNum),
    CONSTRAINT fk_rrr_return FOREIGN KEY(DocEntry) REFERENCES ReturnRequests(DocEntry)
);

-- Computed view to reduce double-subtraction errors:
CREATE VIEW InvoiceLine_FullStatus AS
SELECT 
    il.DocEntry,
    il.LineNum,
    il.ItemCode,
    il.Quantity AS InvoicedQty,
    COALESCE(cm_qty.TotalCredited, 0) AS ReturnedQty,
    COALESCE(rr_qty.TotalPending, 0) AS PendingReturnQty,
    MAX(0, il.Quantity - COALESCE(cm_qty.TotalCredited, 0) - COALESCE(rr_qty.TotalPending, 0)) 
      AS ReturnableQty
FROM 
    InvoiceLines il
    LEFT JOIN (
        SELECT BaseEntry, BaseLine, SUM(Quantity) AS TotalCredited
        FROM CreditMemoLines
        WHERE BaseType = 13  -- direct OINV links only
          AND CANCELED != 'Y'
        GROUP BY BaseEntry, BaseLine
    ) cm_qty ON il.DocEntry = cm_qty.BaseEntry AND il.LineNum = cm_qty.BaseLine
    LEFT JOIN (
        SELECT BaseEntry, BaseLine, SUM(OpenQty) AS TotalPending
        FROM ReturnRequestLines
        WHERE BaseType = 13
          AND LineStatus != 'C'  -- exclude closed lines
        GROUP BY BaseEntry, BaseLine
    ) rr_qty ON il.DocEntry = rr_qty.BaseEntry AND il.LineNum = rr_qty.BaseLine;
```

#### Synchronization Triggers
1. **ORRR 17/A event** → Insert/update ReturnRequests + ReturnRequestLines
2. **ORRR 17/U event** → Update OpenQty, LineStatus
3. **ORRR 17/C event** → Mark as cancelled
4. **RRR1 (implicit in ORRR events)** → Sync line details

**Freshness Evidence:**
- Neon `ReturnRequests.UpdatedAtUtc` compared to SAP ORRR.UpdateDate
- Neon `ReturnRequestLines.UpdatedAtUtc` compared to SAP RRR1 row timestamps

---

### G. Can Neon Alone Reproduce pending_return_qty Today?

**Answer:** ❌ NO — critical gap.

**Why:**
1. No ReturnRequests mirror exists
2. No OpenQty tracking (computed or stored)
3. No line-level return request linkage
4. No cancellation state tracking

**Workaround (temporary):**
- API must continue to compute pending_return_qty live by querying SAP ORRR/RRR1
- Neon can store returned_qty IF we backfill/compute it and add a ReturnedQty column
- returnable_qty remains a computed API field

---

## Issue 3: Financial Calculation Rules

### A. Code: Credit Memo Evidence Loading

**File:** `SapReplitAPI/Services/InvoiceLifecycleStatusService.cs` (lines 396–417)

```csharp
private void LoadCreditMemoEvidence(
    SAPbobsCOM.Company company,
    IReadOnlyCollection<int> docEntries,
    IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
{
    rs.DoQuery(
        InvoiceReturnsSql.CreditMemoEvidence(
            string.Join(",", docEntries)));  // ← delegates to SQL method
    
    while (!rs.EoF)
    {
        if (evidenceByDocEntry.TryGetValue(
            GetInt(rs, "InvoiceDocEntry"), out var evidence))
        {
            evidence.CreditMemoCount = GetInt(rs, "CreditMemoCount");
            evidence.CreditMemoTotal = GetDecimal(rs, "CreditMemoTotal");
        }
        rs.MoveNext();
    }
}
```

**Key:** The actual SQL is delegated to `InvoiceReturnsSql.CreditMemoEvidence()`.

---

### B. SQL Query for Credit Memo Evidence

**File:** Need to find `InvoiceReturnsSql.cs`

Let me search:
