# SapReplitAPI — Production Rules

**Status:** DRAFT — pending management review and approval. Not yet enforced as policy.
**Origin:** Written after the P0 Sales Order Creation incident of 13 October 2026 (wrong SAP CompanyDB connection, stray debug process on the production port). See `docs/INCIDENT_RESPONSE.md` for the incident record.
**Applies to:** current developers, future developers, external contractors, AI coding assistants, and anyone deploying or maintaining the Windows production server for this project.

No one modifies critical production behavior — SAP connectivity, order creation, allocation, pick-list/delivery reconciliation, or the production Windows Service — without following the procedure in `CONTRIBUTING.md`.

---

## Rule 1 — SAP Company Database Protection

SapReplitAPI in production must connect only to SAP CompanyDB **`MOLAS_Live_2021`**.

- The application fails startup if the effective `SAP:CompanyDB` value differs from `MOLAS_Live_2021`. This is enforced today in `SapReplitAPI/Program.cs` (the `AddOptions<SapSettings>()...Validate(...)` block, commit `cad661bc8017bdf9b4a05935ca307cff5e6aa3a3`).
- This validation must not be removed, weakened, or bypassed without explicit management approval. Any PR touching that block requires CODEOWNERS review (see Rule 7 and `.github/CODEOWNERS`).
- Before changing any SAP connection setting, check the **effective runtime value**, not just the JSON files. ASP.NET Core's configuration precedence for this app is:
  1. `appsettings.json`
  2. `appsettings.{Environment}.json`
  3. Environment variables (machine-level, then Windows-Service-level — a service-level `Environment` registry value under `HKLM:\SYSTEM\CurrentControlSet\Services\SapReplitAPI` overrides the machine-wide one for this service only)
  4. Command-line arguments
  Later wins. **Do not assume the JSON configuration is authoritative** — a machine-wide environment variable silently overrode it in the 13 October incident, and `appsettings.json`/`appsettings.Production.json` showed the correct value the entire time.
- The sibling `MolasLubes` application shares this host and its own `SAP__*`-prefixed environment variables. SapReplitAPI's SAP configuration must stay isolated from it — prefer the service-scoped `SapReplitAPI\Environment` registry override over editing any machine-wide variable that another app also reads.

## Rule 2 — Production Windows Service Protection

- The only authorized production runtime is the Windows Service **`SapReplitAPI`**.
- Production executable: **`C:\SAPAPI\SapReplitAPI.exe`**.
- Production port: **5050**.
- Do not run Visual Studio Debug builds, `dotnet run`, or any manually launched executable on port 5050. (This happened on 13 October: a Visual-Studio-launched Debug build bound port 5050 ahead of the real service, ran in the `Development` environment, and silently served real customer traffic for over two hours before being noticed.)
- Before any deployment, verify which process currently owns port 5050 and its executable path (`Get-NetTCPConnection -LocalPort 5050 -State Listen`, then `Get-CimInstance Win32_Process -Filter "ProcessId=<pid>"`). Confirm it is the Windows Service, not an ad-hoc process.
- Do not stop, restart, replace, or reconfigure the production service without authorization and a recorded reason (see Rule 7).

## Rule 3 — SAP Data Integrity

- Do not directly modify SAP Business One transactional tables.
- Sales Orders, Pick Lists, Deliveries, Invoices, and Payments are created or modified only through the SAP Business One DI API as already wired into this codebase, or through SAP's own approved business workflows — never by hand-written `UPDATE`/`DELETE` against `ORDR`, `RDR1`, `OPKL`, `PKL1`/`PKL2`, `ODLN`, `OINV`, `OITW`, or any other transactional table, without an explicitly approved, documented, SAP-supported procedure (as was done, with evidence and authorization, during prior incident repairs — see `memory/bugs.md`).
- Do not bypass SAP DI API validation by writing around it at the SQL level.

## Rule 4 — Idempotency Protection

- Never remove or weaken `RequestId`-based idempotency in `ZoneFulfillmentOrchestrationService` or related services.
- Never disable duplicate-order safeguards (the `FulfillmentOrchestration` state machine, `U_ReplitId` correlation on `ORDR`/`OPKL`/`ODLN`).
- Never reset orchestration states (`Failed`, `UnknownOutcome`, etc.) or replay an uncertain request without first confirming its actual outcome in SAP (check for an existing `ORDR` via `U_ReplitId` before any retry).
- A `Failed`-state `RequestId` is terminal by design — it returns a cached failure and will **not** re-run allocation on retry. A new business attempt for the same content requires a brand-new `RequestId`.
- Preserve the `UnknownOutcome` recovery path (`RecoverUnknownOutcomeAsync`) — it exists specifically to avoid duplicate SAP documents when an outcome is ambiguous.

## Rule 5 — Warehouse Allocation Protection

Do not change any of the following without documented impact analysis, regression tests, and approval:

- `ZoneFulfillment:AllocationMode` (`Legacy` vs `Tiered`)
- `ZoneWarehousePriority` / `OriginWarehousePriority` table contents
- `ZoneFulfillmentOptions.ZoneMembers` (Home/Fallback zone membership)
- Stock availability calculation (`SapService.GetOitwAvailable`, `max(0, OnHand - IsCommited)`)
- `TieredZoneAllocationEngine` / `ZoneAllocationEngine` (Legacy) allocation logic and tier ordering

A configuration or data change in this area was initially suspected as the 13 October root cause and disproven with direct query evidence before the real cause (wrong CompanyDB) was found. Treat allocation-data hypotheses as something to prove with a query, not assume.

## Rule 6 — Pick-List and Delivery Protection

Do not change pick-list quantity calculations, release behavior, delivery gates, or reconciliation logic without verifying, for a representative live order:

- Allocated quantity vs. requested quantity vs. unallocated/remaining quantity
- Picked quantity and delivered quantity
- Warehouse assignment on each fragment (`SoLineFragmentRecord.WhsCode`)
- Partial-fulfillment behavior (`AllocationFragment.HasShortage`)
- Cancellation and retry behavior through the ZF Admin Console actions (`REFRESH_STATE`, `REPLAN_RELEASED`, `RESUME_REPLAN`, `RETRY_DELIVERY`, `RETRY_INVOICE`)

Known pre-existing gap (see P1 investigation report): `ZoneFulfillmentPickListService.CreateOrGetPickListForWhsGroupAsync` builds pick-list line quantities from `SoLineQty`, not `AllocatedQty` — this is correct today only because strict (non-backorder) fulfillment never leaves a fragment partially allocated for a line that still proceeds to pick-list creation. Any future `AllowBackorder` work must address this explicitly before it ships.

## Rule 7 — Production Deployment Approval

Every production deployment needs:

1. A documented change request (what, why, affected components).
2. Code review (see `.github/CODEOWNERS` for files requiring mandatory review).
3. Passing relevant tests (`SapReplitAPI.Tests`).
4. A backup of the current deployment (`C:\SAPAPI` copied to a timestamped folder before overwrite — e.g. `C:\SAPAPI_backup_<yyyyMMdd_HHmmss>`).
5. A rollback procedure (restore from that backup, restart the service).
6. Deployment authorization from the designated approver.
7. Post-deployment verification (service `Running`, correct PID, port 5050 owned by that PID, `Hosting environment: Production`, a live `/plan` or equivalent read-only check agreeing with a direct SAP read).
8. A deployment record (what changed, who approved it, verification evidence — e.g. an incident/closure report like the one for 13 October).

Emergency deployments (active P0) may use an expedited approval process — a documented verbal/chat authorization in the moment is acceptable — but must still be recorded and reviewed afterward, exactly as the 13 October incident was.

## Rule 8 — Secrets and Credentials

Never commit:

- SAP credentials
- SQL Server passwords
- API secrets (e.g. `ZfAdmin:AdminApiKey`)
- Production connection strings containing credentials
- Registry exports containing secrets
- `.env`/environment files containing sensitive values

Production credentials are stored and accessed only through the approved mechanisms already in place: `appsettings.Production.json` for non-secret values, environment variables (machine- or service-scoped) for `SAP__UserName`/`SAP__Password` and similar, per the existing comment in `Program.cs`. Do not print full credential values in logs, chat, or incident reports — this investigation redacted passwords throughout even while confirming connection identity.

## Rule 9 — Logging and Monitoring

Critical operations must log enough to trace:

- `RequestId`
- SAP document identity (`DocEntry`/`DocNum`/`AbsEntry`)
- Allocation decisions (mode, tier, reason — as `[ZF-TIERED]` already does)
- Pick-list processing (`[ZF-PL]`)
- Delivery processing (`[ZF-AUTO]`)
- Failed synchronization events (`SapEventOutbox`, `NeonMirrorWork`)
- `UnknownOutcome` recovery attempts

Never log passwords, tokens, or other credentials. (The existing `[Payment config: AdvanceAccount=..., DefaultBranchId=...]` startup log line is fine — it logs config, not secrets; verify any new startup logging follows the same pattern.)

## Rule 10 — Incident Response

P0 incidents take priority over feature development. During an active incident:

- Preserve evidence (log files, in-flight state) before taking action.
- Establish the actual root cause with direct queries/evidence — do not act on an unverified hypothesis. (Three hypotheses were raised and disproven with evidence during the 13 October incident before the real cause was found; none were acted on prematurely.)
- Avoid speculative production changes.
- Use the smallest safe correction (a service-scoped environment override, not a rewrite).
- Protect existing SAP documents and queued/pending orders — check for in-flight transactions before stopping any process.
- Verify recovery with a direct comparison (SAP read vs. application read; real order DocEntry/DocNum sequence; duplicate check) before declaring an incident closed.
- Record the incident and the preventive action taken (this document, and the startup validation guard, are that preventive action for this incident).

---

*This document is a draft produced for management review following the 13 October 2026 incident. It does not take effect as policy until approved.*
