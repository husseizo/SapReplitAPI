# API Reference

Base URL: `http://<host>:5050` (or via ngrok tunnel)

> **Note**: No authentication is currently enforced. All endpoints are publicly accessible.

---

## Products — `/api/products`

### `GET /api/products/cached`
Returns a paged list of all cached products from SQLite.

**Query parameters**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `page` | int | 1 | Page number (1-based) |
| `pageSize` | int | 2500 | Items per page |

**Response**
```json
{
  "TotalCount": 8420,
  "Page": 1,
  "PageSize": 2500,
  "Products": [ ... ]
}
```

---

### `GET /api/products/cached/by-itemcode/{itemCode}`
Look up a single product by item code.

**Route parameter**: `itemCode` (string)

**Query parameters**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `includeZero` | bool | false | Include items with zero stock |

---

## Customers — `/api/customers`

### `GET /api/customers/cache`
Returns all cached customers from SQLite.

---

### `GET /api/customers/by-phone?phone={phone}`
Find a customer by phone number.

**Query parameters**: `phone` (required)

**Response (single match)**
```json
{ "CardName": "ABC Trading", "Phone": "0501234567" }
```

**Response (multiple matches)**
```json
{ "Customers": [ ... ], "Count": 3 }
```

---

### `POST /api/customers`
Create a new customer in SAP Business One.

**Body**: `CreateCustomerDto`
```json
{
  "CardName": "New Customer",
  "Phone1": "0501234567",
  "GroupCode": 100
}
```

**Response**
```json
{ "Message": "Customer created successfully", "CardCode": "C10042" }
```

---

### `POST /api/customers/sync`
Trigger a manual full customer sync from SAP into the cache. Fire-and-forget (queued).

---

## Orders — `/api/orders`

### `POST /api/orders`
Create a sales order in SAP.

**Body**: `CreateOrderDto`

**Response**
```json
{ "Message": "Order created successfully", "DocEntry": 5421 }
```

---

### `POST /api/orders/quotations`
Create a sales quotation in SAP.

**Body**: `CreateOrderDto`

---

### `PUT /api/orders/{docEntry}`
Update an existing sales order in SAP.

**Route parameter**: `docEntry` (int)
**Body**: `UpdateOrderDto` (must include matching `DocEntry`)

---

### `GET /api/orders/lines/today`
Returns today's cached order lines from SQLite.

**Query parameters**

| Parameter | Type | Description |
|---|---|---|
| `query` | string | Search by DocEntry, description, ItemCode, Manufacturer |
| `keyword` | string | Alias for `query` |

---

## Payments — `/api/payments`

### `GET /api/payments/cached-invoice-headers`
Paged list of cached invoice headers (from 2024-01-01 onwards).

**Query parameters**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `page` | int | 1 | Page number |
| `pageSize` | int | 50 | Items per page (max 2000) |
| `paymentNumber` | int | — | Filter to invoices with this payment number |

**Response**: `PagedResult<CachedInvoice>`

---

### `GET /api/payments/cached-invoice-headers-slp`
Paged invoice headers filtered to a specific salesperson.

**Query parameters**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `slpCode` | int | yes | Salesperson code |
| `page` | int | — | Page number |
| `pageSize` | int | — | Items per page (max 2000) |

---

### `GET /api/payments/cached-invoice-lines`
Paged invoice lines, optionally filtered by document.

**Query parameters**

| Parameter | Type | Description |
|---|---|---|
| `docEntry` | int | Filter to a specific invoice |
| `page` | int | Page number |
| `pageSize` | int | Items per page (max 5000) |

---

### `GET /api/payments/cache-invoices`
Manually trigger an invoice sync, then return a page of results.

---

## Dashboard — `/api/dashboard`

All KPI endpoints accept an optional `?slpCode=` to scope results to a single salesperson.

### `GET /api/dashboard/month-to-date-sales`
Total collected sales (cash + credit) from the 1st of the current month to today.

**Query**: `?slpCode={int}` (optional)

---

### `GET /api/dashboard/sales-range`
Sales total for a custom date range.

**Query**: `startDate`, `endDate`, `slpCode` (optional)

---

### `GET /api/dashboard/sales-range-admin`
Admin: all-salesperson total for a date range.

**Query**: `startDate`, `endDate`

---

### `GET /api/dashboard/month-todate-cash-vs-credit`
Cash vs credit breakdown for the current month.

**Query**: `slpCode` (optional)

---

### `GET /api/dashboard/cash-vs-credit-range`
Cash vs credit breakdown for a date range.

**Query**: `startDate`, `endDate`, `slpCode` (optional)

---

### `GET /api/dashboard/cash-vs-credit-range-admin`
Admin: all-salesperson cash vs credit for a date range.

---

### `GET /api/dashboard/month-todate-average-order-value`
Average order value for the current month.

**Query**: `slpCode` (optional)

---

### `GET /api/dashboard/average-order-value-range`
Average order value for a date range.

**Query**: `slpCode`, `startDate`, `endDate`

---

### `GET /api/dashboard/month-todate-unpaid-orders`
Count and value of unpaid orders for the current month.

**Query**: `slpCode` (optional)

---

### `GET /api/dashboard/unpaid-orders-range`
Unpaid orders for a date range.

**Query**: `startDate`, `endDate`, `slpCode` (optional)

---

## Today's Orders — `/api/today-orders`

### `GET /api/today-orders/headers/today`
Today's order headers. Optionally filter by customer name, DocNum, SlpCode, or SlpName.

**Query**: `query` or `keyword` (search string)

**Response includes**: `DocEntry`, `DocNum`, `DocDate`, `CardName`, `SlpCode`, `SlpName`, `OrderValue`, `Cancelled`, plus a `totalOrderValue` (non-cancelled only).

---

### `GET /api/today-orders/{docEntry}/lines`
Order lines for a specific document.

**Route parameter**: `docEntry` (int)

---

## Open Orders — `/api/open-orders`

### `GET /api/open-orders/headers`
All open (undelivered) order headers.

**Query parameters**

| Parameter | Type | Description |
|---|---|---|
| `keyword` | string | Search CardName or DocEntry |
| `slpCode` | int | Filter by salesperson |

---

### `GET /api/open-orders/lines/{docEntry}`
Lines for a specific open order.

---

### `POST /api/open-orders/sync-manual`
Trigger a manual open orders sync from SAP. Fire-and-forget.

---

## Users — `/api/users`

> ⚠️ No authentication is applied. These endpoints expose all user records including passwords.

### `GET /api/users`
Returns all users.

### `GET /api/users/{id}`
Returns a single user by ID.

### `POST /api/users`
Create a new user.

**Body**: `User`
```json
{
  "Username": "john",
  "SlpCode": 7,
  "SlpName": "John Smith",
  "Password": "...",
  "Role": "Sales"
}
```

### `PUT /api/users/{id}`
Update a user. Body must include matching `Id`.

### `DELETE /api/users/{id}`
Delete a user.
