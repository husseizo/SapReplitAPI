# SapReplitAPI — Incident Response Procedure

**Status:** DRAFT — pending management review and approval.

## Priority levels

### P0 — Critical

Immediate escalation and service restoration required. Takes priority over all feature development.

Examples:
- Sales Order creation unavailable or widely failing.
- Wrong SAP CompanyDB connection (any SapReplitAPI process connected to a database other than `MOLAS_Live_2021`).
- Duplicate SAP document creation.
- Major production outage (service down, port 5050 not owned by the Windows Service).

### P1 — High

Prioritized investigation and corrective action required, but does not block all other work.

Examples:
- Pick-list reconciliation failures.
- Delivery processing failures.
- Significant synchronization errors affecting day-to-day operations (e.g. a recurring SQL/SAP error flooding the logs even if it isn't blocking order creation).

### P2 — Medium

Investigation and scheduled correction required; not urgent.

Examples:
- Historical failed outbox events (`SapEventOutbox`) that are not actively recurring.
- Non-critical synchronization inconsistencies.
- Recoverable background processing failures that retry successfully.

**Priority must be escalated when actual business impact warrants it** — a P2 issue that turns out to represent an active, business-critical failure (e.g. a sales order that silently never reached SAP) is escalated to P0/P1 immediately, not left at its original classification.

## Procedure

### 1. Report

State what's broken, since when, and the business impact (who/what is blocked) as soon as it's known. Include exact error text, affected RequestIds/DocEntries, and timestamps where available.

### 2. Preserve evidence

Before taking any corrective action:
- Do not restart the service, modify configuration, or touch SAP/SQL data until you've captured the current state (log excerpts, process/port ownership, in-flight requests).
- Check for in-flight SAP document creation before stopping any process — an interrupted `ORDR.Add()`/`ODLN.Add()` mid-call is worse than a short delay.

### 3. Establish root cause with evidence

- Form a hypothesis, then verify it with a direct, read-only query — never act on an unverified assumption.
- Record each hypothesis tested and its result, including disproven ones. (During the 13 October P0, three hypotheses — a missing warehouse-priority row, a stale SAP session, and a Legacy/Tiered mode mismatch — were each tested and disproven with direct evidence before the real cause, a stray `SAP__CompanyDB` environment variable, was found. That trail is part of the incident record, not noise — it rules out re-litigating the same wrong guesses in a future incident.)
- Prefer independent verification — e.g. a direct SAP query bypassing the application's own configuration loading — to confirm whether a discrepancy is in the data or in how the running process is configured/connected.

### 4. Authorization for production changes

- Any production mutation (service restart, config change, deployment, SAP/SQL write) requires explicit authorization, recorded in the incident thread, before it happens — except the smallest possible stabilizing action during an active P0 (e.g. stopping a confirmed-unintended process occupying the production port), which may proceed under expedited authorization and must be recorded afterward.
- State exactly what will change and why before doing it, and what evidence will confirm it worked.

### 5. Apply the smallest safe correction

- Fix the actual root cause, scoped as narrowly as possible. Prefer a configuration fix over a code change when the defect is in configuration; prefer a service-scoped override over a machine-wide one when other applications share the host.
- Do not bypass idempotency, stock allocation checks, or duplicate-order safeguards to "unblock" an incident faster.
- Do not deploy untested behavior changes as an emergency workaround.

### 6. Verify recovery

- Compare an independent, direct read (e.g. direct SAP query) against the application's own read for the same data, for at least one representative real case.
- Confirm real transactions succeed end-to-end where possible (e.g. actual orders reaching SAP with correct DocEntry/DocNum, no duplicates).
- Do not declare an incident closed because a *related* system now looks healthy — e.g. a successful Sales Order does not by itself prove pick-list/delivery reconciliation is healthy; verify each stage that was in scope.

### 7. Record and review

- Write a closure report: root cause, correction applied, verification evidence, remaining limitations, and the commit hash of any code change.
- Classify any remaining or newly-discovered issues at their own priority (P1/P2) rather than folding them into the closed incident.
- Feed any new governance/technical gap the incident revealed back into `PRODUCTION_RULES.md` and the technical enforcement checklist.

## Rollback

Every deployment made during incident response must have a recorded rollback path before it's applied:
- Config/environment change: record the prior value.
- Deployment: a timestamped backup of the previous `C:\SAPAPI` contents, restorable by stopping the service, restoring the backup, and restarting.
- SAP/SQL data change: the exact reverse statement, reviewed before the forward change is applied, not written after the fact.

---

*This document is a draft produced for management review following the 13 October 2026 P0 incident. It does not take effect as policy until approved.*
