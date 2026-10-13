# SapReplitAPI — Technical Enforcement Audit

**Status:** DRAFT — read-only findings and recommendations for management review. No repository administration settings or Windows permissions were changed to produce this document.

---

## C1 — Production Configuration Validation

**Existing control (verified):** `SapReplitAPI/Program.cs:96-104` now validates, at startup (`ValidateOnStart()`), that both SAP credentials are present and that `SAP:CompanyDB` equals `MOLAS_Live_2021` exactly (case-insensitive). If either check fails, the host fails to start with a descriptive exception instead of running silently misconfigured. Deployed in commit `cad661bc8017bdf9b4a05935ca307cff5e6aa3a3`.

**Recommended additional validations**, scoped so they only apply where they're meaningful and don't block legitimate local development:

- **Expected production environment check** — when `ASPNETCORE_ENVIRONMENT=Production`, also assert `SAP:Server` matches the known production SAP server (`WIN-GJGQ73V0C3K`) and that `ConnectionStrings:MolasIntegration` points at `localhost` (the production SQL Server). Skip this assertion entirely outside `Production` so local/dev runs against different hosts aren't blocked.
- **Deployment identity check** — log the running executable's path and the current process's effective `SAP:CompanyDB`/`ASPNETCORE_ENVIRONMENT` at `Information` level on every startup (not just on failure), so a `Get-Content` of the log file alone is enough to confirm what a running instance is actually connected to, without needing a diagnostic endpoint or external tool. This directly addresses the gap this investigation hit today: confirming the live service's effective configuration required building a side tool, because nothing in the app itself surfaced it.
- **Configuration consistency check** — at startup, log which configuration provider actually supplied `SAP:CompanyDB` (`appsettings.json` vs. an environment variable) using `IConfigurationRoot.Providers`/`GetChildKeys` introspection, so a future override is visible in the startup log instead of requiring registry inspection to discover.

**Explicitly avoid:** hard-coding a check that only allows startup when `ASPNETCORE_ENVIRONMENT=Production` and `SAP:CompanyDB=MOLAS_Live_2021` together — that would break local development and the test suite, which legitimately need different values outside production.

## C2 — Windows Server Access (read-only review; no changes made)

Findings as of this audit:

- **Service start/stop/reconfigure rights** (via `sc.exe sdshow SapReplitAPI`): limited to `NT AUTHORITY\SYSTEM`, `BUILTIN\Administrators`, and `BUILTIN\Server Operators`. Interactive/service logon accounts have query-only rights (cannot start, stop, or reconfigure). `Server Operators` currently has **no members**, so in practice this reduces to System + local Administrators.
- **Local Administrators group membership** (`net localgroup administrators`): a substantial list — **9 named individual accounts** (`Administrator`, `Ahmed`, `Ajabuelly`, `Alliyah`, `johanes`, `Joshua`, `Nuru`, `sapsupport`, `suleiman`) plus **4 domain-level groups** (`Domain Admins`, `Enterprise Admins`, `Exchange Trusted Subsystem`, `Organization Management`). Membership in any of those domain groups grants full local admin on this machine — meaning everyone in `Domain Admins`/`Enterprise Admins` org-wide can start/stop the production service, deploy to `C:\SAPAPI`, change its service-level environment variables, and launch arbitrary processes on port 5050. This is almost certainly broader than the set of people who actually need that access, and is a plausible contributing factor to how an unattended Visual Studio debug session ended up bound to the production port in the 13 October incident.
- **`C:\SAPAPI` NTFS permissions**: `BUILTIN\Users` (i.e. **any** authenticated local user, not just admins) has `AppendData` and `CreateFiles` rights, in addition to `ReadAndExecute`. This lets a non-admin account create new files inside the production deployment folder and append to existing ones. This is broader than necessary — deployment should require admin or a dedicated service account, not general authenticated-user write access.
- **Port 5050 firewall rule**: no dedicated inbound firewall rule scoped to port 5050 was found. Access control for that port currently relies entirely on host-level process/service control, not the firewall.

**Recommendations (least privilege), for administrator action — not applied here:**

1. Narrow who can start/stop/reconfigure `SapReplitAPI` and deploy to `C:\SAPAPI` to a small, named deployment group, rather than relying on broad domain-admin membership.
2. Remove `BUILTIN\Users`' `AppendData`/`CreateFiles` grant on `C:\SAPAPI`; restrict write access to Administrators and/or the specific account that runs deployments.
3. Add an explicit inbound firewall rule for port 5050 scoped to the expected client source (e.g. the frontend/API gateway), if one doesn't already exist elsewhere in the network layer.
4. Consider a scheduled/periodic check (even a simple one run manually before each deployment) that reports who is currently in local Administrators on this box, so membership drift is visible.

## C3 — Deployment Safety

Recommended automated pre/post-deployment checklist (as a PowerShell script, not yet written):

Pre-deployment:
- [ ] Build configuration is `Release|Any CPU` (the COM reference to the SAP DI API requires VS MSBuild, not `dotnet build`).
- [ ] Target environment confirmed (`ASPNETCORE_ENVIRONMENT` expected to be `Production` for the live service).
- [ ] `C:\SAPAPI` backed up to a timestamped folder before any file is overwritten.
- [ ] Current port-5050 owner identified and confirmed to be the `SapReplitAPI` Windows Service (not a stray process) before stopping it.

Post-deployment:
- [ ] Service status is `Running`.
- [ ] Port 5050 is owned by the service's own PID.
- [ ] Startup log shows `Hosting environment: Production` and no validation failures.
- [ ] Effective `SAP:CompanyDB` is `MOLAS_Live_2021` — confirmed by a direct SAP read (independent of the app) agreeing with a live read-only endpoint call (e.g. the `/plan` dry-run) for a known item/warehouse.
- [ ] No new `[ERR]` entries in the first few minutes of the log beyond known/expected ones.
- [ ] Rollback path (the pre-deployment backup) is confirmed present and restorable.

This is the exact sequence manually followed during the 13 October incident response; turning it into a script would make it repeatable and remove reliance on memory.

## C4 — Regression Protection

`SapReplitAPI.Tests` (xUnit) currently covers: Tiered allocation, offline fulfillment phases C–E, pick-list event refresh/timestamps, invoice mirror freshness/drift, customer creation, and returns/credit-memo flows. It does **not** currently cover:

| Recommended test | What it should assert |
|---|---|
| `SapSettingsValidationTests` (new) | Options validation rejects a `CompanyDB` other than `MOLAS_Live_2021`; accepts the correct value; rejects missing credentials. Exercises the same `.Validate()` chain added in `Program.cs`, extracted so it's testable without booting the full host. |
| Tiered allocation (existing, extend) | `TieredAllocation/*` already tests the engine/resolver in isolation — extend with a case using the exact production inputs from the 13 October incident (`VAG11387`, origin `001`, zone `Cluster-side`) as a permanent regression fixture. |
| Request idempotency (existing, extend) | Confirm a `Failed`-state `RequestId` returns the cached failure and does not re-invoke SAP `Add()` (this was verified by direct code reading during the incident; it should be a test, not just a reading). |
| SAP document recovery | `RecoverUnknownOutcomeAsync` — a test driving `UnknownOutcome` state through recovery with a fake SAP adapter, confirming no duplicate `ORDR` is created when one already exists for the `RequestId`. |
| Pick-list reconciliation | A test against the `OPKL`/`ORDR` UDF-dependent query in `GetPickListHeaderByAbsEntry`/`GetZfPickListValidationState`, using a fake/in-memory recordset that can simulate "column not found" to assert the failure mode is handled (currently it throws and is caught per-record by the reconciliation loop — worth asserting that explicitly rather than relying on it staying that way by accident). |
| Delivery reconciliation | A test confirming `DeliveryCreated` is logged/recorded exactly once per `RequestId` even though the current code logs it twice (see P1 investigation report) — to lock in that this is a double-log, not a double-document, and catch a future regression that turns it into an actual duplicate. |

---

## B5 — GitHub Branch Protection Recommendations

**Note:** the `gh` CLI was not available in this environment, so current branch protection settings on the GitHub repository could not be read for this audit. The recommendations below are standard GitHub features to be enabled by a repository administrator; no repository settings were changed.

For the default branch (`master`):

1. **Require a pull request before merging** — disable direct pushes to `master`, including for administrators if possible.
2. **Require approvals** — at least one approving review before merge; require re-approval on new commits.
3. **Require review from Code Owners** — so the proposed `.github/CODEOWNERS` file (once its reviewer mapping is confirmed by management) is actually enforced.
4. **Require status checks to pass before merging** — once CI is added (this repo currently has no workflow files under `.github/workflows/`); at minimum, a build + `SapReplitAPI.Tests` run.
5. **Require branches to be up to date before merging.**
6. **Restrict force pushes** to `master`.
7. **Restrict branch deletion** for `master` and any `release/*` branches.
8. **Restrict who can bypass these rules** — ideally no one, or a minimal named list, not "anyone with admin."
9. Where available on the plan, **use a GitHub Ruleset** instead of (or alongside) classic branch protection for finer-grained enforcement and better audit visibility.

These require a GitHub organization/repo administrator to apply — not something enforceable from within the codebase.

---

*This document is a draft produced for management review following the 13 October 2026 P0 incident. No production systems, repository settings, or Windows permissions were modified to produce it.*
