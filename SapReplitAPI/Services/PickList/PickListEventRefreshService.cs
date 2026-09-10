#pragma warning disable CA1416

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Orchestrates the event-driven PickList fast path for a single OPKL AbsEntry:
///   1. SAP read + SQLite targeted write (via PickListCacheService.TargetedRefreshAsync)
///   2. Neon targeted write (DELETE AbsEntry rows + conditional INSERT), serialized by NeonPickListWriteCoordinator
///   3. NeonMirror:PickLists watermark advance in SQLite (non-fatal)
///
/// Called by ZoneFulfillmentPickListService and ZoneFulfillmentPickReconciliationService
/// trigger seams after durable state mutations.
/// Non-fatal contract: failure does NOT roll back the calling operation.
/// Scheduled fallbacks (PickListDeltaSyncJob, NeonSyncJob) remain in force.
/// </summary>
public sealed class PickListEventRefreshService : IPickListEventRefreshService
{
    private readonly PickListCacheService          _cache;
    private readonly NeonDbContext                 _neon;
    private readonly CacheDbContext                _sqlite;
    private readonly NeonPickListWriteCoordinator  _plCoord;
    private readonly ILogger<PickListEventRefreshService> _log;

    private const string NeonMirrorKey = "NeonMirror:PickLists";

    public PickListEventRefreshService(
        PickListCacheService          cache,
        NeonDbContext                 neon,
        CacheDbContext                sqlite,
        NeonPickListWriteCoordinator  plCoord,
        ILogger<PickListEventRefreshService> log)
    {
        _cache   = cache;
        _neon    = neon;
        _sqlite  = sqlite;
        _plCoord = plCoord;
        _log     = log;
    }

    /// <summary>
    /// Runs the full event-driven refresh for one AbsEntry.
    /// Returns (true, null) on full success; (false, error) if SQLite or Neon phase failed.
    /// The caller must not roll back any SAP or Molas state on failure — log and allow scheduled fallback.
    /// </summary>
    public async Task<(bool ok, string? error)> RefreshAsync(int absEntry, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[PickListEventRefresh] Start AbsEntry={Abs}", absEntry);

        // ── Phase 1: SAP read + SQLite targeted write ────────────────────────
        bool found;
        CachedPickList? header;
        List<CachedPickListLine> lines;
        List<CachedPickListBinAllocation> bins;
        DateTime sqliteWatermark;

        try
        {
            var p1Sw = Stopwatch.StartNew();
            (found, header, lines, bins, sqliteWatermark) =
                await _cache.TargetedRefreshAsync(absEntry, ct);
            p1Sw.Stop();

            _log.LogInformation(
                "[PickListEventRefresh] SQLite updated AbsEntry={Abs} Found={F} Lines={L} Bins={B} Phase1Ms={Ms:F0}",
                absEntry, found, lines.Count, bins.Count, p1Sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[PickListEventRefresh] SQLite phase failed AbsEntry={Abs} TotalMs={Ms:F0}",
                absEntry, sw.Elapsed.TotalMilliseconds);
            return (false, $"SQLite phase: {ex.GetType().Name}: {ex.Message}");
        }

        // ── Phase 2: Neon targeted write ─────────────────────────────────────
        var neonSw = Stopwatch.StartNew();
        try
        {
            await _plCoord.WaitAsync(ct);
            try
            {
                var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
                if (conn.State == System.Data.ConnectionState.Broken)
                    await conn.CloseAsync();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync(ct);

                using var tx = await conn.BeginTransactionAsync(ct);

                // Delete all existing Neon rows for this AbsEntry (idempotent)
                using (var del = new NpgsqlCommand(
                    @"DELETE FROM ""PickListBinAllocations"" WHERE ""AbsEntry"" = @ae", conn, tx))
                {
                    del.Parameters.AddWithValue("@ae", NpgsqlDbType.Integer, absEntry);
                    await del.ExecuteNonQueryAsync(ct);
                }
                using (var del = new NpgsqlCommand(
                    @"DELETE FROM ""PickListLines"" WHERE ""AbsEntry"" = @ae", conn, tx))
                {
                    del.Parameters.AddWithValue("@ae", NpgsqlDbType.Integer, absEntry);
                    await del.ExecuteNonQueryAsync(ct);
                }

                if (found && header != null)
                {
                    // Header: ON CONFLICT DO UPDATE for safety
                    using (var ins = new NpgsqlCommand(@"
INSERT INTO ""PickLists"" (""AbsEntry"",""Name"",""OwnerCode"",""OwnerName"",""Status"",""Canceled"",""Remarks"",
    ""PickDate"",""CreateDate"",""UpdateDate"",""U_ReplitId"",""LastSyncedAt"",""SlpCode"",""SlpName"",""ZoneRef"",""DeliveryLocation"")
VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15)
ON CONFLICT (""AbsEntry"") DO UPDATE SET
    ""Name""=EXCLUDED.""Name"",""OwnerCode""=EXCLUDED.""OwnerCode"",""OwnerName""=EXCLUDED.""OwnerName"",
    ""Status""=EXCLUDED.""Status"",""Canceled""=EXCLUDED.""Canceled"",""Remarks""=EXCLUDED.""Remarks"",
    ""PickDate""=EXCLUDED.""PickDate"",""CreateDate""=EXCLUDED.""CreateDate"",""UpdateDate""=EXCLUDED.""UpdateDate"",
    ""U_ReplitId""=EXCLUDED.""U_ReplitId"",""LastSyncedAt""=EXCLUDED.""LastSyncedAt"",
    ""SlpCode""=EXCLUDED.""SlpCode"",""SlpName""=EXCLUDED.""SlpName"",
    ""ZoneRef""=EXCLUDED.""ZoneRef"",""DeliveryLocation""=EXCLUDED.""DeliveryLocation""", conn, tx))
                    {
                        ins.Parameters.AddWithValue("@p0",  NpgsqlDbType.Integer,     header.AbsEntry);
                        ins.Parameters.AddWithValue("@p1",  NpgsqlDbType.Text,        header.Name      ?? "");
                        ins.Parameters.AddWithValue("@p2",  NpgsqlDbType.Integer,     header.OwnerCode);
                        ins.Parameters.AddWithValue("@p3",  NpgsqlDbType.Text,        header.OwnerName ?? "");
                        ins.Parameters.AddWithValue("@p4",  NpgsqlDbType.Text,        header.Status    ?? "");
                        ins.Parameters.AddWithValue("@p5",  NpgsqlDbType.Text,        header.Canceled  ?? "N");
                        ins.Parameters.AddWithValue("@p6",  NpgsqlDbType.Text,        header.Remarks   ?? "");
                        ins.Parameters.AddWithValue("@p7",  NpgsqlDbType.Date,        header.PickDate);
                        ins.Parameters.AddWithValue("@p8",  NpgsqlDbType.Date,        header.CreateDate);
                        ins.Parameters.AddWithValue("@p9",  NpgsqlDbType.Date,        header.UpdateDate);
                        ins.Parameters.AddWithValue("@p10", NpgsqlDbType.Text,        (object?)header.U_ReplitId ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p11", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(header.LastSyncedAt, DateTimeKind.Utc));
                        ins.Parameters.AddWithValue("@p12", NpgsqlDbType.Integer,     (object?)header.SlpCode ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p13", NpgsqlDbType.Text,        header.SlpName   ?? "");
                        ins.Parameters.AddWithValue("@p14", NpgsqlDbType.Text,        (object?)header.ZoneRef          ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p15", NpgsqlDbType.Text,        (object?)header.DeliveryLocation ?? DBNull.Value);
                        await ins.ExecuteNonQueryAsync(ct);
                    }

                    // Lines: DELETE already done; plain INSERT
                    foreach (var l in lines)
                    {
                        using var ins = new NpgsqlCommand(@"
INSERT INTO ""PickListLines"" (""AbsEntry"",""PickEntry"",""OrderEntry"",""OrderLine"",""BaseObject"",
    ""RelQtty"",""PickQtty"",""PickStatus"",""PrevReleas"",""ItemCode"",""Dscription"",""WhsCode"",
    ""SourceSoDocNum"",""ZoneRef"",""DeliveryLocation"",""U_ReplitId"",""CreatedTime"",""PickedTime"")
VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17)", conn, tx);

                        ins.Parameters.AddWithValue("@p0",  NpgsqlDbType.Integer,     l.AbsEntry);
                        ins.Parameters.AddWithValue("@p1",  NpgsqlDbType.Integer,     l.PickEntry);
                        ins.Parameters.AddWithValue("@p2",  NpgsqlDbType.Integer,     l.OrderEntry);
                        ins.Parameters.AddWithValue("@p3",  NpgsqlDbType.Integer,     l.OrderLine);
                        ins.Parameters.AddWithValue("@p4",  NpgsqlDbType.Integer,     l.BaseObject);
                        ins.Parameters.AddWithValue("@p5",  NpgsqlDbType.Numeric,     l.RelQtty);
                        ins.Parameters.AddWithValue("@p6",  NpgsqlDbType.Numeric,     l.PickQtty);
                        ins.Parameters.AddWithValue("@p7",  NpgsqlDbType.Text,        l.PickStatus ?? "");
                        ins.Parameters.AddWithValue("@p8",  NpgsqlDbType.Numeric,     l.PrevReleas);
                        ins.Parameters.AddWithValue("@p9",  NpgsqlDbType.Text,        l.ItemCode   ?? "");
                        ins.Parameters.AddWithValue("@p10", NpgsqlDbType.Text,        l.Dscription ?? "");
                        ins.Parameters.AddWithValue("@p11", NpgsqlDbType.Text,        l.WhsCode    ?? "");
                        ins.Parameters.AddWithValue("@p12", NpgsqlDbType.Integer,     l.SourceSoDocNum.HasValue ? (object)l.SourceSoDocNum.Value : DBNull.Value);
                        ins.Parameters.AddWithValue("@p13", NpgsqlDbType.Text,        (object?)l.ZoneRef          ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p14", NpgsqlDbType.Text,        (object?)l.DeliveryLocation ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p15", NpgsqlDbType.Text,        (object?)l.U_ReplitId       ?? DBNull.Value);
                        // CreatedTime / PickedTime: NULL must remain NULL — never substitute sync time
                        ins.Parameters.AddWithValue("@p16", NpgsqlDbType.TimestampTz, l.CreatedTime.HasValue ? (object)DateTime.SpecifyKind(l.CreatedTime.Value, DateTimeKind.Utc) : DBNull.Value);
                        ins.Parameters.AddWithValue("@p17", NpgsqlDbType.TimestampTz, l.PickedTime.HasValue  ? (object)DateTime.SpecifyKind(l.PickedTime.Value,  DateTimeKind.Utc) : DBNull.Value);
                        await ins.ExecuteNonQueryAsync(ct);
                    }

                    // Bins: DELETE already done; plain INSERT
                    foreach (var b in bins)
                    {
                        using var ins = new NpgsqlCommand(@"
INSERT INTO ""PickListBinAllocations"" (""AbsEntry"",""PickEntry"",""Pkl2LinNum"",""OrderEntry"",""OrderLine"",
    ""ItemCode"",""WhsCode"",""BinAbsEntry"",""BinCode"",""PickQtty"",""RelQtty"",""OpenCreQty"",
    ""PickListName"",""PickListStatus"",""SlpCode"",""SlpName"",""ZoneRef"",""DeliveryLocation"",""U_ReplitId"")
VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18)", conn, tx);

                        ins.Parameters.AddWithValue("@p0",  NpgsqlDbType.Integer, b.AbsEntry);
                        ins.Parameters.AddWithValue("@p1",  NpgsqlDbType.Integer, b.PickEntry);
                        ins.Parameters.AddWithValue("@p2",  NpgsqlDbType.Integer, b.Pkl2LinNum);
                        ins.Parameters.AddWithValue("@p3",  NpgsqlDbType.Integer, b.OrderEntry);
                        ins.Parameters.AddWithValue("@p4",  NpgsqlDbType.Integer, b.OrderLine);
                        ins.Parameters.AddWithValue("@p5",  NpgsqlDbType.Text,    b.ItemCode       ?? "");
                        ins.Parameters.AddWithValue("@p6",  NpgsqlDbType.Text,    b.WhsCode        ?? "");
                        ins.Parameters.AddWithValue("@p7",  NpgsqlDbType.Integer, b.BinAbsEntry);
                        ins.Parameters.AddWithValue("@p8",  NpgsqlDbType.Text,    b.BinCode        ?? "");
                        ins.Parameters.AddWithValue("@p9",  NpgsqlDbType.Numeric, b.PickQtty);
                        ins.Parameters.AddWithValue("@p10", NpgsqlDbType.Numeric, b.RelQtty);
                        ins.Parameters.AddWithValue("@p11", NpgsqlDbType.Numeric, b.OpenCreQty);
                        ins.Parameters.AddWithValue("@p12", NpgsqlDbType.Text,    b.PickListName   ?? "");
                        ins.Parameters.AddWithValue("@p13", NpgsqlDbType.Text,    b.PickListStatus ?? "");
                        ins.Parameters.AddWithValue("@p14", NpgsqlDbType.Integer, (object?)b.SlpCode ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p15", NpgsqlDbType.Text,    b.SlpName        ?? "");
                        ins.Parameters.AddWithValue("@p16", NpgsqlDbType.Text,    (object?)b.ZoneRef          ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p17", NpgsqlDbType.Text,    (object?)b.DeliveryLocation ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@p18", NpgsqlDbType.Text,    (object?)b.U_ReplitId       ?? DBNull.Value);
                        await ins.ExecuteNonQueryAsync(ct);
                    }
                }

                await tx.CommitAsync(ct);
            }
            finally
            {
                _plCoord.Release();
            }

            neonSw.Stop();
            _log.LogInformation(
                "[PickListEventRefresh] Neon updated AbsEntry={Abs} Found={F} Lines={L} Bins={B} NeonMs={Ms:F0}",
                absEntry, found, lines.Count, bins.Count, neonSw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            neonSw.Stop();
            sw.Stop();
            _log.LogError(ex, "[PickListEventRefresh] Neon phase failed AbsEntry={Abs} TotalMs={Ms:F0}",
                absEntry, sw.Elapsed.TotalMilliseconds);
            return (false, $"Neon phase: {ex.GetType().Name}: {ex.Message}");
        }

        // ── Phase 3: advance NeonMirror:PickLists watermark ──────────────────
        // Prevents NeonSyncJob from treating this change as unseen on next cycle.
        // Non-fatal: Neon write is already committed; a watermark failure is safe.
        try
        {
            var meta = await _sqlite.SyncMetadata.FirstOrDefaultAsync(x => x.Type == NeonMirrorKey, ct);
            if (meta == null)
                await _sqlite.SyncMetadata.AddAsync(new SyncMetadata { Type = NeonMirrorKey, LastSyncedAt = sqliteWatermark }, ct);
            else if (meta.LastSyncedAt < sqliteWatermark)
                meta.LastSyncedAt = sqliteWatermark;

            await _sqlite.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[PickListEventRefresh] NeonMirror watermark update failed (non-fatal) AbsEntry={Abs}", absEntry);
        }

        sw.Stop();
        _log.LogInformation(
            "[PickListEventRefresh] Done AbsEntry={Abs} Found={F} Lines={L} Bins={B} TotalMs={Ms:F0}",
            absEntry, found, lines.Count, bins.Count, sw.Elapsed.TotalMilliseconds);

        return (true, null);
    }
}
