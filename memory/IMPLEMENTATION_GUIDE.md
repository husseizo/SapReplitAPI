# Return Request (ORRR) Event Handler — Implementation & Deployment Guide

**Date:** 2026-09-14  
**Scope:** Synchronize SAP Return Requests (ORRR + RRR1) to Neon and recalculate invoice pending/returnable quantities  
**Status:** Code complete. Manual database + configuration steps required before deployment.

---

## Overview

This implementation adds event-driven synchronization of Return Requests from SAP to Neon, following the same pattern as Credit Memos:

1. **ReturnRequestEventHandler.cs** — Listens for ORRR events (ObjectType=234000031)
2. **NeonReturnRequestWriteService.cs** — Atomic Neon writes (UPSERT + recalculation)
3. **ReturnRequestDto.cs** — Data transfer objects (ORRR header + RRR1 lines)
4. **New Neon tables** — ReturnRequests, ReturnRequestLines
5. **New InvoiceLines columns** — PendingReturnQty, ReturnableQty (computed)

---

## What's New

### Tables (Neon PostgreSQL)

#### ReturnRequests
```sql
CREATE TABLE "ReturnRequests" (
    "DocEntry" INTEGER PRIMARY KEY,
    "DocNum" INTEGER NOT NULL,
    "CardCode" VARCHAR(15),
    "CardName" VARCHAR(100),
    "DocDate" DATE,
    "DocStatus" VARCHAR(1) CHECK ("DocStatus" IN ('O', 'C')),
    "Canceled" VARCHAR(1) DEFAULT 'N' CHECK ("Canceled" IN ('Y', 'N')),
    "DocTotal" NUMERIC(18,4),
    "Comments" TEXT,
    "U_AppRef" VARCHAR(50) UNIQUE,
    "U_ReplitId" VARCHAR(50),
    "UpdatedAtUtc" TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    CONSTRAINT uc_orrr_appref UNIQUE("U_AppRef") WHERE "U_AppRef" IS NOT NULL
);

CREATE INDEX idx_orrr_cardcode ON "ReturnRequests"("CardCode");
CREATE INDEX idx_orrr_docdate ON "ReturnRequests"("DocDate");
CREATE INDEX idx_orrr_status ON "ReturnRequests"("DocStatus", "Canceled");
```

#### ReturnRequestLines
```sql
CREATE TABLE "ReturnRequestLines" (
    "DocEntry" INTEGER NOT NULL,
    "LineNum" INTEGER NOT NULL,
    "BaseType" INTEGER,              -- 13=OINV, 15=ODLN
    "BaseEntry" INTEGER,             -- OINV.DocEntry or ODLN.DocEntry
    "BaseLine" INTEGER,              -- INV1.LineNum or DLN1.LineNum
    "ItemCode" VARCHAR(20),
    "Dscription" TEXT,
    "Quantity" NUMERIC(18,4),        -- Total qty being returned
    "OpenQty" NUMERIC(18,4),         -- Qty not yet credited
    "WhsCode" VARCHAR(8),
    "LineStatus" VARCHAR(1) DEFAULT 'O' CHECK ("LineStatus" IN ('O', 'C')),
    "UpdatedAtUtc" TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    PRIMARY KEY("DocEntry", "LineNum"),
    CONSTRAINT fk_rrr_return FOREIGN KEY("DocEntry") REFERENCES "ReturnRequests"("DocEntry") ON DELETE CASCADE
);

CREATE INDEX idx_rrl_basetype_entry ON "ReturnRequestLines"("BaseType", "BaseEntry", "BaseLine");
CREATE INDEX idx_rrl_itemcode ON "ReturnRequestLines"("ItemCode");
```

### InvoiceLines Modifications

```sql
-- Add new columns to InvoiceLines
ALTER TABLE "InvoiceLines" ADD COLUMN "ReturnedQty" NUMERIC(18,4) DEFAULT 0;
ALTER TABLE "InvoiceLines" ADD COLUMN "PendingReturnQty" NUMERIC(18,4) DEFAULT 0;

-- Computed column (optional, for convenience)
ALTER TABLE "InvoiceLines" ADD COLUMN "ReturnableQty" NUMERIC(18,4) 
  GENERATED ALWAYS AS (
    GREATEST(0, "Quantity" - COALESCE("ReturnedQty", 0) - COALESCE("PendingReturnQty", 0))
  ) STORED;

CREATE INDEX idx_invl_returned_qty ON "InvoiceLines"("ReturnedQty");
CREATE INDEX idx_invl_pending_qty ON "InvoiceLines"("PendingReturnQty");
```

---

## Deployment Steps

### Step 1: Database Migrations (Neon PostgreSQL)

#### 1a. Create ReturnRequests table

```bash
# SSH into production database or use pgAdmin
psql -h <neon-host> -U <user> -d <database> -c "

CREATE TABLE IF NOT EXISTS \"ReturnRequests\" (
    \"DocEntry\" INTEGER PRIMARY KEY,
    \"DocNum\" INTEGER NOT NULL,
    \"CardCode\" VARCHAR(15),
    \"CardName\" VARCHAR(100),
    \"DocDate\" DATE,
    \"DocStatus\" VARCHAR(1),
    \"Canceled\" VARCHAR(1) DEFAULT 'N',
    \"DocTotal\" NUMERIC(18,4),
    \"Comments\" TEXT,
    \"U_AppRef\" VARCHAR(50),
    \"U_ReplitId\" VARCHAR(50),
    \"UpdatedAtUtc\" TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_orrr_cardcode ON \"ReturnRequests\"(\"CardCode\");
CREATE INDEX IF NOT EXISTS idx_orrr_docdate ON \"ReturnRequests\"(\"DocDate\");
CREATE INDEX IF NOT EXISTS idx_orrr_status ON \"ReturnRequests\"(\"DocStatus\", \"Canceled\");
"
```

#### 1b. Create ReturnRequestLines table

```bash
psql -h <neon-host> -U <user> -d <database> -c "

CREATE TABLE IF NOT EXISTS \"ReturnRequestLines\" (
    \"DocEntry\" INTEGER NOT NULL,
    \"LineNum\" INTEGER NOT NULL,
    \"BaseType\" INTEGER,
    \"BaseEntry\" INTEGER,
    \"BaseLine\" INTEGER,
    \"ItemCode\" VARCHAR(20),
    \"Dscription\" TEXT,
    \"Quantity\" NUMERIC(18,4),
    \"OpenQty\" NUMERIC(18,4),
    \"WhsCode\" VARCHAR(8),
    \"LineStatus\" VARCHAR(1) DEFAULT 'O',
    \"UpdatedAtUtc\" TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    PRIMARY KEY(\"DocEntry\", \"LineNum\"),
    CONSTRAINT fk_rrr_return FOREIGN KEY(\"DocEntry\") REFERENCES \"ReturnRequests\"(\"DocEntry\") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_rrl_basetype_entry ON \"ReturnRequestLines\"(\"BaseType\", \"BaseEntry\", \"BaseLine\");
CREATE INDEX IF NOT EXISTS idx_rrl_itemcode ON \"ReturnRequestLines\"(\"ItemCode\");
"
```

#### 1c. Modify InvoiceLines

```bash
psql -h <neon-host> -U <user> -d <database> -c "

ALTER TABLE \"InvoiceLines\" ADD COLUMN IF NOT EXISTS \"ReturnedQty\" NUMERIC(18,4) DEFAULT 0;
ALTER TABLE \"InvoiceLines\" ADD COLUMN IF NOT EXISTS \"PendingReturnQty\" NUMERIC(18,4) DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_invl_returned_qty ON \"InvoiceLines\"(\"ReturnedQty\");
CREATE INDEX IF NOT EXISTS idx_invl_pending_qty ON \"InvoiceLines\"(\"PendingReturnQty\");
"
```

### Step 2: Code Registration (Program.cs)

**Location:** `SapReplitAPI/Program.cs`, around line 120–150

**Add these services:**

```csharp
// ── Return Request (ORRR) event sync ─────────────────────────────────────
builder.Services.AddScoped<NeonReturnRequestWriteService>();
builder.Services.AddScoped<ReturnRequestEventHandler>();
```

**Register the event handler in EventHandlerRouter:**

Find the section where `CreditMemoEventHandler` is registered, usually in a factory or router class.
Add similar registration for `ReturnRequestEventHandler`.

**Location (probable):** `SapReplitAPI/Services/Events/EventHandlerRouter.cs` or similar

```csharp
private readonly IServiceProvider _serviceProvider;

private List<ISapEventHandler> GetAllHandlers()
{
    return new()
    {
        // Existing handlers...
        _serviceProvider.GetRequiredService<CreditMemoEventHandler>(),
        // NEW: Add this
        _serviceProvider.GetRequiredService<ReturnRequestEventHandler>(),
        // ... rest
    };
}
```

### Step 3: Backfill Historical Data

**Purpose:** Populate Neon with all existing ORRR data before handlers start running.

**Run this SQL on Neon to backfill from SAP (if reachable):**

```sql
-- Backfill ReturnRequests
INSERT INTO \"ReturnRequests\"
  (\"DocEntry\", \"DocNum\", \"CardCode\", \"CardName\", \"DocDate\", \"DocStatus\", \"Canceled\", \"DocTotal\", \"UpdatedAtUtc\")
SELECT 
  H.DocEntry, H.DocNum, H.CardCode, H.CardName, H.DocDate, 
  H.DocStatus, ISNULL(H.CANCELED, 'N'), H.DocTotal, GETDATE()
FROM ORRR H
WHERE H.CANCELED != 'Y'
ON CONFLICT (\"DocEntry\") DO NOTHING;

-- Backfill ReturnRequestLines
INSERT INTO \"ReturnRequestLines\"
  (\"DocEntry\", \"LineNum\", \"BaseType\", \"BaseEntry\", \"BaseLine\", 
   \"ItemCode\", \"Dscription\", \"Quantity\", \"OpenQty\", \"WhsCode\", \"LineStatus\", \"UpdatedAtUtc\")
SELECT 
  L.DocEntry, L.LineNum, L.BaseType, L.BaseEntry, L.BaseLine, 
  L.ItemCode, L.Dscription, L.Quantity, L.OpenQty, L.WhsCode, 
  ISNULL(L.LineStatus, 'O'), GETDATE()
FROM RRR1 L
ON CONFLICT (\"DocEntry\", \"LineNum\") DO NOTHING;

-- Recalculate all InvoiceLines.PendingReturnQty
UPDATE \"InvoiceLines\"
SET \"PendingReturnQty\" = (
  SELECT COALESCE(SUM(rr.\"OpenQty\"), 0)
  FROM \"ReturnRequestLines\" rr
  JOIN \"ReturnRequests\" rh ON rr.\"DocEntry\" = rh.\"DocEntry\"
  WHERE rr.\"BaseEntry\" = \"InvoiceLines\".\"DocEntry\"
    AND rr.\"BaseLine\" = \"InvoiceLines\".\"LineNum\"
    AND rr.\"BaseType\" = 13
    AND rh.\"Canceled\" = 'N'
    AND rh.\"DocStatus\" = 'O'
    AND rr.\"LineStatus\" = 'O'
);
```

### Step 4: Build & Deploy

```bash
# Build the solution
dotnet build SapReplitAPI.sln -c Release

# Publish (Windows Service or Docker)
if Windows:
  dotnet publish SapReplitAPI.csproj -c Release -o ./publish
  # Copy to C:\SAPAPI\ and restart Windows Service
else:
  # Docker: rebuild and push image
  docker build -t sapreplitapi:latest .
  docker push <registry>/sapreplitapi:latest
  # Redeploy via Kubernetes or Docker Compose
```

### Step 5: Verify Deployment

#### 5a. Check logs for handler registration

```bash
# Tail logs
tail -f /var/log/sap-replit/app.log | grep ReturnRequestHandler

# Expected: "ReturnRequestHandler registered" or similar
```

#### 5b. Create a test return request in SAP

1. Go to Salesorder in SAP
2. Select an invoice with qty > 1
3. Create Return Request (ORRR) with qty = 1
4. Submit

#### 5c. Verify Neon sync

```sql
-- Check if ORRR was synced
SELECT * FROM \"ReturnRequests\" ORDER BY \"UpdatedAtUtc\" DESC LIMIT 1;

-- Check related lines
SELECT * FROM \"ReturnRequestLines\" WHERE \"DocEntry\" = <test_orrr_entry>;

-- Verify invoice pending qty updated
SELECT \"DocEntry\", \"LineNum\", \"Quantity\", \"ReturnedQty\", \"PendingReturnQty\", \"ReturnableQty\"
FROM \"InvoiceLines\"
WHERE \"DocEntry\" = <test_invoice_entry>;

-- Expected: PendingReturnQty = 1, ReturnableQty = Quantity - ReturnedQty - 1
```

#### 5d. Check middleware logs

```bash
grep -i "ReturnRequestHandler" C:\SAPLogs\app.log

# Expected output:
# [ReturnRequestHandler] Done: OrrrDocEntry=<X> DocNum=<Y> AffectedInvoices=1 Refreshed=1 RrrLines=1 ...
```

---

## Testing Scenarios

### Scenario 1: Simple Return Request (Single Invoice, Single Line)

**Setup:**
- Invoice 28000, Line 0, Item "ABC", Qty 10
- Credit memo total = 0 (no credits yet)

**Action:**
- Create ORRR 500 for Invoice 28000, qty 5

**Expected Results:**

| Field | Before | After |
|---|---|---|
| PendingReturnQty | 0 | 5 |
| ReturnableQty | 10 | 5 |

### Scenario 2: Partial Credit

**Setup:**
- Invoice 28000, Line 0, Qty 10
- ORRR 500 created, qty 5 (pending)
- ORIN 1000 created from ORRR, qty 3 (credited)

**Expected:**
- ReturnedQty = 3 (from credit memo)
- PendingReturnQty = 2 (5 requested - 3 already credited)
- ReturnableQty = 5 (10 - 3 - 2)

### Scenario 3: Return Request Cancellation

**Setup:**
- ORRR 500 with qty 5 (pending)
- Cancel ORRR 500 (set CANCELED=Y)

**Expected:**
- PendingReturnQty for linked invoice = 0 (cancelled requests excluded)
- ReturnableQty increases by 5

---

## Rollback Plan

**If issues occur during/after deployment:**

### Immediate Rollback

```bash
# Stop the service
sudo systemctl stop SapReplitAPI

# Revert to previous release
cd /opt/sap-api
git checkout <previous-tag>
dotnet publish -c Release

# Restart
sudo systemctl start SapReplitAPI
```

### Database Rollback

If new tables/columns caused issues:

```sql
-- Drop new tables (data will be recreated on next sync)
DROP TABLE IF EXISTS \"ReturnRequestLines\";
DROP TABLE IF EXISTS \"ReturnRequests\";

-- Remove columns (optional, can keep for future re-deployment)
ALTER TABLE \"InvoiceLines\" DROP COLUMN IF EXISTS \"PendingReturnQty\";
ALTER TABLE \"InvoiceLines\" DROP COLUMN IF EXISTS \"ReturnableQty\";
```

---

## Support & Troubleshooting

### Issue: Handler not processing ORRR events

**Symptoms:** No log entries for ReturnRequestHandler

**Solutions:**
1. Verify handler is registered in Program.cs
2. Check EventHandlerRouter includes ReturnRequestEventHandler
3. Verify SAP outbox events are being written (check SapEventOutbox table)

### Issue: Neon sync timeout

**Symptoms:** `[NeonReturnRequest] UpsertReturnRequestAsync failed ... Connection timeout`

**Solutions:**
1. Check Neon connection string in appsettings.json
2. Verify Neon PostgreSQL is accessible from middleware host
3. Increase NpgsqlCommand.CommandTimeout in NeonReturnRequestWriteService

### Issue: PendingReturnQty not recalculating

**Symptoms:** Manual SQL shows correct data, but API response shows old values

**Solutions:**
1. Verify NeonEventWriteService recalculation SQL (lines 130–147)
2. Check for stale API cache (flush SQLite cache)
3. Manually trigger invoice refresh: DELETE from InvoiceLines, resync

### Issue: Referential integrity error

**Symptoms:** `ERROR: insert or update on table "ReturnRequestLines" violates foreign key constraint`

**Solutions:**
1. Ensure ReturnRequests table has the ORRR DocEntry before inserting RRR1
2. Verify backfill script ran in correct order (headers before lines)

---

## Performance & Optimization

### Query Tuning

**PendingReturnQty recalculation (heaviest operation):**

```sql
-- Current: per-invoice recalculation after each ORRR event
-- Optimized: batch recalculation for all affected invoices

UPDATE \"InvoiceLines\" il
SET \"PendingReturnQty\" = subq.pending_qty
FROM (
  SELECT DISTINCT 
    rr.\"BaseEntry\" as inv_doc_entry,
    rr.\"BaseLine\" as inv_line_num,
    COALESCE(SUM(rr.\"OpenQty\"), 0) as pending_qty
  FROM \"ReturnRequestLines\" rr
  JOIN \"ReturnRequests\" rh ON rr.\"DocEntry\" = rh.\"DocEntry\"
  WHERE rr.\"BaseType\" = 13
    AND rh.\"Canceled\" = 'N'
    AND rh.\"DocStatus\" = 'O'
    AND rr.\"LineStatus\" = 'O'
  GROUP BY rr.\"BaseEntry\", rr.\"BaseLine\"
) subq
WHERE il.\"DocEntry\" = subq.inv_doc_entry
  AND il.\"LineNum\" = subq.inv_line_num;
```

### Index Strategy

- **Hot path:** `idx_rrl_basetype_entry` — used in PendingReturnQty lookup
- **Secondary:** `idx_orrr_status` — filters for open/uncancelled requests
- **Optional:** Add covering index if API frequently queries (DocEntry, LineNum, OpenQty)

---

## Future Enhancements

1. **API Endpoint:** `GET /api/return-requests/{invoiceDocEntry}` — list pending returns for invoice
2. **Analytics:** Materialized view for return metrics by customer/item/period
3. **Webhooks:** Notify external systems on return request status changes
4. **Credit Memo Auto-Creation:** Generate ORIN automatically when RRR qty >= threshold

---

## Contacts & Escalation

- **Deployment Issues:** DevOps team
- **SAP Integration Issues:** SAP Administrator
- **Neon Database Issues:** Cloud database team
- **Code Bugs:** Development team
