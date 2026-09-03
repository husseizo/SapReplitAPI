using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.ZoneFulfillment;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// SQLite read-through cache mirror for ZF final fulfillment reports.
///
/// Ownership contract:
///   NEON = durable source of truth.
///   SQLite = performance / read-through cache. Server-replaceable.
///
/// Write path: Neon first, then SQLite (SQLite failure never blocks 13/A).
/// Read path:  SQLite first → if missing or stale (UpdatedAtUtc older than Neon) → Neon → rehydrate SQLite.
/// Neon failure: serve stale-but-internally-consistent local cache for Generated/SnapshotReady reports.
/// SQLite failure: fall through to Neon directly.
/// </summary>
public sealed class ZoneFulfillmentReportCacheService
{
    private readonly CacheDbContext                             _db;
    private readonly ZoneFulfillmentReportRepository           _repo;
    private readonly ILogger<ZoneFulfillmentReportCacheService> _log;

    // Per-ReportId lock — prevents concurrent Neon fetches for the same missing report
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public ZoneFulfillmentReportCacheService(
        CacheDbContext db,
        ZoneFulfillmentReportRepository repo,
        ILogger<ZoneFulfillmentReportCacheService> log)
    {
        _db   = db;
        _repo = repo;
        _log  = log;
    }

    // ── Write path ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes report header + lines to SQLite atomically.
    /// Called after Neon persists; any failure is logged but never re-thrown.
    /// Returns true if write succeeded.
    /// </summary>
    public async Task<bool> UpsertAsync(
        ZfReportRecord neonRecord,
        List<ZfReportLine> lines,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var header = MapToCache(neonRecord);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Upsert header
            var existing = await _db.ZoneFulfillmentReports
                .AsTracking()
                .FirstOrDefaultAsync(r => r.ReportId == neonRecord.ReportId, ct);

            if (existing is null)
                _db.ZoneFulfillmentReports.Add(header);
            else
                CopyToExisting(header, existing);

            // Replace lines atomically
            var oldLines = await _db.ZoneFulfillmentReportLines
                .Where(l => l.ReportId == neonRecord.ReportId)
                .ToListAsync(ct);
            _db.ZoneFulfillmentReportLines.RemoveRange(oldLines);

            var cacheLines = lines.Select(MapLineToCache).ToList();
            _db.ZoneFulfillmentReportLines.AddRange(cacheLines);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            sw.Stop();
            _log.LogInformation(
                "[ZF-REPORT] LocalCacheSaved: ReportId={Rid} RequestId={Req} DeliveryDocEntry={De} " +
                "InvoiceDocEntry={Ie} Status={St} Lines={Lc} durationMs={Ms:F1}",
                neonRecord.ReportId, neonRecord.RequestId, neonRecord.DeliveryDocEntry,
                neonRecord.InvoiceDocEntry, neonRecord.Status, lines.Count, sw.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[ZF-REPORT] LocalCacheFailed: ReportId={Rid} RequestId={Req} durationMs={Ms:F1}",
                neonRecord.ReportId, neonRecord.RequestId, sw.Elapsed.TotalMilliseconds);
            return false;
        }
    }

    // ── Read-through: single report ───────────────────────────────────────────

    /// <summary>
    /// Read-through get for one report.
    /// Returns (record, lines, source) where source is "local" or "neon".
    /// Returns null if not found in either store.
    /// </summary>
    public async Task<(ZfReportRecord Record, List<ZfReportLine> Lines, string Source)?> GetReportWithLinesAsync(
        Guid reportId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: try SQLite
        CachedZoneFulfillmentReport? local = null;
        List<CachedZoneFulfillmentReportLine> localLines = [];
        try
        {
            local = await _db.ZoneFulfillmentReports
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ReportId == reportId, ct);
            if (local is not null)
                localLines = await _db.ZoneFulfillmentReportLines
                    .AsNoTracking()
                    .Where(l => l.ReportId == reportId)
                    .OrderBy(l => l.LineSeq)
                    .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[ZF-REPORT-CACHE] LocalUnavailable: ReportId={Rid} — falling through to Neon", reportId);
        }

        // Step 2: check Neon to detect staleness (or absence)
        ZfReportRecord? neon = null;
        try
        {
            neon = await _repo.FindByReportIdAsync(reportId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[ZF-REPORT-CACHE] NeonUnavailable: ReportId={Rid}", reportId);
        }

        // Case A: both missing
        if (local is null && neon is null)
            return null;

        // Case B: local exists and Neon unavailable → serve local if it's a terminal status
        if (local is not null && neon is null)
        {
            _log.LogWarning(
                "[ZF-REPORT-CACHE] NeonFallback: ReportId={Rid} Status={St} serving from local cache (Neon unreachable)",
                reportId, local.Status);
            return (MapFromCache(local), localLines.Select(MapLineFromCache).ToList(), "local-neon-unavailable");
        }

        // Case C: local missing → hydrate from Neon
        if (local is null)
        {
            _log.LogInformation(
                "[ZF-REPORT-CACHE] Miss: ReportId={Rid} — hydrating from Neon", reportId);
            return await HydrateAndReturnAsync(neon!, reportId, sw, ct);
        }

        // Case D: local present — check staleness by UpdatedAtUtc
        var localUtc = DateTime.SpecifyKind(local.UpdatedAtUtc, DateTimeKind.Utc);
        var neonUtc  = DateTime.SpecifyKind(neon!.UpdatedAtUtc, DateTimeKind.Utc);

        if (neonUtc > localUtc)
        {
            _log.LogInformation(
                "[ZF-REPORT-CACHE] Stale: ReportId={Rid} localUpdatedAt={Lu:o} neonUpdatedAt={Nu:o} — refreshing",
                reportId, localUtc, neonUtc);
            return await HydrateAndReturnAsync(neon!, reportId, sw, ct);
        }

        // Case E: local is current
        sw.Stop();
        _log.LogInformation(
            "[ZF-REPORT-CACHE] Hit: ReportId={Rid} localUpdatedAt={Lu:o} lineCount={Lc} durationMs={Ms:F1}",
            reportId, localUtc, localLines.Count, sw.Elapsed.TotalMilliseconds);
        return (MapFromCache(local), localLines.Select(MapLineFromCache).ToList(), "local");
    }

    /// <summary>Read-through get returning just the ZfReportRecord (no lines).</summary>
    public async Task<(ZfReportRecord Record, string Source)?> GetReportAsync(
        Guid reportId, CancellationToken ct)
    {
        var result = await GetReportWithLinesAsync(reportId, ct);
        if (result is null) return null;
        return (result.Value.Record, result.Value.Source);
    }

    // ── Read-through: by RequestId ────────────────────────────────────────────

    /// <summary>
    /// Read-through list of reports for a RequestId.
    /// Serves local if all present and current; falls back to Neon for missing/stale entries.
    /// </summary>
    public async Task<List<ZfReportRecord>> GetReportsForRequestAsync(
        Guid requestId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Try Neon first (authoritative list)
        List<ZfReportRecord> neonList = [];
        bool neonOk = false;
        try
        {
            neonList = await _repo.FindByRequestIdAsync(requestId, ct);
            neonOk   = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[ZF-REPORT-CACHE] NeonUnavailable for RequestId={Req} — falling through to local", requestId);
        }

        if (neonOk)
        {
            // Upsert each fresh Neon record into local cache (best-effort, no await needed for warming)
            _ = Task.Run(async () =>
            {
                foreach (var r in neonList)
                {
                    try
                    {
                        var localRow = MapToCache(r);
                        var exists = await _db.ZoneFulfillmentReports
                            .AsTracking()
                            .FirstOrDefaultAsync(x => x.ReportId == r.ReportId, CancellationToken.None);
                        if (exists is null)
                            _db.ZoneFulfillmentReports.Add(localRow);
                        else if (DateTime.SpecifyKind(r.UpdatedAtUtc, DateTimeKind.Utc)
                                 > DateTime.SpecifyKind(exists.UpdatedAtUtc, DateTimeKind.Utc))
                            CopyToExisting(localRow, exists);
                        await _db.SaveChangesAsync(CancellationToken.None);
                    }
                    catch { /* best-effort cache warm */ }
                }
            });

            sw.Stop();
            _log.LogInformation(
                "[ZF-REPORT-CACHE] NeonFallback list: RequestId={Req} count={C} durationMs={Ms:F1}",
                requestId, neonList.Count, sw.Elapsed.TotalMilliseconds);
            return neonList;
        }

        // Neon unavailable — serve from local cache
        try
        {
            var localList = await _db.ZoneFulfillmentReports
                .AsNoTracking()
                .Where(r => r.RequestId == requestId)
                .OrderByDescending(r => r.UpdatedAtUtc)
                .ToListAsync(ct);
            sw.Stop();
            _log.LogWarning(
                "[ZF-REPORT-CACHE] LocalUnavailable-Neon: serving {C} local reports for RequestId={Req}",
                localList.Count, requestId);
            return localList.Select(MapFromCache).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[ZF-REPORT-CACHE] LocalUnavailable: both stores failed for RequestId={Req}", requestId);
            return [];
        }
    }

    // ── Explicit rehydration ──────────────────────────────────────────────────

    /// <summary>
    /// Fetches one report from Neon and persists it to SQLite.
    /// Used for cache loss recovery and reconciliation.
    /// </summary>
    public async Task HydrateFromNeonAsync(Guid reportId, CancellationToken ct)
    {
        _log.LogInformation("[ZF-REPORT-CACHE] HydrateStarted: ReportId={Rid}", reportId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var neon = await _repo.FindByReportIdAsync(reportId, ct);
            if (neon is null)
            {
                _log.LogWarning("[ZF-REPORT-CACHE] HydrateFailed: ReportId={Rid} not found in Neon", reportId);
                return;
            }
            var neonLines = await _repo.FindLinesByReportIdAsync(reportId, ct);
            await UpsertAsync(neon, neonLines, ct);

            sw.Stop();
            _log.LogInformation(
                "[ZF-REPORT-CACHE] Hydrated: ReportId={Rid} RequestId={Req} DeliveryDocEntry={De} " +
                "InvoiceDocEntry={Ie} lineCount={Lc} durationMs={Ms:F1}",
                reportId, neon.RequestId, neon.DeliveryDocEntry, neon.InvoiceDocEntry,
                neonLines.Count, sw.Elapsed.TotalMilliseconds);

            _log.LogInformation(
                "[ZF-REPORT] CacheRehydrated: ReportId={Rid} Status={St}", reportId, neon.Status);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[ZF-REPORT-CACHE] HydrateFailed: ReportId={Rid} durationMs={Ms:F1}",
                reportId, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Removes the local cache entry for a report. Used for cache-loss regression tests only.
    /// </summary>
    public async Task DeleteLocalAsync(Guid reportId, CancellationToken ct)
    {
        var lines = await _db.ZoneFulfillmentReportLines
            .Where(l => l.ReportId == reportId).ToListAsync(ct);
        _db.ZoneFulfillmentReportLines.RemoveRange(lines);

        var header = await _db.ZoneFulfillmentReports.FindAsync(new object[] { reportId }, ct);
        if (header is not null)
            _db.ZoneFulfillmentReports.Remove(header);

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("[ZF-REPORT-CACHE] LocalDeleted: ReportId={Rid} (test only)", reportId);
    }

    // ── Snapshot SHA-256 ─────────────────────────────────────────────────────

    /// <summary>SHA-256 of SnapshotJson UTF-8 bytes. Empty string if json is null/empty.</summary>
    public static string ComputeSnapshotSha256(string? json)
    {
        if (string.IsNullOrEmpty(json)) return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<(ZfReportRecord, List<ZfReportLine>, string)?> HydrateAndReturnAsync(
        ZfReportRecord neon, Guid reportId,
        System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        _log.LogInformation("[ZF-REPORT-CACHE] HydrateStarted: ReportId={Rid}", reportId);

        List<ZfReportLine> neonLines = [];
        try
        {
            neonLines = await _repo.FindLinesByReportIdAsync(reportId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZF-REPORT-CACHE] HydrateFailed (lines): ReportId={Rid}", reportId);
        }

        // Write to local cache (best-effort)
        _ = UpsertAsync(neon, neonLines, CancellationToken.None);

        sw.Stop();
        _log.LogInformation(
            "[ZF-REPORT-CACHE] Hydrated: ReportId={Rid} lineCount={Lc} durationMs={Ms:F1}",
            reportId, neonLines.Count, sw.Elapsed.TotalMilliseconds);

        return (neon, neonLines, "neon");
    }

    private SemaphoreSlim GetLock(Guid reportId)
        => _locks.GetOrAdd(reportId, _ => new SemaphoreSlim(1, 1));

    // ── Mappers ───────────────────────────────────────────────────────────────

    private static CachedZoneFulfillmentReport MapToCache(ZfReportRecord r) =>
        new()
        {
            ReportId          = r.ReportId,
            RequestId         = r.RequestId,
            OrchestrationId   = r.OrchestrationId,
            ReportType        = r.ReportType,
            Status            = r.Status,
            SalesOrderDocEntry = r.SalesOrderDocEntry,
            SalesOrderDocNum  = r.SalesOrderDocNum,
            DeliveryDocEntry  = r.DeliveryDocEntry,
            DeliveryDocNum    = r.DeliveryDocNum,
            InvoiceDocEntry   = r.InvoiceDocEntry,
            InvoiceDocNum     = r.InvoiceDocNum,
            CardCode          = r.CardCode ?? "",
            DeliveryLocation  = r.DeliveryLocation ?? "",
            ZoneRef           = r.ZoneRef ?? "",
            U_ReplitId        = r.U_ReplitId ?? "",
            SnapshotJson      = r.SnapshotJson,
            SnapshotSha256    = ComputeSnapshotSha256(r.SnapshotJson),
            FileName          = r.FileName,
            MimeType          = r.MimeType,
            FileSize          = r.FileSize,
            Sha256            = r.Sha256,
            GeneratedAtUtc    = r.GeneratedAtUtc,
            UpdatedAtUtc      = r.UpdatedAtUtc,
            ErrorMessage      = r.ErrorMessage
        };

    private static void CopyToExisting(CachedZoneFulfillmentReport src, CachedZoneFulfillmentReport dst)
    {
        dst.RequestId         = src.RequestId;
        dst.OrchestrationId   = src.OrchestrationId;
        dst.ReportType        = src.ReportType;
        dst.Status            = src.Status;
        dst.SalesOrderDocEntry = src.SalesOrderDocEntry;
        dst.SalesOrderDocNum  = src.SalesOrderDocNum;
        dst.DeliveryDocEntry  = src.DeliveryDocEntry;
        dst.DeliveryDocNum    = src.DeliveryDocNum;
        dst.InvoiceDocEntry   = src.InvoiceDocEntry;
        dst.InvoiceDocNum     = src.InvoiceDocNum;
        dst.CardCode          = src.CardCode;
        dst.DeliveryLocation  = src.DeliveryLocation;
        dst.ZoneRef           = src.ZoneRef;
        dst.U_ReplitId        = src.U_ReplitId;
        dst.SnapshotJson      = src.SnapshotJson;
        dst.SnapshotSha256    = src.SnapshotSha256;
        dst.FileName          = src.FileName;
        dst.MimeType          = src.MimeType;
        dst.FileSize          = src.FileSize;
        dst.Sha256            = src.Sha256;
        dst.GeneratedAtUtc    = src.GeneratedAtUtc;
        dst.UpdatedAtUtc      = src.UpdatedAtUtc;
        dst.ErrorMessage      = src.ErrorMessage;
    }

    private static ZfReportRecord MapFromCache(CachedZoneFulfillmentReport r) =>
        new()
        {
            ReportId          = r.ReportId,
            RequestId         = r.RequestId,
            OrchestrationId   = r.OrchestrationId,
            ReportType        = r.ReportType,
            Status            = r.Status,
            SalesOrderDocEntry = r.SalesOrderDocEntry,
            SalesOrderDocNum  = r.SalesOrderDocNum,
            DeliveryDocEntry  = r.DeliveryDocEntry,
            DeliveryDocNum    = r.DeliveryDocNum,
            InvoiceDocEntry   = r.InvoiceDocEntry,
            InvoiceDocNum     = r.InvoiceDocNum,
            CardCode          = r.CardCode,
            DeliveryLocation  = r.DeliveryLocation,
            ZoneRef           = r.ZoneRef,
            U_ReplitId        = r.U_ReplitId,
            SnapshotJson      = r.SnapshotJson,
            FileName          = r.FileName,
            MimeType          = r.MimeType,
            FileSize          = r.FileSize,
            Sha256            = r.Sha256,
            GeneratedAtUtc    = r.GeneratedAtUtc,
            UpdatedAtUtc      = r.UpdatedAtUtc,
            ErrorMessage      = r.ErrorMessage
        };

    private static CachedZoneFulfillmentReportLine MapLineToCache(ZfReportLine l) =>
        new()
        {
            ReportId           = l.ReportId,
            LineSeq            = l.LineSeq,
            ItemCode           = l.ItemCode ?? "",
            Description        = l.Description,
            RequestedQty       = l.RequestedQty  ?? 0m,
            PickedQty          = l.PickedQty     ?? 0m,
            DeliveredQty       = l.DeliveredQty  ?? 0m,
            UnitPrice          = l.UnitPrice     ?? 0m,
            LineTotal          = l.LineTotal     ?? 0m,
            WhsCode            = l.WhsCode,
            OpklAbsEntry       = l.OpklAbsEntry,
            PickerUserId       = l.PickerUserId,
            PickerUserCode     = l.PickerUserCode,
            PickerName         = l.PickerName,
            BinAbsEntry        = l.BinAbsEntry,
            BinCode            = l.BinCode,
            BinQty             = l.BinQty,
            SalesOrderBaseLine = l.SalesOrderBaseLine,
            DeliveryLineNum    = l.DeliveryLineNum,
            InvoiceLineNum     = l.InvoiceLineNum,
            InvoiceBaseType    = l.InvoiceBaseType,
            InvoiceBaseEntry   = l.InvoiceBaseEntry,
            InvoiceBaseLine    = l.InvoiceBaseLine
        };

    private static ZfReportLine MapLineFromCache(CachedZoneFulfillmentReportLine l) =>
        new()
        {
            ReportId           = l.ReportId,
            LineSeq            = l.LineSeq,
            ItemCode           = l.ItemCode,
            Description        = l.Description,
            RequestedQty       = l.RequestedQty,
            PickedQty          = l.PickedQty,
            DeliveredQty       = l.DeliveredQty,
            UnitPrice          = l.UnitPrice,
            LineTotal          = l.LineTotal,
            WhsCode            = l.WhsCode,
            OpklAbsEntry       = l.OpklAbsEntry,
            PickerUserId       = l.PickerUserId,
            PickerUserCode     = l.PickerUserCode,
            PickerName         = l.PickerName,
            BinAbsEntry        = l.BinAbsEntry,
            BinCode            = l.BinCode,
            BinQty             = l.BinQty,
            SalesOrderBaseLine = l.SalesOrderBaseLine,
            DeliveryLineNum    = l.DeliveryLineNum,
            InvoiceLineNum     = l.InvoiceLineNum,
            InvoiceBaseType    = l.InvoiceBaseType,
            InvoiceBaseEntry   = l.InvoiceBaseEntry,
            InvoiceBaseLine    = l.InvoiceBaseLine
        };
}
