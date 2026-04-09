# Deployment Guide

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows (required for SAP COM interop) |
| .NET Runtime | .NET 8 (included in single-file publish) |
| SAP B1 Client | Must be installed on the same machine |
| SAP License Server | Accessible from the deployment machine |
| Disk | `F:\AutohubCaches\` must exist for SQLite DB |
| Log folder | `C:\SAPLogs\` must exist (or adjust path in `appsettings.json`) |

---

## First-Time Setup

### 1. Create required folders

```powershell
New-Item -ItemType Directory -Force "F:\AutohubCaches"
New-Item -ItemType Directory -Force "C:\SAPLogs"
```

### 2. Set environment variables (host machine)

SAP credentials must be set as machine-level environment variables. Do **not** put them in `appsettings.json`.

```powershell
[System.Environment]::SetEnvironmentVariable("SAP__Server",         "WIN-GJGQ73V0C3K",          "Machine")
[System.Environment]::SetEnvironmentVariable("SAP__CompanyDB",      "MOLAS_Live_2021",           "Machine")
[System.Environment]::SetEnvironmentVariable("SAP__UserName",       "<sap_username>",            "Machine")
[System.Environment]::SetEnvironmentVariable("SAP__Password",       "<sap_password>",            "Machine")
[System.Environment]::SetEnvironmentVariable("SAP__LicenseServer",  "WIN-GJGQ73V0C3K:30000",     "Machine")
[System.Environment]::SetEnvironmentVariable("SAP__SLDServer",      "WIN-GJGQ73V0C3K:40000",     "Machine")
```

Restart the service/process after setting env vars.

### 3. Configure `appsettings.json`

Update the connection strings section:

```json
"ConnectionStrings": {
  "CacheDB": "Data Source=F:\\AutohubCaches\\productcache.db;Cache=Shared;Mode=ReadWriteCreate;",
  "NeonDb": "Host=<neon-host>;Database=<db>;Username=<user>;Password=<password>;SSL Mode=Require;Trust Server Certificate=true"
}
```

The `NeonDb` key is optional. Remove it or leave it empty to disable the Neon mirror.

---

## Build & Publish

From the project directory:

```powershell
dotnet publish -c Release -r win-x64 --no-self-contained -o C:\SapReplitAPI\publish
```

Or with single-file output:

```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -o C:\SapReplitAPI\publish
```

> Single-file publish is configured in `.csproj` by default (`PublishSingleFile=true`, `PublishTrimmed=false`).

---

## Running as a Windows Service

### Install

```powershell
sc.exe create SapReplitAPI `
  binPath= "C:\SapReplitAPI\publish\SapReplitAPI.exe" `
  DisplayName= "SAP Replit API" `
  start= auto
```

### Start / Stop

```powershell
sc.exe start SapReplitAPI
sc.exe stop SapReplitAPI
```

### Check status

```powershell
sc.exe query SapReplitAPI
```

### Uninstall

```powershell
sc.exe delete SapReplitAPI
```

---

## Running Manually (Development)

```powershell
cd SapReplitAPI
dotnet run
```

Or run the published exe directly:

```powershell
C:\SapReplitAPI\publish\SapReplitAPI.exe
```

The API listens on port **5050** (Kestrel). Internal Urls binding is also set to `http://localhost:7121` via `appsettings.json`.

---

## Database Migrations

EF Core migrations run **automatically at startup** (`db.Database.Migrate()` in `Program.cs`). No manual migration step is needed on deployment.

To add a new migration during development (requires Visual Studio Package Manager Console — `dotnet ef` CLI is blocked by the SAP COM reference):

```
PM> Add-Migration <MigrationName>
PM> Update-Database
```

---

## ngrok Tunnel (Optional)

To expose the API externally:

1. Edit `ngrok.yml` with your authtoken and desired hostname.
2. Run:
   ```powershell
   .\ngrok.exe start sapmiddleware
   ```

The tunnel will forward `https://accurate-ewe-bursting.ngrok-free.app` → `http://localhost:7121`.

> ⚠️ Never commit the authtoken to source control. Rotate it at [dashboard.ngrok.com](https://dashboard.ngrok.com) if exposed.

---

## Logs

| File | Description |
|---|---|
| `C:\SAPLogs\app.log` | Main application log (Information+), 14-day rolling |
| `C:\SAPLogs\order-full-sync.log` | `OrderFullSyncJob` verbose output |
| `C:\SAPLogs\order-delta-sync.log` | `OrderDeltaSyncJob` verbose output |

Log levels per source context are configured in `appsettings.json` under `Serilog.MinimumLevel.Override`.

---

## Health Check

After startup, verify the service is running:

```powershell
Invoke-WebRequest http://localhost:5050/swagger -UseBasicParsing
```

Or check that Quartz jobs are registering by looking for the `📅 Configuring Quartz jobs...` log line.

---

## Updating the Application

1. Pull latest code from `master`.
2. Publish the new build.
3. Stop the service → replace the binary → start the service.

```powershell
sc.exe stop SapReplitAPI
# copy new publish output to C:\SapReplitAPI\publish
sc.exe start SapReplitAPI
```

EF migrations will run automatically on start if there are pending migrations.
