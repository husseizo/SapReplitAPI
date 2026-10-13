# Contributing to SapReplitAPI

**Status:** DRAFT — pending management review and approval.

This document defines the mandatory development workflow for anyone changing SapReplitAPI: current developers, future developers, external contractors, and AI coding assistants. See `PRODUCTION_RULES.md` for the production operations policy this workflow exists to protect.

## Core principle

**Production is not a development or debugging environment.** Do not run Visual Studio Debug builds, `dotnet run`, or manually launched executables against the production SAP company (`MOLAS_Live_2021`) or on the production port (`5050`). Use a local/test SAP company, or coordinate a maintenance window, for anything exploratory.

## Development procedure

1. **Create a feature or fix branch.** Use the existing `claude/<topic>` or `feature/<topic>` naming convention already in this repo. Never commit directly to `master`.
2. **Document the intended change** — what's broken or missing, and why this is the right fix, before writing code.
3. **Identify affected SAP and middleware components.** For anything touching Zone Fulfillment, SAP connectivity, or allocation, name the exact files/tables (e.g. "`ZoneFulfillmentOrchestrationService`, reads `ZoneWarehousePriority`, writes `ORDR`/`RDR1`").
4. **Implement the change outside production** — against a local build, a non-production SAP company if one is available, or with the existing `SapReplitAPI.Tests` fakes (`FakeOfflineSapAdapter`, `OfflineTestDb`, etc.).
5. **Run relevant unit and integration tests** in `SapReplitAPI.Tests` (xUnit). Add new tests for new behavior — see Rule 5/6 areas in `PRODUCTION_RULES.md` and Part C4 of the technical enforcement checklist for what's currently under-tested.
6. **Review database and configuration impacts.** If the change touches `appsettings.json`, `appsettings.Production.json`, or any environment variable, state the **effective** value after ASP.NET Core's configuration precedence is applied — not just what the JSON file says. If it touches SQL Server tables (`MolasIntegration`) or SAP UDFs, confirm the columns exist in `MOLAS_Live_2021` specifically.
7. **Submit a Pull Request** (see requirements below).
8. **Obtain approval from the designated reviewer** — mandatory for any file matched by `.github/CODEOWNERS`.
9. **Merge only after required checks pass** (once CI is configured — see Part C of the technical enforcement checklist; this repo currently has no automated status checks).
10. **Deploy only through the approved production process** in `PRODUCTION_RULES.md` Rule 7 — build with VS MSBuild (`Release|Any CPU`, required because of the SAP DI API COM reference), publish via `FolderProfile5`, back up `C:\SAPAPI`, deploy, restart, verify.

## Pull Request requirements

Every PR that touches SAP integration, Zone Fulfillment, or production configuration must explain:

- **Why** the change is necessary (link the issue/incident if there is one).
- **Which files changed**, and which of those are CODEOWNERS-protected.
- **Which SAP tables or documents are affected** (e.g. `ORDR`, `OPKL`, `ODLN`, specific UDFs).
- **Expected business behavior** before and after the change.
- **Risks** — what could this break, and in which warehouse/zone/allocation mode.
- **Test evidence** — which tests were added or run, and their output.
- **Rollback procedure** — how to undo this if it causes a problem in production.

A PR that cannot answer these for a SAP/ZF-touching change should not be approved.

## What this repo's test project already covers (as of this writing)

`SapReplitAPI.Tests` has coverage for: Tiered allocation (`TieredAllocation/*`), offline fulfillment phases (`Offline/Phase_C`–`Phase_E`), pick-list event refresh/timestamps, invoice mirror freshness and drift, customer creation, and returns/credit-memo flows. It currently has **no** test for SAP configuration/connection validation (the `SAP:CompanyDB` startup guard) — see the technical enforcement checklist for the recommended addition.

---

*This document is a draft produced for management review following the 13 October 2026 P0 incident. It does not take effect as policy until approved.*
