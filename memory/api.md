# API Contracts & Field Mappings

## POST /api/Customers

### Request DTO: `CreateCustomerDto`
| JSON field | C# property | SAP field | Notes |
|-----------|-------------|-----------|-------|
| `cardName` | `CardName` | `bp.CardName` | Required |
| `phone` | `Phone` | `bp.Phone1` + `U_Phone` | Optional |
| `customerType` | `CustomerType` | `U_Customer_Type` | Optional |
| `region` | `Region` | `U_REGION` | Preferred. Falls back to `city` if empty |
| `city` | `City` | `U_REGION` | ODOO fallback — "dar es salaam" → "DAR ES SALAAM" |
| `address` | `Address` | `bp.Address` + billing address | Legacy field |
| `address1` | `Address1` | `bp.Address` + billing address | ODOO field name |
| `salesPersonCode` | `SlpCode` | `bp.SalesPersonCode` | Preferred over salesPersonName |
| `salesPersonName` | `SalesPersonName` | `bp.SalesPersonCode` (looked up) | Fallback |
| `viN1` / `VIN1` | `VIN1` | `U_VIN1` | Optional (`string?`) |
| `viN2` / `VIN2` | `VIN2` | `U_VIN2` | Optional (`string?`) |
| `viN3` / `VIN3` | `VIN3` | `U_VIN3` | Optional (`string?`) |

### Valid Region Values (U_REGION)
```
ARUSHA, DAR ES SALAAM, DODOMA, GEITA, IRINGA, KAGERA, KATAVI, KIGOMA,
KILIMANJARO, LINDI, MANYARA, MARA, MBEYA, MOROGORO, MTWARA, MWANZA,
NJOMBE, PEMBA, PWANI, RUKWA, RUVUMA, SHINYANGA, SIMIYU, SINGIDA,
SONGWE, TABORA, TANGA, UNGUJA
```
Empty/null input → `"CHOSE REGION"`. Invalid input → throws exception (500).

### Response
```json
{ "message": "Customer created successfully", "cardCode": "CUS001346" }
```

---

## GET /api/Customers/cache
Returns all cached customers from SQLite. No params.

## GET /api/Customers/by-phone?phone=...
Returns customer(s) matching phone. 404 if none, single object if one, array if multiple.

## POST /api/Customers/sync
Queues a full customer sync from SAP. Returns 200 immediately (fire-and-forget).

---

## Order Sync
- Full sync: `POST /api/Orders/sync` (or Quartz job)
- Delta sync: Quartz `OrderDeltaSyncJob` — runs on schedule, calls `SyncOrdersDeltaAsync`
- Batch size: 200 orders per batch (default)
- Date range: from `SyncMetadata.LastSyncedAt` (Type="Order") to today

---

---

## SAP PostTransactionNotice — Event Matrix (Phase 0A+0B+0C, confirmed 2026-08-26)

**SBO_SP_PostTransactionNotice** fires post-commit in MOLAS_Live_2021 (SAP B1 PL18).

### ObjectType Registry (confirmed — Phase 0A through 0D)
| ObjectType | Document | Table | TransType observed | Notes |
|-----------|---------|-------|-------------------|----|
| 13 | A/R Invoice | OINV / INV1 | A | Invoice domain |
| 14 | A/R Credit Memo | ORIN / RIN1 | A | Physical (conditional on OINM) + Invoice domain |
| 15 | Delivery | ODLN / DLN1 | A only | No U or C on close — closure is silent |
| 16 | A/R Return | ORDN / RDN1 | A | Physical domain |
| 17 | Sales Order | ORDR / RDR1 | A, U, C | Commitment domain only — NOT BinInventory |
| 24 | Incoming Payment | ORCT | A, C | Payment domain — 24/C is NOT noise |
| 59 | Goods Receipt | OIGN / IGN1 | A only | 59/C: DI API -5006, never fires |
| 60 | Goods Issue | OIGE / IGE1 | A only | 60/C: DI API -5006, never fires |
| 67 | Stock Transfer | OWTR / WTR1 | A, C | 67/C fires on cancel; creates reversal OWTR; no 67/A for reversal |
| 10000011 | SAP internal (billing) | — | A | Companion to OINV, ORCT — EXCLUDE |
| 10000013 | SAP internal (trade) | — | A, U | Companion to ORDR, ODLN, ORDN — EXCLUDE |
| 321 | Reconciliation | OITR | A | Financial only — EXCLUDE |
| 410000000 | APLUS heartbeat | — | U | EXCLUDE |

### Fast-path SQL per included ObjectType
```sql
-- 15/A: Delivery — query DLN1
SELECT ItemCode, WhsCode, Quantity FROM DLN1 WHERE DocEntry = @DocEntry;

-- 16/A: A/R Return — query RDN1 + get closed ODLN
SELECT r.ItemCode, r.WhsCode, r.Quantity, o.BaseEntry AS ClosedOdlnDocEntry
FROM RDN1 r JOIN ORDN o ON o.DocEntry = r.DocEntry WHERE r.DocEntry = @DocEntry;

-- 59/A: OIGN — query IGN1
SELECT ItemCode, WhsCode, Quantity FROM IGN1 WHERE DocEntry = @DocEntry;

-- 60/A: OIGE — query IGE1
SELECT ItemCode, WhsCode, Quantity FROM IGE1 WHERE DocEntry = @DocEntry;

-- 67/A or 67/C: OWTR — query both warehouses (same query works for original on cancel)
SELECT o.FromWarehouse, t.ItemCode, t.WhsCode AS ToWarehouse, t.Quantity
FROM OWTR o JOIN WTR1 t ON t.DocEntry = o.DocEntry WHERE o.DocEntry = @DocEntry;
-- NOTE: 67/C fires for original DocEntry; SAP auto-creates reversal OWTR (no 67/A for it).
-- Handler just reads current OITW/OIBQ for both warehouses after either 67/A or 67/C.

-- 13/A: OINV from delivery — find base ODLN (no OITW refresh needed)
SELECT BaseEntry AS OdlnDocEntry FROM INV1
WHERE DocEntry = @DocEntry AND BaseType = 15 AND LineNum = 0;

-- 14/A: Credit Memo — check if CM moved physical stock (MUST check before routing)
-- Step 1: Get DocNum for the CM
SELECT DocNum FROM ORIN WHERE DocEntry = @DocEntry;
-- Step 2: Check OINM for T=14 movement
SELECT InQty, OutQty FROM OINM
WHERE TransType = 14 AND BASE_REF = @CmDocNum;
-- If rows found → refresh OITW/OIBQ for affected warehouse(s)
-- If no rows → invoice state update only, skip physical
-- Also get base invoice for invoice domain:
SELECT BaseType, BaseEntry FROM RIN1
WHERE DocEntry = @DocEntry AND LineNum = 0;
```

### OINM columns valid in this SAP version
`ItemCode, TransType, InQty, OutQty, BASE_REF, DocDate, TransNum`
`WhsCode` is INVALID. `BASE_REF` = DocNum (not DocEntry).

---

## ODOO → SapReplitAPI Field Mapping Notes
ODOO uses different field names than the canonical DTO fields:
| ODOO field | Canonical field | Behaviour |
|-----------|----------------|-----------|
| `city` | `Region` / `City` | Used as SAP region |
| `address1` | `Address` / `Address1` | Used as billing street |
| `viN1`/`viN2`/`viN3` | `VIN1`/`VIN2`/`VIN3` | JSON is case-insensitive by default |
| `salesPersonCode` | `SlpCode` | Mapped via `[JsonPropertyName]` |
| `firstName` + `lastName` | — | Not currently used (ignored) |
| `email` | — | Not currently used (ignored) |
| `country` | — | Not currently used (ignored) |
| `province` | — | Not currently used (ignored) |
