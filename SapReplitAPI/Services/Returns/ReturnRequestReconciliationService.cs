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
    ILogger<ReturnRequestReconciliationService> log)
{
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

            var toProcess = candidates.Take(Math.Max(1, opts.BatchSize)).ToList();
            report.DeferredToNextRun = candidates.Count - toProcess.Count;

            foreach (var docEntry in toProcess)
            {
                ct.ThrowIfCancellationRequested();

                if (sw.Elapsed > budget)
                {
                    report.DeferredToNextRun += toProcess.Count - report.CandidatesRefreshed - report.Errors.Count;
                    log.LogWarning(
                        "[RETURN-RECON] Time budget ({Budget}s) exceeded after {Refreshed} of {Total} candidates — deferring the rest to the next run.",
                        budget.TotalSeconds, report.CandidatesRefreshed, toProcess.Count);
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
                    // Per-candidate isolation: one bad document must not abort the batch, and does
                    // not advance any watermark — it simply remains a candidate on the next run
                    // because the lookback window is a sliding "now minus N minutes" comparison,
                    // not a checkpoint that could get stuck past a failed row.
                    report.Errors.Add($"DocEntry={docEntry}: {ex.GetType().Name}: {ex.Message}");
                    log.LogError(ex, "[RETURN-RECON] Failed to refresh DocEntry={DocEntry} — will retry on next run.", docEntry);
                }
            }
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
