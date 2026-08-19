using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.SoDelivery;

namespace SapReplitAPI.Services.SoDelivery;

public class SoDeliveryDbService
{
    private readonly CacheDbContext _db;
    private readonly ILogger<SoDeliveryDbService> _log;

    public SoDeliveryDbService(CacheDbContext db, ILogger<SoDeliveryDbService> log)
    {
        _db  = db;
        _log = log;
    }

    // ── Runs ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks ALL runs for the given processing date and returns whether any COMPLETED or
    /// RUNNING run exists. Used by the run guard in SoDeliveryService — must check all runs,
    /// not just the latest, so a COMPLETED run is never masked by a later ABORTED run.
    /// Single query: returns both flags in one round-trip.
    /// </summary>
    public async Task<(bool HasCompleted, bool HasRunning)> GetRunGuardStatusAsync(DateTime processingDate)
    {
        var dateOnly = processingDate.Date;
        var statuses = await _db.SoDeliveryRuns
            .AsNoTracking()
            .Where(r => r.ProcessingDate == dateOnly)
            .Select(r => r.Status)
            .ToListAsync();
        return (
            statuses.Contains(RunStatus.Completed),
            statuses.Contains(RunStatus.Running));
    }

    /// <summary>
    /// Returns the most recent run for the given processing date, or null if none exists.
    /// Multiple runs per date are allowed (IsForced=true re-runs), so "most recent" is by Id DESC.
    /// NOT used for safety guards — use GetRunGuardStatusAsync for that.
    /// </summary>
    public async Task<SoDeliveryRun?> GetRunByDateAsync(DateTime processingDate)
    {
        var dateOnly = processingDate.Date;
        return await _db.SoDeliveryRuns
            .AsNoTracking()
            .Where(r => r.ProcessingDate == dateOnly)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Paginated run list, newest first. Optional filters: exact processing date and status string.
    /// Maximum pageSize enforced by the caller (controller caps at 100).
    /// Returns summary rows only — no Logs or Lines loaded.
    /// </summary>
    public async Task<(List<SoDeliveryRun> Items, int TotalCount)> GetRunsPagedAsync(
        int       page,
        int       pageSize,
        DateTime? date   = null,
        string?   status = null)
    {
        var q = _db.SoDeliveryRuns.AsNoTracking();

        if (date.HasValue)
            q = q.Where(r => r.ProcessingDate == date.Value.Date);

        if (!string.IsNullOrEmpty(status))
            q = q.Where(r => r.Status == status);

        int total = await q.CountAsync();
        var items = await q
            .OrderByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    /// <summary>Returns the most recent run across all dates, or null if the table is empty.</summary>
    public async Task<SoDeliveryRun?> GetLatestRunAsync()
    {
        return await _db.SoDeliveryRuns
            .AsNoTracking()
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns a run by Id with all Logs and their Lines eagerly loaded.
    /// Intended for PDF report generation — do not use for list endpoints.
    /// </summary>
    public async Task<SoDeliveryRun?> GetRunByIdAsync(int runId)
    {
        return await _db.SoDeliveryRuns
            .AsNoTracking()
            .Include(r => r.Logs)
                .ThenInclude(l => l.Lines)
            .FirstOrDefaultAsync(r => r.Id == runId);
    }

    public async Task<SoDeliveryRun> CreateRunAsync(
        DateTime processingDate,
        string   triggeredBy,
        bool     isForced)
    {
        var run = new SoDeliveryRun
        {
            ProcessingDate = processingDate.Date,
            StartTime      = DateTime.UtcNow,
            Status         = RunStatus.Running,
            TriggeredBy    = triggeredBy,
            IsForced       = isForced,
        };
        _db.SoDeliveryRuns.Add(run);
        await _db.SaveChangesAsync();
        _log.LogInformation("▶️ [SoDelivery] Run created — Id={Id} Date={Date} TriggeredBy={By} IsForced={F}",
            run.Id, run.ProcessingDate.ToString("yyyy-MM-dd"), triggeredBy, isForced);
        return run;
    }

    /// <summary>
    /// Persists changes to a SoDeliveryRun (status, counters, EndTime, PdfPath, ErrorMessage).
    /// Entity may be tracked or detached — both are handled.
    /// </summary>
    public async Task UpdateRunAsync(SoDeliveryRun run)
    {
        if (_db.Entry(run).State == EntityState.Detached)
            _db.SoDeliveryRuns.Update(run);
        await _db.SaveChangesAsync();
    }

    // ── SO Logs ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a new SoDeliveryLog and returns it with Id populated.
    /// Throws DbUpdateException if (RunId, SoDocEntry) unique constraint is violated.
    /// </summary>
    public async Task<SoDeliveryLog> CreateSoLogAsync(SoDeliveryLog log)
    {
        _db.SoDeliveryLogs.Add(log);
        await _db.SaveChangesAsync();
        return log;
    }

    /// <summary>
    /// Persists changes to an existing SoDeliveryLog (Status, DurationMs, DeliveryDocEntry, etc.).
    /// Entity may be tracked or detached — both are handled.
    /// </summary>
    public async Task UpdateSoLogAsync(SoDeliveryLog log)
    {
        if (_db.Entry(log).State == EntityState.Detached)
            _db.SoDeliveryLogs.Update(log);
        await _db.SaveChangesAsync();
    }

    /// <summary>Returns all logs for a run with their line logs, ordered by insertion order.</summary>
    public async Task<List<SoDeliveryLog>> GetLogsForRunAsync(int runId)
    {
        return await _db.SoDeliveryLogs
            .AsNoTracking()
            .Include(l => l.Lines)
            .Where(l => l.RunId == runId)
            .OrderBy(l => l.Id)
            .ToListAsync();
    }

    /// <summary>Returns FAILED and EXCEPTION logs for a run with their line logs.</summary>
    public async Task<List<SoDeliveryLog>> GetFailedLogsAsync(int runId)
    {
        return await _db.SoDeliveryLogs
            .AsNoTracking()
            .Include(l => l.Lines)
            .Where(l => l.RunId == runId &&
                       (l.Status == SoLogStatus.Failed || l.Status == SoLogStatus.Exception))
            .OrderBy(l => l.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Returns true if any run has successfully delivered this SO.
    /// Used for SKIPPED_ALREADY_PROCESSED idempotency guard.
    /// SAP remains the final source of truth — this is a fast local hint only.
    /// </summary>
    public async Task<bool> HasSuccessLogAsync(int soDocEntry)
    {
        return await _db.SoDeliveryLogs
            .AsNoTracking()
            .AnyAsync(l => l.SoDocEntry == soDocEntry && l.Status == SoLogStatus.Success);
    }

    // ── Line Logs ─────────────────────────────────────────────────────────────

    /// <summary>Batch-inserts line log records. Caller must set LogId on each entry before calling.</summary>
    public async Task AddLineLogsAsync(IEnumerable<SoDeliveryLineLog> lines)
    {
        _db.SoDeliveryLineLogs.AddRange(lines);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Updates BinAllocationsJson on a single line log. Used by the backfill endpoint to
    /// retroactively populate bin data from OIBD for runs processed before this feature existed.
    /// </summary>
    public async Task UpdateLineLogBinAllocJsonAsync(int lineLogId, string json)
    {
        var entity = await _db.SoDeliveryLineLogs.FindAsync(lineLogId);
        if (entity is null) return;
        entity.BinAllocationsJson = json;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns all success SO logs for a run with their line logs, where at least one line
    /// has a null BinAllocationsJson and the SO has a known delivery DocEntry.
    /// Used by the backfill endpoint.
    /// </summary>
    public async Task<List<SoDeliveryLog>> GetSuccessLogsForBackfillAsync(int runId)
    {
        return await _db.SoDeliveryLogs
            .Include(l => l.Lines)
            .Where(l => l.RunId == runId
                     && l.Status == SoLogStatus.Success
                     && l.DeliveryDocEntry.HasValue
                     && l.Lines.Any(ll => ll.BinAllocationsJson == null))
            .ToListAsync();
    }

    // ── Backlog Enrichment ────────────────────────────────────────────────────

    /// <summary>
    /// For each DocEntry in the list, returns the most recent log's ProcessedAt, Status, and ErrorMessage.
    /// Batched into two queries — no N+1. DocEntries with no history are absent from the result.
    /// </summary>
    public async Task<Dictionary<int, BacklogEnrichmentData>> GetBacklogEnrichmentsAsync(
        IEnumerable<int> docEntries)
    {
        var ids = docEntries.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, BacklogEnrichmentData>();

        // Step 1: highest log Id per SoDocEntry (SQLite autoincrement = insertion order = latest)
        var latestIds = await _db.SoDeliveryLogs
            .AsNoTracking()
            .Where(l => ids.Contains(l.SoDocEntry))
            .GroupBy(l => l.SoDocEntry)
            .Select(g => g.Max(l => l.Id))
            .ToListAsync();

        if (latestIds.Count == 0) return new Dictionary<int, BacklogEnrichmentData>();

        // Step 2: fetch those rows — one query, no N+1
        return await _db.SoDeliveryLogs
            .AsNoTracking()
            .Where(l => latestIds.Contains(l.Id))
            .Select(l => new { l.SoDocEntry, l.ProcessedAt, l.Status, l.ErrorMessage })
            .ToDictionaryAsync(
                l => l.SoDocEntry,
                l => new BacklogEnrichmentData(l.ProcessedAt, l.Status, l.ErrorMessage));
    }
}

/// <summary>Snapshot of the most recent processing attempt for a backlog SO.</summary>
public sealed record BacklogEnrichmentData(
    DateTime? LastAttemptDate,
    string?   LastAttemptStatus,
    string?   LastFailureReason);
