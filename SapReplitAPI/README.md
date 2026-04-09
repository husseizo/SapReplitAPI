# SapReplitAPI

A .NET 8 Web API that acts as a middleware layer between **SAP Business One** (via COM interop) and external clients. It maintains a local SQLite cache of SAP data (products, orders, invoices, customers) and optionally mirrors that cache to a PostgreSQL (Neon) database. Background Quartz jobs keep the cache continuously in sync.

---

## Table of Contents

- [Overview](#overview)
- [Tech Stack](#tech-stack)
- [Prerequisites](#prerequisites)
- [Configuration](#configuration)
- [Running the Application](#running-the-application)
- [API Endpoints](#api-endpoints)
- [Background Jobs](#background-jobs)
- [Project Structure](#project-structure)
- [Known Issues & Roadmap](#known-issues--roadmap)

---

## Overview

```
SAP Business One (COM)
        │
        ▼
  SapReplitAPI (.NET 8)
  ┌─────────────────────────────────┐
  │  Controllers  ←── HTTP clients  │
  │  Services                       │
  │  Quartz Jobs (sync)             │
  │       │                         │
  │  SQLite Cache (local)           │
  │       │ (optional mirror)       │
  │  Neon PostgreSQL (cloud)        │
  └─────────────────────────────────┘
```

- Runs on **port 5050** (Kestrel) and is exposed via ngrok tunnel.
- Can also run as a **Windows Service**.
- All SAP reads go through `SapService` (COM interop); results are persisted to SQLite.
- The Neon mirror is optional — the app starts and functions fully without it.

---

## Tech Stack

| Component         | Technology                          |
|-------------------|-------------------------------------|
| Framework         | ASP.NET Core 8.0                    |
| SAP Integration   | SAPbobsCOM (COM Interop, Windows)   |
| Primary Cache     | SQLite via EF Core 9 + Microsoft.Data.Sqlite |
| Cloud Mirror      | PostgreSQL on Neon via Npgsql       |
| Job Scheduler     | Quartz.NET 3.14                     |
| Logging           | Serilog (Console + rolling File)    |
| API Docs          | Swagger / Swashbuckle               |
| Tunnel            | ngrok                               |

---

## Prerequisites

- Windows (required for SAP COM interop)
- SAP Business One client installed on the same machine (provides `SAPbobsCOM`)
- .NET 8 SDK
- SAP B1 license server accessible

---

## Configuration

All sensitive values must be set as **environment variables** on the host machine. Do **not** put credentials in `appsettings.json`.

### SAP Credentials

```
SAP__Server=<hostname>
SAP__CompanyDB=<database name>
SAP__UserName=<sap username>
SAP__Password=<sap password>
SAP__LicenseServer=<host:30000>
SAP__SLDServer=<host:40000>
```

### Connection Strings (appsettings.json or env vars)

```json
"ConnectionStrings": {
  "CacheDB": "Data Source=F:\\AutohubCaches\\productcache.db;Cache=Shared;Mode=ReadWriteCreate;",
  "NeonDb": "Host=...;Database=...;Username=...;Password=...;SSL Mode=Require;"
}
```

> `NeonDb` is optional. If absent, the Neon mirror and `NeonSyncJob` are automatically disabled.

### ngrok (optional tunnel)

Edit `ngrok.yml` with your authtoken and desired hostname, then run `ngrok.exe start sapmiddleware`.

---

## Running the Application

### Development

```bash
dotnet run
```

### As a Windows Service

```bash
sc create SapReplitAPI binPath="C:\path\to\SapReplitAPI.exe"
sc start SapReplitAPI
```

### Logs

Logs are written to `C:\SAPLogs\app.log` (rolling daily, 14-day retention). Per-job log files are also created in the same folder.

---

## API Endpoints

See [API_REFERENCE.md](API_REFERENCE.md) for full documentation.

| Controller        | Base Route              | Description                          |
|-------------------|-------------------------|--------------------------------------|
| Products          | `/api/products`         | Cached product lookup and sync       |
| Customers         | `/api/customers`        | Customer creation, cache, phone lookup |
| Orders            | `/api/orders`           | Order/quotation creation, line query |
| Payments          | `/api/payments`         | Invoice headers, lines, payments     |
| Dashboard         | `/api/dashboard`        | Sales KPIs and sales range queries   |
| TodayOrders       | `/api/today-orders`     | Today's order headers and lines      |
| OpenOrders        | `/api/open-orders`      | Open order headers and lines         |
| Users             | `/api/users`            | User CRUD                            |

Swagger UI is available at `/swagger` when running in Development mode.

---

## Background Jobs

See [SYNC_JOBS.md](SYNC_JOBS.md) for details on each job.

| Job                   | Interval  | Type        |
|-----------------------|-----------|-------------|
| InvoiceDeltaSyncJob   | 1 min     | Delta       |
| SyncTodayOrdersJob    | 3 min     | Full/Today  |
| OrderDeltaSyncJob     | 5 min     | Delta       |
| SyncOpenOrdersJob     | 5 min     | Full        |
| NeonSyncJob           | 5 min     | Mirror      |
| CustomerFullSyncJob   | 6 min     | Full        |
| ProductDeltaSyncJob   | 70 min    | Delta       |
| OrderFullSyncJob      | 5 hrs     | Full        |
| InvoiceFullSyncJob    | 12 hrs    | Full        |
| ProductFullSyncJob    | Daily 2AM | Full        |

---

## Project Structure

See [ARCHITECTURE.md](ARCHITECTURE.md) for a detailed breakdown.

```
SapReplitAPI/
├── Controllers/         # HTTP API controllers
├── DTOs/                # Data transfer objects
├── Jobs/                # Quartz background sync jobs
├── Mappers/             # Object mapping helpers
├── Migrations/          # EF Core SQLite migrations
├── Models/              # Domain models (Cache, Auth, Orders, etc.)
├── Services/
│   ├── Cached Services/ # EF-based cache read/write services
│   ├── Neon/            # NeonDbContext (PostgreSQL mirror)
│   └── Queue/           # Background task queue (IBackgroundTaskQueue)
├── Program.cs           # App entry point, DI, Quartz config
├── QuartzExtensions.cs  # AddJobAndTrigger helper
└── appsettings.json     # Non-secret configuration
```

---

## Known Issues & Roadmap

- **No authentication** — all endpoints are currently public. JWT or API-key auth needs to be added.
- **Plaintext passwords in User table** — passwords should be hashed with BCrypt before storage.
- **Duplicate job scheduling** — `InvoiceStatusCacheJob` and `CacheInvoiceStatusJob` both fire at 5 AM daily; one should be removed.
- **CORS** — currently set to `AllowAll`; should be scoped to known origins in production.
- **Product paging** — `GetCachedProductsAsync` loads all rows into memory before paging; query-level paging needed.

See [CHANGELOG.md](CHANGELOG.md) for history of changes.
