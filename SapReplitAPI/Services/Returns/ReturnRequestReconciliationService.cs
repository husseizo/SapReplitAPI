using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Returns;

public sealed class ReturnRequestReconciliationReport
{
    public int CandidatesExamined { get; set; }
    public int CandidatesRefreshed { get; set; }
    public int InvoiceLinesRecalculated { get; set; }
    public int StaleQuantitiesCorrected { get; set; }
    public int DeferredToNextRun { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Scheduled safety net for Return Request (ORRR) to Neon/SQLite synchronization. This service
/// does NOT replace, modify, or duplicate the authoritative recompute logic — every candidate is
/// refreshed through the exact same, unmodified calls the primary event-driven path
/// (ReturnRequestEventHandler) already uses: SapService.GetReturnRequestSnapshotByDocEntry (via
/// IReturnRequestSapReader), ReturnRequestCacheService.RefreshSingleAsync,
/// NeonReturnRequestWriteService.UpsertReturnRequestAsync.
///
/// Three independent tiers, each with its own schedule, cursor, and cost profile:
///   1. RunIncrementalAsync  — lookback-window, timestamp-driven, hourly-scale.
///   2. RunHistoricalRotationAsync — bounded sweep through EVERY ORRR ever created, rotating via
///      its own persisted cursor, closing the gap the timestamp-driven tier cannot prove (a
///      delayed credit-memo cancellation that never bumps any reliable timestamp).
///   3. The pre-existing ReturnsMirrorBackfillService (full, table-locked) — unchanged, driven by
///      a separate job, kept as the final correctness backstop.
/// </summary>
public sealed class ReturnRequestReconciliationService(
    IReturnRequestSapReader sapReader,
    ReturnRequestCacheService cache,
    NeonReturnRequestWriteService neonWriter,
    NeonDbContext neonDb,
    CacheDbContext sqliteDb,
    ILogger<ReturnRequestReconciliationService> log)
{
    /// <summary>
    /// Pure batch-selection algorithm shared by both the incremental and the historical rotation
    /// tiers — no SAP, no database, independently unit-testable. Fixes a deterministic-starvation
    /// defect in the original design: a plain <c>candidates.Take(BatchSize)</c> always selects the
    /// SAME lowest DocEntries when the candidate set is larger than BatchSize and doesn't change
    /// between runs — candidates past the cut would be starved forever.
    ///
    /// Selects candidates strictly after <paramref name="cursor"/> first (continuing where the
    /// previous run left off), and if that doesn't fill the batch, wraps around to the start of
    /// the ordered list — guaranteeing every candidate in a given snapshot of the candidate set is
    /// reached within a bounded number of runs (ceil(candidateCount / batchSize)).
    ///
    /// <paramref name="newCursor"/> resets to 0 once a full lap completes (the selected batch's
    /// maximum equals the whole ordered list's maximum) so the next lap starts fresh. The caller
    /// can detect "a lap just completed" by checking <c>newCursor == 0 &amp;&amp; selected.Count > 0</c>.
    /// </summary>
    internal static List<int> SelectBatch(IReadOnlyList<int> candidates, int cursor, int batchSize, out int newCursor)
    {
        var ordered = candidates.Distinct().OrderBy(d => d).ToList();
        newCursor = cursor;
        if (ordered.Count == 0) return ordered;

        var batchSizeSafe = Math.Max(1, batchSize);
        var selected = ordered.Where(d => d > cursor).Take(batchSizeSafe).ToList();
        if (selected.Count < batchSizeSafe)
        {
            var remaining = batchSizeSafe - selected.Count;
            var selectedSet = new HashSet<int>(selected);
            var wrap = ordered.Where(d => d <= cursor && !selectedSet.Contains(d)).Take(remaining).ToList();
            selected.AddRange(wrap);
        }

        if (selected.Count > 0)
        {
            newCursor = selected.Max();
            if (newCursor == ordered.Max())
                newCursor = 0; // full lap complete — start the next lap from the beginning
        }

        return selected;
    }

    // ── Incremental-tier cursor (Id=1, "LastProcessedDocEntry") ───────────────────────────

    internal async Task<int> ReadCursorAsync(CancellationToken ct = default)
        => await ReadIntColumnAsync("LastProcessedDocEntry", ct);

    internal async Task WriteCursorAsync(int newCursor, CancellationToken ct = default)
        => await UpsertCursorRowAsync("LastProcessedDocEntry", newCursor, "UpdatedAtUtc", ct);

    // ── Historical-rotation-tier cursor (same row, separate columns) ──────────────────────

    internal async Task<int> ReadHistoricalCursorAsync(CancellationToken ct = default)
        => await ReadIntColumnAsync("HistoricalCursorDocEntry", ct);

    internal async Task WriteHistoricalCursorAsync(int newCursor, bool lapCompleted, CancellationToken ct = default)
    {
        await UpsertCursorRowAsync("HistoricalCursorDocEntry", newCursor, "HistoricalUpdatedAtUtc", ct);
        if (lapCompleted)
        {
            var conn = sqliteDb.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE ""ReturnReconciliationCursor"" SET ""HistoricalLapsCompleted"" = ""HistoricalLapsCompleted"" + 1 WHERE ""Id"" = 1";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Shared read helper, hardened against "missing or corrupted cursor state": a missing row,
    /// a NULL value, or a value SQLite happens to hold in a non-integer-parseable form (its type
    /// affinity is not strict) all fall back to 0 — the safe "restart the rotation" default —
    /// rather than throwing and taking down a whole reconciliation run over a bookkeeping value.
    /// </summary>
    private async Task<int> ReadIntColumnAsync(string column, CancellationToken ct)
    {
        try
        {
            var conn = sqliteDb.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT ""{column}"" FROM ""ReturnReconciliationCursor"" WHERE ""Id"" = 1";
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? 0 : Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[RETURN-RECON] Cursor column {Column} read failed/corrupted — defaulting to 0 (rotation restarts from the beginning; no candidate is permanently lost).", column);
            return 0;
        }
    }

    private async Task UpsertCursorRowAsync(string valueColumn, int value, string timestampColumn, CancellationToken ct)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO ""ReturnReconciliationCursor"" (""Id"", ""{valueColumn}"", ""{timestampColumn}"")
VALUES (1, $value, $now)
ON CONFLICT(""Id"") DO UPDATE SET ""{valueColumn}"" = $value, ""{timestampColumn}"" = $now";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "$value"; p1.Value = value; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "$now"; p2.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(p2);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Observability record for /api/system/return-reconciliation-health. Written on EVERY run
    /// (success or failure) regardless of whether the rotation cursor itself advances, so a run
    /// that errored out is visible via health/logs rather than silently indistinguishable from
    /// "never ran."
    /// </summary>
    private async Task WriteLastRunSummaryAsync(ReturnRequestReconciliationReport report, CancellationToken ct)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ""ReturnReconciliationCursor""
    (""Id"", ""LastProcessedDocEntry"", ""UpdatedAtUtc"", ""LastRunAtUtc"", ""LastRunCandidatesExamined"", ""LastRunRefreshed"", ""LastRunErrors"", ""LastRunDurationMs"")
VALUES (1, 0, $now, $now, $examined, $refreshed, $errors, $durationMs)
ON CONFLICT(""Id"") DO UPDATE SET
    ""LastRunAtUtc"" = $now,
    ""LastRunCandidatesExamined"" = $examined,
    ""LastRunRefreshed"" = $refreshed,
    ""LastRunErrors"" = $errors,
    ""LastRunDurationMs"" = $durationMs";
        var now = DateTime.UtcNow.ToString("O");
        void AddParam(string name, object value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); }
        AddParam("$now", now);
        AddParam("$examined", report.CandidatesExamined);
        AddParam("$refreshed", report.CandidatesRefreshed);
        AddParam("$errors", report.Errors.Count);
        AddParam("$durationMs", (long)report.Duration.TotalMilliseconds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteHistoricalLastRunSummaryAsync(ReturnRequestReconciliationReport report, CancellationToken ct)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ""ReturnReconciliationCursor""
    (""Id"", ""HistoricalCursorDocEntry"", ""HistoricalLastRunAtUtc"", ""HistoricalLastRunExamined"", ""HistoricalLastRunRefreshed"", ""HistoricalLastRunErrors"", ""HistoricalLastRunDurationMs"")
VALUES (1, 0, $now, $examined, $refreshed, $errors, $durationMs)
ON CONFLICT(""Id"") DO UPDATE SET
    ""HistoricalLastRunAtUtc"" = $now,
    ""HistoricalLastRunExamined"" = $examined,
    ""HistoricalLastRunRefreshed"" = $refreshed,
    ""HistoricalLastRunErrors"" = $errors,
    ""HistoricalLastRunDurationMs"" = $durationMs";
        var now = DateTime.UtcNow.ToString("O");
        void AddParam(string name, object value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); }
        AddParam("$now", now);
        AddParam("$examined", report.CandidatesExamined);
        AddParam("$refreshed", report.CandidatesRefreshed);
        AddParam("$errors", report.Errors.Count);
        AddParam("$durationMs", (long)report.Duration.TotalMilliseconds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record LastRunSummary(
        int LastProcessedDocEntry, DateTime? LastRunAtUtc, int? LastRunCandidatesExamined,
        int? LastRunRefreshed, int? LastRunErrors, long? LastRunDurationMs);

    public async Task<LastRunSummary?> GetLastRunSummaryAsync(CancellationToken ct = default)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""LastProcessedDocEntry"", ""LastRunAtUtc"", ""LastRunCandidatesExamined"", ""LastRunRefreshed"", ""LastRunErrors"", ""LastRunDurationMs"" FROM ""ReturnReconciliationCursor"" WHERE ""Id"" = 1";
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return new LastRunSummary(
            rdr.GetInt32(0),
            rdr.IsDBNull(1) ? null : DateTime.Parse(rdr.GetString(1)),
            rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
            rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
            rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            rdr.IsDBNull(5) ? null : rdr.GetInt64(5));
    }

    public sealed record HistoricalRunSummary(
        int HistoricalCursorDocEntry, DateTime? HistoricalLastRunAtUtc, int? HistoricalLastRunExamined,
        int? HistoricalLastRunRefreshed, int? HistoricalLastRunErrors, long? HistoricalLastRunDurationMs,
        int LapsCompleted);

    public async Task<HistoricalRunSummary?> GetHistoricalRunSummaryAsync(CancellationToken ct = default)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""HistoricalCursorDocEntry"", ""HistoricalLastRunAtUtc"", ""HistoricalLastRunExamined"", ""HistoricalLastRunRefreshed"", ""HistoricalLastRunErrors"", ""HistoricalLastRunDurationMs"", ""HistoricalLapsCompleted"" FROM ""ReturnReconciliationCursor"" WHERE ""Id"" = 1";
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return new HistoricalRunSummary(
            rdr.GetInt32(0),
            rdr.IsDBNull(1) ? null : DateTime.Parse(rdr.GetString(1)),
            rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
            rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
            rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            rdr.IsDBNull(5) ? null : rdr.GetInt64(5),
            rdr.GetInt32(6));
    }

    // ── Tier 1: incremental (timestamp-driven, lookback window) ───────────────────────────

    public Task<ReturnRequestReconciliationReport> RunIncrementalAsync(ReturnReconciliationOptions opts, CancellationToken ct = default)
    {
        var since = DateTime.Now.AddMinutes(-Math.Max(1, opts.LookbackMinutes));
        return RunTierAsync(
            tierName: "RETURN-RECON",
            discover: () => sapReader.GetChangedDocEntries(since),
            readCursor: ct2 => ReadCursorAsync(ct2),
            writeCursor: (newCursor, _, ct2) => WriteCursorAsync(newCursor, ct2),
            writeSummary: WriteLastRunSummaryAsync,
            batchSize: opts.BatchSize,
            maxExecutionSeconds: opts.MaxExecutionSeconds,
            extraStartLog: $"LookbackMinutes={opts.LookbackMinutes} SinceLocal={since}",
            ct: ct);
    }

    // ── Tier 2: historical rotation (bounded sweep through every ORRR ever created) ───────

    public Task<ReturnRequestReconciliationReport> RunHistoricalRotationAsync(ReturnReconciliationOptions opts, CancellationToken ct = default)
        => RunTierAsync(
            tierName: "RETURN-HIST-RECON",
            discover: () => sapReader.GetAllDocEntries(),
            readCursor: ct2 => ReadHistoricalCursorAsync(ct2),
            writeCursor: (newCursor, lapCompleted, ct2) => WriteHistoricalCursorAsync(newCursor, lapCompleted, ct2),
            writeSummary: WriteHistoricalLastRunSummaryAsync,
            batchSize: opts.HistoricalBatchSize,
            maxExecutionSeconds: opts.HistoricalMaxExecutionSeconds,
            extraStartLog: "sweeping all historical ORRR DocEntries",
            ct: ct);

    /// <summary>
    /// Shared orchestration for both tiers — identical batch/time-budget/error-isolation/cursor
    /// semantics, differing only in how candidates are discovered and where the cursor/summary
    /// are persisted. Extracted so RunIncrementalAsync and RunHistoricalRotationAsync cannot
    /// silently drift apart in behavior.
    /// </summary>
    private async Task<ReturnRequestReconciliationReport> RunTierAsync(
        string tierName,
        Func<List<int>> discover,
        Func<CancellationToken, Task<int>> readCursor,
        Func<int, bool, CancellationToken, Task> writeCursor,
        Func<ReturnRequestReconciliationReport, CancellationToken, Task> writeSummary,
        int batchSize,
        int maxExecutionSeconds,
        string extraStartLog,
        CancellationToken ct)
    {
        var report = new ReturnRequestReconciliationReport();
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(1, maxExecutionSeconds));

        log.LogInformation("[{Tier}] Run started. {Extra} BatchSize={BatchSize} MaxExecutionSeconds={MaxExecutionSeconds}",
            tierName, extraStartLog, batchSize, maxExecutionSeconds);

        try
        {
            var candidates = discover();
            report.CandidatesExamined = candidates.Count;

            var cursor = await readCursor(ct);
            var toProcess = SelectBatch(candidates, cursor, batchSize, out var newCursor);
            report.DeferredToNextRun = candidates.Count - toProcess.Count;
            log.LogDebug("[{Tier}] Batch selection: cursor={Cursor} -> newCursor={NewCursor} selected={Selected} of {Total}",
                tierName, cursor, newCursor, toProcess.Count, candidates.Count);

            bool runWasClean = true;

            foreach (var docEntry in toProcess)
            {
                ct.ThrowIfCancellationRequested();

                if (sw.Elapsed > budget)
                {
                    report.DeferredToNextRun += toProcess.Count - report.CandidatesRefreshed - report.Errors.Count;
                    log.LogWarning("[{Tier}] Time budget ({Budget}s) exceeded after {Refreshed} of {Total} candidates — deferring the rest to the next run.",
                        tierName, budget.TotalSeconds, report.CandidatesRefreshed, toProcess.Count);
                    runWasClean = false;
                    break;
                }

                try
                {
                    var corrected = await RefreshOneAsync(docEntry, ct);
                    report.CandidatesRefreshed++;
                    report.StaleQuantitiesCorrected += corrected.staleCorrected;
                    report.InvoiceLinesRecalculated += corrected.linesRecalculated;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.Errors.Add($"DocEntry={docEntry}: {ex.GetType().Name}: {ex.Message}");
                    log.LogError(ex, "[{Tier}] Failed to refresh DocEntry={DocEntry} — will retry on next run.", tierName, docEntry);
                    runWasClean = false;
                }
            }

            if (runWasClean && toProcess.Count > 0)
            {
                bool lapCompleted = newCursor == 0;
                await writeCursor(newCursor, lapCompleted, ct);
                if (lapCompleted)
                    log.LogInformation("[{Tier}] Full lap completed — every candidate in this snapshot has now been revisited at least once.", tierName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report.Errors.Add($"Candidate discovery failed: {ex.GetType().Name}: {ex.Message}");
            log.LogError(ex, "[{Tier}] Candidate discovery (SAP query) failed.", tierName);
        }

        sw.Stop();
        report.Duration = sw.Elapsed;

        log.LogInformation(
            "[{Tier}] Run complete. Examined={Examined} Refreshed={Refreshed} InvoiceLinesRecalculated={Lines} StaleCorrected={Stale} Deferred={Deferred} Errors={ErrorCount} Duration={DurationMs}ms",
            tierName, report.CandidatesExamined, report.CandidatesRefreshed, report.InvoiceLinesRecalculated,
            report.StaleQuantitiesCorrected, report.DeferredToNextRun, report.Errors.Count, sw.ElapsedMilliseconds);

        try { await writeSummary(report, ct); }
        catch (Exception ex) { log.LogWarning(ex, "[{Tier}] Failed to persist last-run health summary (non-fatal).", tierName); }

        return report;
    }

    /// <summary>
    /// Stable 64-bit key for Postgres session-level advisory locks, namespaced to this feature
    /// (ASCII "RR" = 0x5252 in the high bits) so it cannot collide with any other advisory lock
    /// usage introduced elsewhere in this codebase (none existed as of this change).
    /// </summary>
    private static long AdvisoryLockKey(int docEntry) => ((long)0x5252 << 32) | (uint)docEntry;

    /// <summary>
    /// Refreshes exactly one ORRR DocEntry through the unmodified existing call path. Guarded by
    /// a Postgres session-level advisory lock keyed on the DocEntry — this is purely additive
    /// coordination between THIS service's own two tiers (incremental vs. historical rotation),
    /// which did not exist as a race risk before this task (only one reconciliation tier existed
    /// previously). It does NOT, and cannot, coordinate with ReturnRequestEventHandler, which is
    /// a protected component this task does not modify — see the Phase 4 delivery report's
    /// concurrency assessment for the proposed (not implemented) deeper fix covering that path.
    /// </summary>
    private async Task<(int staleCorrected, int linesRecalculated)> RefreshOneAsync(int docEntry, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)neonDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var lockKey = AdvisoryLockKey(docEntry);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("@k", NpgsqlDbType.Bigint, lockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var snapshot = sapReader.GetSnapshot(docEntry);
            if (snapshot is null)
            {
                log.LogWarning("[RETURN-RECON] ORRR DocEntry={DocEntry} not found in SAP — skipping (deleted/renumbered?).", docEntry);
                return (0, 0);
            }

            var newInvoiceRefs = snapshot.Value.lines
                .Where(l => l.BaseType == 13 && l.BaseEntry > 0)
                .Select(l => (l.BaseEntry, l.BaseLine))
                .Distinct()
                .ToList();

            var affectedInvoiceEntries = await GetAffectedInvoiceDocEntriesAsync(docEntry, newInvoiceRefs, ct);
            var before = await ReadPendingReturnQtyAsync(affectedInvoiceEntries, ct);

            // Unmodified existing call path — identical to ReturnRequestEventHandler.
            await cache.RefreshSingleAsync(snapshot.Value.header, snapshot.Value.lines, ct);
            await neonWriter.UpsertReturnRequestAsync(snapshot.Value.header, snapshot.Value.lines, ct);

            var after = await ReadPendingReturnQtyAsync(affectedInvoiceEntries, ct);
            int staleCorrected = after.Count(kv => !before.TryGetValue(kv.Key, out var prev) || prev != kv.Value);

            return (staleCorrected, newInvoiceRefs.Select(r => r.BaseEntry).Distinct().Count());
        }
        finally
        {
            await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlockCmd.Parameters.AddWithValue("@k", NpgsqlDbType.Bigint, lockKey);
            await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None); // always attempt, even if ct was cancelled
        }
    }

    private async Task<HashSet<(int DocEntry, int LineNum)>> GetAffectedInvoiceDocEntriesAsync(
        int orrrDocEntry, List<(int BaseEntry, int BaseLine)> newRefs, CancellationToken ct)
    {
        var result = new HashSet<(int, int)>(newRefs.Select(r => (r.BaseEntry, r.BaseLine)));

        var conn = (NpgsqlConnection)neonDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            @"SELECT ""BaseEntry"", ""BaseLine"" FROM ""ReturnRequestLines"" WHERE ""DocEntry"" = @de AND ""BaseType"" = 13 AND ""BaseEntry"" > 0",
            conn);
        cmd.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, orrrDocEntry);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            result.Add((rdr.GetInt32(0), rdr.GetInt32(1)));

        return result;
    }

    private async Task<Dictionary<(int DocEntry, int LineNum), decimal>> ReadPendingReturnQtyAsync(
        IEnumerable<(int DocEntry, int LineNum)> keys, CancellationToken ct)
    {
        var result = new Dictionary<(int, int), decimal>();
        var keyList = keys.Distinct().ToList();
        if (keyList.Count == 0) return result;

        var conn = (NpgsqlConnection)neonDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        foreach (var docEntries in keyList.Select(k => k.DocEntry).Distinct().Chunk(300))
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT \"DocEntry\", \"LineNum\", \"PendingReturnQty\" FROM \"InvoiceLines\" WHERE \"DocEntry\" IN ({string.Join(",", docEntries)})",
                conn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                result[(rdr.GetInt32(0), rdr.GetInt32(1))] = Convert.ToDecimal(rdr.GetValue(2));
        }

        return result;
    }
}
