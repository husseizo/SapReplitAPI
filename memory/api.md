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
