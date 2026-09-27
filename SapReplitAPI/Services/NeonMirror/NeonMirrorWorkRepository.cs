using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.NeonMirror;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SapReplitAPI.Services.NeonMirror;

/// <summary>
/// All access to the SQLite NeonMirrorWork durable queue.
///
/// EnqueueAsync is deliberately a static method taking the CALLER'S already-open
/// SqliteConnection/SqliteTransaction — it does not open its own connection or start
/// its own transaction. This is what makes the enqueue atomic with the cache write
/// that produced it: the caller (InventoryEventRefreshService.RunSqliteFullRefreshAsync)
/// passes in the exact same transaction it is about to commit. If that transaction
/// rolls back, the enqueue rolls back with it — there is no crash window where the
/// cache mutation commits but the mirror-work row does not exist, or vice versa.
///
/// Every other method here (claim/mark/reset/health) is worker-side and uses its own
/// CacheDbContext — those run on a separate, independent schedule from the cache write.
/// </summary>
public class NeonMirrorWorkRepository
{
    private readonly CacheDbContext _db;

    public NeonMirrorWorkRepository(CacheDbContext db) => _db = db;

    /// <summary>Protected no-arg ctor for test subclasses that need to override
    /// worker-side methods without a real DbContext. Not used by EnqueueAsync,
    /// which is static and needs no instance state.</summary>
    protected NeonMirrorWorkRepository() { _db = null!; }

    // ── Atomic enqueue (same-transaction as the cache write) ────────────────
    //
    // Coalescing: if a Pending/Retrying row already exists for (entityType, entityKey),
    // this is a no-op — the existing row will pick up current state when the worker
    // processes it (worker always rereads latest, never replays a stored payload), so
    // a second identical intent adds nothing. This is the mechanism that makes four
    // consecutive "BM10001 changed" signals collapse into one piece of durable work.
    public static async Task EnqueueAsync(
        SqliteConnection conn, SqliteTransaction tx,
        string entityType, string entityKey, Guid? sourceEventId,
        CancellationToken ct = default)
    {
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                SELECT COUNT(1) FROM NeonMirrorWork
                WHERE EntityType = $et AND EntityKey = $ek
                  AND Status IN ('Pending','Retrying');
                """;
            check.Parameters.AddWithValue("$et", entityType);
            check.Parameters.AddWithValue("$ek", entityKey);
            var existing = (long)(await check.ExecuteScalarAsync(ct) ?? 0L);
            if (existing > 0) return;
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO NeonMirrorWork
                (EntityType, EntityKey, Operation, Priority, SourceEventId, SourceVersion,
                 Status, AttemptCount, NextAttemptAtUtc, CreatedAtUtc, StartedAtUtc, CompletedAtUtc, LastError)
            VALUES
                ($et, $ek, 'UPSERT', 0, $sid, $sv,
                 'Pending', 0, NULL, $now, NULL, NULL, NULL);
            """;
        ins.Parameters.AddWithValue("$et", entityType);
        ins.Parameters.AddWithValue("$ek", entityKey);
        ins.Parameters.AddWithValue("$sid", (object?)sourceEventId?.ToString() ?? DBNull.Value);
        ins.Parameters.AddWithValue("$sv", DateTime.UtcNow.Ticks);
        ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marks the mirror row(s) for an entity Done immediately — called when
    /// the fast-path direct Neon push succeeds, so the worker doesn't needlessly
    /// reprocess something already fresh. No-op if none are Pending (already Done,
    /// or none were ever created — both harmless).</summary>
    public virtual async Task MarkDoneForEntityAsync(
        string entityType, string entityKey, CancellationToken ct = default)
    {
        var rows = await _db.NeonMirrorWork
            .Where(w => w.EntityType == entityType && w.EntityKey == entityKey
                     && (w.Status == NeonMirrorWorkStatus.Pending || w.Status == NeonMirrorWorkStatus.Retrying))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            r.Status = NeonMirrorWorkStatus.Done;
            r.CompletedAtUtc = DateTime.UtcNow;
        }
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);
    }

    // ── Worker-side claim ────────────────────────────────────────────────────
    // Single worker, no concurrent claimants — no locking hints needed (unlike
    // SapEventOutbox, which is claimed from a shared SQL Server table by a process
    // that could in principle run more than once; NeonMirrorWorker is one
    // BackgroundService instance per process, and only one process runs against this
    // SQLite file).
    public virtual async Task<List<NeonMirrorWork>> ClaimBatchAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var batch = await _db.NeonMirrorWork
            .Where(w => (w.Status == NeonMirrorWorkStatus.Pending || w.Status == NeonMirrorWorkStatus.Retrying)
                     && (w.NextAttemptAtUtc == null || w.NextAttemptAtUtc <= now))
            .OrderBy(w => w.Priority).ThenBy(w => w.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var w in batch)
        {
            w.Status = NeonMirrorWorkStatus.Processing;
            w.StartedAtUtc = now;
            w.AttemptCount += 1;
        }
        if (batch.Count > 0) await _db.SaveChangesAsync(ct);
        return batch;
    }

    public virtual async Task MarkDoneAsync(long id, CancellationToken ct = default)
    {
        var w = await _db.NeonMirrorWork.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return;
        w.Status = NeonMirrorWorkStatus.Done;
        w.CompletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // Exponential backoff: 5s,15s,30s,1m,2m,5m,10m,10m — 8 attempts before DeadLetter.
    // Cumulative wait before DeadLetter ≈ 5+15+30+60+120+300+600+600 = 1730s ≈ 29 min,
    // comfortably longer than the 10-minute outage the design must survive (NM_05).
    private static readonly int[] BackoffSeconds = { 5, 15, 30, 60, 120, 300, 600, 600 };
    public const int MaxAttemptsBeforeDeadLetter = 8;

    public virtual async Task MarkRetryingAsync(long id, string error, CancellationToken ct = default)
    {
        var w = await _db.NeonMirrorWork.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return;

        string truncated = error.Length > 4000 ? error[..4000] : error;
        w.LastError = truncated;

        if (w.AttemptCount >= MaxAttemptsBeforeDeadLetter)
        {
            w.Status = NeonMirrorWorkStatus.DeadLetter;
            w.CompletedAtUtc = DateTime.UtcNow;
        }
        else
        {
            w.Status = NeonMirrorWorkStatus.Retrying;
            int idx = Math.Clamp(w.AttemptCount - 1, 0, BackoffSeconds.Length - 1);
            w.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(BackoffSeconds[idx]);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Recovers rows stuck in Processing after a crash/restart — mirrors
    /// SapEventOutbox's ResetOrphanedAsync. Any row Processing for longer than
    /// staleMinutes returns to Retrying with an immediate NextAttemptAtUtc.</summary>
    public virtual async Task<int> ResetStaleProcessingAsync(int staleMinutes = 5, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-staleMinutes);
        var stale = await _db.NeonMirrorWork
            .Where(w => w.Status == NeonMirrorWorkStatus.Processing && w.StartedAtUtc != null && w.StartedAtUtc < cutoff)
            .ToListAsync(ct);
        foreach (var w in stale)
        {
            w.Status = NeonMirrorWorkStatus.Retrying;
            w.NextAttemptAtUtc = DateTime.UtcNow;
            w.LastError = (w.LastError ?? "") + $" | Orphaned Processing row reset at {DateTime.UtcNow:o}";
        }
        if (stale.Count > 0) await _db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public virtual async Task<NeonMirrorHealthSnapshot> QueryHealthAsync(CancellationToken ct = default)
    {
        var all = await _db.NeonMirrorWork.AsNoTracking().ToListAsync(ct);
        int pending = all.Count(w => w.Status == NeonMirrorWorkStatus.Pending);
        int processing = all.Count(w => w.Status == NeonMirrorWorkStatus.Processing);
        int retrying = all.Count(w => w.Status == NeonMirrorWorkStatus.Retrying);
        int done = all.Count(w => w.Status == NeonMirrorWorkStatus.Done);
        int deadLetter = all.Count(w => w.Status == NeonMirrorWorkStatus.DeadLetter);

        var oldestPending = all
            .Where(w => w.Status == NeonMirrorWorkStatus.Pending || w.Status == NeonMirrorWorkStatus.Retrying)
            .Select(w => w.CreatedAtUtc)
            .DefaultIfEmpty()
            .Min();
        long? oldestAgeSec = oldestPending == default
            ? null
            : (long)(DateTime.UtcNow - oldestPending).TotalSeconds;

        var lagSamples = all
            .Where(w => w.Status == NeonMirrorWorkStatus.Done && w.CompletedAtUtc != null)
            .Select(w => (long)(w.CompletedAtUtc!.Value - w.CreatedAtUtc).TotalMilliseconds)
            .OrderBy(x => x)
            .ToList();

        long? P(double pct) => lagSamples.Count == 0
            ? null
            : lagSamples[Math.Min(lagSamples.Count - 1, (int)Math.Ceiling(pct * lagSamples.Count) - 1 < 0 ? 0 : (int)Math.Ceiling(pct * lagSamples.Count) - 1)];

        return new NeonMirrorHealthSnapshot(
            Pending: pending, Processing: processing, Retrying: retrying,
            Done: done, DeadLetter: deadLetter,
            OldestPendingAgeSec: oldestAgeSec,
            MirrorLagP50Ms: P(0.50), MirrorLagP95Ms: P(0.95), MirrorLagP99Ms: P(0.99)
        );
    }
}

public sealed record NeonMirrorHealthSnapshot(
    int Pending, int Processing, int Retrying, int Done, int DeadLetter,
    long? OldestPendingAgeSec,
    long? MirrorLagP50Ms, long? MirrorLagP95Ms, long? MirrorLagP99Ms
);
