# Return Request Reconciliation — Staging Verification Plan

**Status:** DRAFT — a plan to execute in a staging environment, not yet run anywhere. Production deployment remains unauthorized (see `PRODUCTION_RULES.md`). `FullReconciliationEnabled` must stay `false` throughout.

## Pre-requisites

- A staging Windows host running the SapReplitAPI build from branch `claude/explore-project-w2myQ` (commit referenced in the Phase 4 delivery report).
- An isolated PostgreSQL instance for this verification — never the production Neon database. A local instance (as used for this phase's own testing) or a dedicated staging Postgres is acceptable; a disposable schema per test run is recommended.
- SAP connectivity: ideally a non-production SAP company. If none exists, controlled **read-only** verification against the real SAP company is acceptable (as already practiced throughout this project's investigation phases) — never mutate SAP data as part of this plan.

## 1. Isolated PostgreSQL

- [ ] Confirm the staging Postgres is not the production Neon cluster (check the hostname in the connection string before anything else).
- [ ] Run the full `SapReplitAPI.Tests` suite with `RETURNS_TEST_POSTGRES` pointed at it; confirm RR01–RR11, the cursor-persistence tests, the health-endpoint tests, and the orchestration tests (OR01–OR07) all pass, matching the Phase 4 delivery report's numbers.
- [ ] Confirm the generated-column (`ReturnableQty`) and FK (`InvoiceLines.DocEntry → Invoices.DocEntry`) behavior matches production's actual schema (spot-check against `C:\SAPAPI`'s real Neon schema definition, read-only).

## 2. Non-production SAP connection / read-only verification

- [ ] Confirm the staging app's effective `SAP:CompanyDB` (via the startup validation guard already in `Program.cs`, or by checking logs) resolves to the intended company — staging or production, explicitly confirmed, never ambiguous.
- [ ] If running against the real production SAP company for read-only verification: confirm no write operations execute. The reconciliation feature only ever *reads* SAP (`GetReturnRequestDocEntriesChangedSince`, `GetReturnRequestIdsForBackfill`, `GetReturnRequestSnapshotByDocEntry`) — verify this by reviewing the DI API call log for the verification window and confirming zero `Add()`/`Update()`/`Cancel()` calls originated from this feature's code paths.
- [ ] Re-run the delayed-ORIN-cancellation timestamp check (the `orincancelcheck`/`loginstanccheck` style read-only queries used during Phase 3/4 investigation) if a non-production SAP company with controllable test data becomes available — this is the one piece of SAP-side evidence this project has not yet been able to gather conclusively.

## 3. Quartz job execution

- [ ] Start the staging app with `ReturnReconciliation:Enabled=true`, `HistoricalRotationEnabled=true`, `FullReconciliationEnabled=false` (explicitly keep this one false).
- [ ] Confirm all three jobs register without error at startup (check for the `⚠️ [ReturnReconciliation] Invalid ... cron expression` warning — it should NOT appear if config is valid; confirm it DOES appear and falls back safely if you deliberately misconfigure a cron string, to re-verify the Phase 4 safety fix under staging's exact config loading).
- [ ] Watch `ReturnRequestReconciliationJob` fire on its configured interval and confirm log lines (`[RETURN-RECON] Run started...` / `Run complete...`).
- [ ] Watch `ReturnRequestHistoricalRotationJob` fire similarly (`[RETURN-HIST-RECON]` prefix).
- [ ] Confirm `[DisallowConcurrentExecution]` holds: manually trigger overlapping executions (e.g. via Quartz's own scheduler API) and confirm the second is skipped/queued, not run concurrently with itself.

## 4. SQLite cursor persistence

- [ ] After a few job cycles, inspect `ReturnReconciliationCursor` in the staging SQLite file directly — confirm `LastProcessedDocEntry` and `HistoricalCursorDocEntry` both advance as expected.
- [ ] Restart the staging Windows Service (a staging action, not production) and confirm the cursor values survive and the rotation continues from where it left off, not from zero.
- [ ] Confirm `HistoricalLapsCompleted` increments after a full historical rotation lap completes (estimate: with the default `HistoricalBatchSize=10` and `HistoricalRotationCron` every 15 minutes, a full lap over the current 216 ORRR documents takes `ceil(216/10) = 22` runs × 15 minutes ≈ **5.5 hours** — confirm this matches observed behavior in staging, adjusting the estimate if the real ORRR count has grown).

## 5. Health endpoint

- [ ] `GET /api/system/return-reconciliation-health` (with the correct `X-API-Key`) — confirm it reports `Enabled=true`, `HistoricalRotationEnabled=true`, `FullReconciliationEnabled=false`, and live `LastRunAtUtc`/`HistoricalLastRunAtUtc` timestamps that advance between calls.
- [ ] Confirm the response contains no credentials, connection strings, or API keys (already unit-tested; re-confirm against the real endpoint as a sanity check).
- [ ] Deliberately induce a failure (e.g. temporarily point SAP connectivity at an invalid value) and confirm `Status` reports `"Degraded"` or `"Unhealthy"` appropriately, then recovers to `"Healthy"` once connectivity is restored.

## 6. Error handling

- [ ] Confirm a single bad ORRR document (if one can be identified or simulated in staging) does not abort a batch — check logs for `[RETURN-RECON] Failed to refresh DocEntry=... — will retry on next run.` and confirm the SAME document is retried on the next run.
- [ ] Confirm the cursor does not advance past a batch that had any error (re-verify `ReadCursorAsync`/`ReadHistoricalCursorAsync` stay at the pre-batch value after an error).

## 7. Performance metrics

- [ ] Record actual per-document refresh latency in staging (compare against the ~3.5–16s range observed in this project's production log analysis) — confirms whether staging's SAP/Neon round-trip latency is representative.
- [ ] Record actual incremental-job and historical-rotation-job run durations against their `MaxExecutionSeconds`/`HistoricalMaxExecutionSeconds` budgets — confirm the defaults (240s / 120s) are comfortably sufficient for the configured `BatchSize`/`HistoricalBatchSize`, adjusting if staging's latency differs materially from production's.

## 8. Rollback

- [ ] Confirm rollback is purely configuration: setting `Enabled=false`, `HistoricalRotationEnabled=false` (and `FullReconciliationEnabled` was never turned on) returns both jobs to a no-op state with zero code changes needed.
- [ ] Confirm no data cleanup is required on rollback — the `ReturnReconciliationCursor` table is harmless to leave in place (purely operational bookkeeping, not user-facing data) and the jobs only ever recompute idempotently.
- [ ] Document the git commit hash and config values in place at the moment staging verification is considered complete, for an unambiguous reference point before any future production authorization request.

---

*This plan does not authorize production deployment on its own completion — it is the evidence a separate production authorization request would be built on, per `PRODUCTION_RULES.md` Rule 7.*
