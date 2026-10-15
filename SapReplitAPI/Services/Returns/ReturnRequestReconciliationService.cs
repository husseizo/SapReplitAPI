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
/// (ReturnRequestEventHandler) already uses: SapService.GetReturnRequestSnapshotByDocEntry,
/// ReturnRequestCacheService.RefreshSingleAsync, NeonReturnRequestWriteService.UpsertReturnRequestAsync.
///
/// This class only adds: candidate discovery (SapService.GetReturnRequestDocEntriesChangedSince),
/// bounding (batch size, time budget), per-candidate error isolation, and before/after metrics.
/// </summary>
public sealed class ReturnRequestReconciliationService(
    SapService sap,
    ReturnRequestCacheService cache,
    NeonReturnRequestWriteService neonWriter,
    NeonDbContext neonDb,
    CacheDbContext sqliteDb,
    ILogger<ReturnRequestReconciliationService> log)
{
    /// <summary>
    /// Pure batch-selection algorithm for the incremental reconciliation job — no SAP, no
    /// database, independently unit-testable. Fixes a deterministic-starvation defect in the
    /// original design: a plain <c>candidates.Take(BatchSize)</c> always selects the SAME lowest
    /// DocEntries when the candidate set is larger than BatchSize and doesn't change between
    /// runs (true whenever LookbackMinutes keeps a candidate visible across multiple runs, which
    /// is required for the lookback-window design to work at all) — candidates past the cut
    /// would be starved forever, never processed, until they age out of the window.
    ///
    /// This selects candidates strictly after <paramref name="cursor"/> first (continuing where
    /// the previous run left off), and if that doesn't fill the batch, wraps around to the start
    /// of the ordered list — guaranteeing every candidate in a given snapshot of the candidate
    /// set is reached within a bounded number of runs (ceil(candidateCount / batchSize)),
    /// regardless of how large the set grows, without needing the batch size itself to grow.
    ///
    /// <paramref name="newCursor"/> resets to 0 once a full lap completes (the selected batch's
    /// maximum equals the whole ordered list's maximum) so the next lap starts fresh rather than
    /// the cursor climbing toward ever-larger, eventually-irrelevant values.
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

    private async Task<int> ReadCursorAsync(CancellationToken ct)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""LastProcessedDocEntry"" FROM ""ReturnReconciliationCursor"" WHERE ""Id"" = 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private async Task WriteCursorAsync(int newCursor, CancellationToken ct)
    {
        var conn = sqliteDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ""ReturnReconciliationCursor"" (""Id"", ""LastProcessedDocEntry"", ""UpdatedAtUtc"")
VALUES (1, $cursor, $now)
ON CONFLICT(""Id"") DO UPDATE SET ""LastProcessedDocEntry"" = $cursor, ""UpdatedAtUtc"" = $now";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "$cursor"; p1.Value = newCursor; cmd.Parameters.Add(p1);
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

    public async Task<ReturnRequestReconciliationReport> RunIncrementalAsync(
        ReturnReconciliationOptions opts, CancellationToken ct = default)
    {
        var report = new ReturnRequestReconciliationReport();
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(1, opts.MaxExecutionSeconds));
        var since = DateTime.Now.AddMinutes(-Math.Max(1, opts.LookbackMinutes));

        log.LogInformation(
            "[RETURN-RECON] Incremental run started. LookbackMinutes={LookbackMinutes} SinceLocal={Since} BatchSize={BatchSize} MaxExecutionSeconds={MaxExecutionSeconds}",
            opts.LookbackMinutes, since, opts.BatchSize, opts.MaxExecutionSeconds);

        try
        {
            var candidates = sap.GetReturnRequestDocEntriesChangedSince(since);
            report.CandidatesExamined = candidates.Count;

            var cursor = await ReadCursorAsync(ct);
            var toProcess = SelectBatch(candidates, cursor, opts.BatchSize, out var newCursor);
            report.DeferredToNextRun = candidates.Count - toProcess.Count;
            log.LogDebug("[RETURN-RECON] Batch selection: cursor={Cursor} -> newCursor={NewCursor} selected={Selected} of {Total}",
                cursor, newCursor, toProcess.Count, candidates.Count);

            // Cursor only advances if this entire selected batch completes cleanly (no time-budget
            // cutoff, no per-candidate error). This is deliberately conservative: "a failed or
            // interrupted run must be safely recoverable" is satisfied by never letting the
            // rotation pointer move past a candidate this run could not confirm — the cost is
            // some redundant (but idempotent, harmless) reprocessing of already-succeeded
            // candidates in the SAME batch on the next run, not a skipped candidate.
            bool runWasClean = true;

            foreach (var docEntry in toProcess)
            {
                ct.ThrowIfCancellationRequested();

                if (sw.Elapsed > budget)
                {
                    report.DeferredToNextRun += toProcess.Count - report.CandidatesRefreshed - report.Errors.Count;
                    log.LogWarning(
                        "[RETURN-RECON] Time budget ({Budget}s) exceeded after {Refreshed} of {Total} candidates — deferring the rest to the next run.",
                        budget.TotalSeconds, report.CandidatesRefreshed, toProcess.Count);
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
                    // Per-candidate isolation: one bad document must not abort the batch — keep
                    // attempting the rest — but the cursor will not advance past this batch (see
                    // runWasClean above), so this candidate is retried on the next run too.
                    report.Errors.Add($"DocEntry={docEntry}: {ex.GetType().Name}: {ex.Message}");
                    log.LogError(ex, "[RETURN-RECON] Failed to refresh DocEntry={DocEntry} — will retry on next run.", docEntry);
                    runWasClean = false;
                }
            }

            if (runWasClean && toProcess.Count > 0)
                await WriteCursorAsync(newCursor, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report.Errors.Add($"Candidate discovery failed: {ex.GetType().Name}: {ex.Message}");
            log.LogError(ex, "[RETURN-RECON] Candidate discovery (SAP query) failed.");
        }

        sw.Stop();
        report.Duration = sw.Elapsed;

        log.LogInformation(
            "[RETURN-RECON] Incremental run complete. Examined={Examined} Refreshed={Refreshed} InvoiceLinesRecalculated={Lines} StaleCorrected={Stale} Deferred={Deferred} Errors={ErrorCount} Duration={DurationMs}ms",
            report.CandidatesExamined, report.CandidatesRefreshed, report.InvoiceLinesRecalculated,
            report.StaleQuantitiesCorrected, report.DeferredToNextRun, report.Errors.Count, sw.ElapsedMilliseconds);

        try { await WriteLastRunSummaryAsync(report, ct); }
        catch (Exception ex) { log.LogWarning(ex, "[RETURN-RECON] Failed to persist last-run health summary (non-fatal)."); }

        return report;
    }

    private async Task<(int staleCorrected, int linesRecalculated)> RefreshOneAsync(int docEntry, CancellationToken ct)
    {
        var snapshot = sap.GetReturnRequestSnapshotByDocEntry(docEntry);
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
