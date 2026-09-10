#pragma warning disable CA1416 // Possible null argument

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.TodayOrders;

/// <summary>
/// Orchestrates the event-driven TodayOrders fast path for a single ORDR DocEntry:
///   1. SAP read + SQLite targeted write (via TodayOrderCacheService.RefreshSingleOrderAsync)
///   2. Neon targeted write (DELETE DocEntry + conditional INSERT), serialized by NeonTodayOrderWriteCoordinator
///   3. NeonMirror:TodayOrders watermark advance in SQLite
///
/// Called by SalesOrderCommitmentEventHandler for 17/A, 17/U, 17/C events.
/// Does NOT perform TRUNCATE — only touches rows for the given DocEntry.
/// </summary>
public sealed class TodayOrderEventRefreshService
{
    private readonly TodayOrderCacheService _todayCache;
    private readonly NeonDbContext _neon;
    private readonly CacheDbContext _sqlite;
    private readonly NeonTodayOrderWriteCoordinator _neonCoord;
    private readonly ILogger<TodayOrderEventRefreshService> _log;

    private const string NeonMirrorKey = "NeonMirror:TodayOrders";

    public TodayOrderEventRefreshService(
        TodayOrderCacheService todayCache,
        NeonDbContext neon,
        CacheDbContext sqlite,
        NeonTodayOrderWriteCoordinator neonCoord,
        ILogger<TodayOrderEventRefreshService> log)
    {
        _todayCache = todayCache;
        _neon       = neon;
        _sqlite     = sqlite;
        _neonCoord  = neonCoord;
        _log        = log;
    }

    /// <summary>
    /// Runs the full event-driven refresh for one DocEntry.
    /// Returns (true, null) on success; (false, error) on any failure — caller should mark event retryable.
    /// </summary>
    public async Task<(bool ok, string? error)> RefreshAsync(int docEntry, string txType, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[TodayOrderEventRefresh] Start DocEntry={D} TxType={T}", docEntry, txType);

        // ── Phase 1: SAP read + SQLite targeted write ────────────────────────
        long sapReadMs = 0;
        bool qualifies;
        CachedTodayOrder? header;
        List<CachedTodayOrderLine> lines;
        DateTime sqliteWatermark;

        try
        {
            var phase1Sw = Stopwatch.StartNew();
            (qualifies, header, lines, sqliteWatermark) =
                await _todayCache.RefreshSingleOrderAsync(docEntry, ct);
            phase1Sw.Stop();

            sapReadMs = phase1Sw.ElapsedMilliseconds;

            _log.LogInformation(
                "[TodayOrderEventRefresh] SQLite updated DocEntry={D} Qualifies={Q} Lines={L} Phase1Ms={Ms:F0}",
                docEntry, qualifies, lines.Count, phase1Sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[TodayOrderEventRefresh] Failed at SQLite phase. DocEntry={D} TxType={T} TotalMs={Ms:F0}",
                docEntry, txType, sw.Elapsed.TotalMilliseconds);
            return (false, $"SQLite phase: {ex.GetType().Name}: {ex.Message}");
        }

        // ── Phase 2: Neon targeted write ─────────────────────────────────────
        var neonSw = Stopwatch.StartNew();
        try
        {
            await _neonCoord.WaitAsync(ct);
            try
            {
                var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
                if (conn.State == System.Data.ConnectionState.Broken)
                    await conn.CloseAsync();
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync(ct);

                using var tx = await conn.BeginTransactionAsync(ct);

                // Remove any existing rows for this DocEntry (idempotent)
                using (var delLines = new NpgsqlCommand(
                    @"DELETE FROM ""TodayOrderLines"" WHERE ""DocEntry"" = @docEntry", conn, tx))
                {
                    delLines.Parameters.AddWithValue("@docEntry", NpgsqlDbType.Integer, docEntry);
                    await delLines.ExecuteNonQueryAsync(ct);
                }

                using (var delHeader = new NpgsqlCommand(
                    @"DELETE FROM ""TodayOrderHeaders"" WHERE ""DocEntry"" = @docEntry", conn, tx))
                {
                    delHeader.Parameters.AddWithValue("@docEntry", NpgsqlDbType.Integer, docEntry);
                    await delHeader.ExecuteNonQueryAsync(ct);
                }

                if (qualifies && header != null)
                {
                    // Upsert header
                    using var insHeader = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderHeaders"" (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",""SlpCode"",""SlpName"",""Cancelled"")
VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum""     = EXCLUDED.""DocNum"",
    ""CardName""   = EXCLUDED.""CardName"",
    ""DocDate""    = EXCLUDED.""DocDate"",
    ""OrderValue"" = EXCLUDED.""OrderValue"",
    ""Status""     = EXCLUDED.""Status"",
    ""SlpCode""    = EXCLUDED.""SlpCode"",
    ""SlpName""    = EXCLUDED.""SlpName"",
    ""Cancelled""  = EXCLUDED.""Cancelled""", conn, tx);

                    insHeader.Parameters.AddWithValue("@p0", NpgsqlDbType.Integer, header.DocEntry);
                    insHeader.Parameters.AddWithValue("@p1", NpgsqlDbType.Integer, header.DocNum);
                    insHeader.Parameters.AddWithValue("@p2", NpgsqlDbType.Text,    header.CardName ?? string.Empty);
                    insHeader.Parameters.AddWithValue("@p3", NpgsqlDbType.Date,    header.DocDate);
                    insHeader.Parameters.AddWithValue("@p4", NpgsqlDbType.Numeric, header.OrderValue);
                    insHeader.Parameters.AddWithValue("@p5", NpgsqlDbType.Text,    header.Status   ?? string.Empty);
                    insHeader.Parameters.AddWithValue("@p6", NpgsqlDbType.Integer, (object?)header.SlpCode ?? DBNull.Value);
                    insHeader.Parameters.AddWithValue("@p7", NpgsqlDbType.Text,    header.SlpName  ?? string.Empty);
                    insHeader.Parameters.AddWithValue("@p8", NpgsqlDbType.Boolean, header.Cancelled);
                    await insHeader.ExecuteNonQueryAsync(ct);

                    // Insert lines (DELETE already removed them above; plain INSERT is safe)
                    foreach (var line in lines)
                    {
                        using var insLine = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderLines"" (""DocEntry"",""DocDate"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)", conn, tx);

                        insLine.Parameters.AddWithValue("@p0", NpgsqlDbType.Integer, line.DocEntry);
                        insLine.Parameters.AddWithValue("@p1", NpgsqlDbType.Date,    line.DocDate);
                        insLine.Parameters.AddWithValue("@p2", NpgsqlDbType.Text,    line.ItemCode      ?? string.Empty);
                        insLine.Parameters.AddWithValue("@p3", NpgsqlDbType.Text,    line.Dscription    ?? string.Empty);
                        insLine.Parameters.AddWithValue("@p4", NpgsqlDbType.Numeric, line.Quantity);
                        insLine.Parameters.AddWithValue("@p5", NpgsqlDbType.Numeric, line.Price);
                        insLine.Parameters.AddWithValue("@p6", NpgsqlDbType.Text,    line.WhsCode       ?? string.Empty);
                        insLine.Parameters.AddWithValue("@p7", NpgsqlDbType.Text,    line.U_ItemName    ?? string.Empty);
                        insLine.Parameters.AddWithValue("@p8", NpgsqlDbType.Text,    line.U_Manufacturer ?? string.Empty);
                        await insLine.ExecuteNonQueryAsync(ct);
                    }
                }

                await tx.CommitAsync(ct);
            }
            finally
            {
                _neonCoord.Release();
            }

            neonSw.Stop();
            _log.LogInformation(
                "[TodayOrderEventRefresh] Neon updated DocEntry={D} Qualifies={Q} Lines={L} NeonMs={Ms:F0}",
                docEntry, qualifies, lines.Count, neonSw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            neonSw.Stop();
            sw.Stop();
            _log.LogError(ex, "[TodayOrderEventRefresh] Failed at Neon phase. DocEntry={D} TxType={T} TotalMs={Ms:F0}",
                docEntry, txType, sw.Elapsed.TotalMilliseconds);
            // SQLite is already correct. Neon failure makes event retryable.
            // On retry: SQLite write is idempotent; Neon write picks up from correct SQLite state.
            return (false, $"Neon phase: {ex.GetType().Name}: {ex.Message}");
        }

        // ── Phase 3: advance NeonMirror watermark ────────────────────────────
        // Prevents NeonSyncJob from doing an unnecessary full replace for this same change.
        // Safe: Neon write already committed; SQLite watermark is sqliteWatermark.
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
            // Non-fatal: NeonSyncJob will detect source > mirror and run full replace next cycle.
            _log.LogWarning(ex, "[TodayOrderEventRefresh] NeonMirror watermark update failed (non-fatal). DocEntry={D}", docEntry);
        }

        sw.Stop();
        _log.LogInformation(
            "[TodayOrderEventRefresh] Done DocEntry={D} TxType={T} Qualifies={Q} LineCount={L} SapReadMs={SAP} NeonMs={N} TotalMs={T2:F0}",
            docEntry, txType, qualifies, lines.Count, sapReadMs, neonSw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds);

        return (true, null);
    }
}
