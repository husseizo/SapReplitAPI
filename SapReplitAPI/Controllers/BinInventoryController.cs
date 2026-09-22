using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/bin-inventory")]
[ServiceFilter(typeof(SapReplitAPI.Filters.ApiKeyAuthFilter))]
public class BinInventoryController : ControllerBase
{
    private readonly BinInventorySyncService          _sync;
    private readonly CacheDbContext                   _db;
    private readonly SapService                       _sap;
    private readonly NeonDbContext                    _neon;
    private readonly InventoryEventRefreshService     _inv;
    private readonly ILogger<BinInventoryController>  _log;

    public BinInventoryController(
        BinInventorySyncService sync,
        CacheDbContext db,
        SapService sap,
        NeonDbContext neon,
        InventoryEventRefreshService inv,
        ILogger<BinInventoryController> log)
    {
        _sync = sync;
        _db   = db;
        _sap  = sap;
        _neon = neon;
        _inv  = inv;
        _log  = log;
    }

    // ── Full sync ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a full SAP → SQLite → Neon bin inventory snapshot (all 4 bin-managed warehouses).
    /// Returns diagnostic report including reconciliation (MISSING_BIN_INVENTORY detection) vs WarehouseInventory.OnHand.
    /// </summary>
    [HttpPost("sync/full")]
    public async Task<IActionResult> FullSync()
    {
        _log.LogInformation("[BinInv] Manual full sync triggered via API.");
        var report = await _sync.FullSyncAsync();

        var meta = await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source");

        return Ok(new
        {
            TotalSapRows      = report.TotalSapRows,
            DistinctItemCodes = report.DistinctItemCodes,
            RowsByWarehouse   = report.RowsByWarehouse,
            Upserted          = report.Upserted,
            Removed           = report.Removed,
            SyncTime          = report.SyncTime.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            DurationSeconds   = Math.Round(report.DurationSeconds, 2),
            WatermarkAfter    = meta?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            TotalCachedRows   = await _db.BinInventories.CountAsync(),
            Reconciliation    = new
            {
                report.Reconciliation.Checked,
                report.Reconciliation.Matched,
                report.Reconciliation.Mismatched,
                report.Reconciliation.MissingBinInventory
            }
        });
    }

    // ── Delta sync ────────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a delta sync — detects changed ItemCodes from OINM + OITM since (last watermark − 2h),
    /// fetches current OIBQ snapshot for affected items, upserts, removes stale rows, and immediately
    /// pushes changes to Neon. ORDR excluded — SO commitment does not alter bin-level OnHandQty.
    /// </summary>
    [HttpPost("sync/delta")]
    public async Task<IActionResult> DeltaSync()
    {
        _log.LogInformation("[BinInv] Manual delta sync triggered via API.");

        var watermarkBefore = (await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source"))
            ?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "(none)";

        var (upserted, removed) = await _sync.DeltaSyncAsync();

        var watermarkAfter = (await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source"))
            ?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");

        return Ok(new
        {
            WatermarkBefore = watermarkBefore,
            WatermarkAfter  = watermarkAfter,
            Upserted        = upserted,
            Removed         = removed,
            TotalCachedRows = await _db.BinInventories.CountAsync()
        });
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns current BinInventory.Source watermark and row counts by warehouse.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var meta = await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source");

        var byWarehouse = await _db.BinInventories
            .Where(b => new[] { "001", "002", "003", "004" }.Contains(b.WhsCode))
            .GroupBy(b => b.WhsCode)
            .Select(g => new { WhsCode = g.Key, Count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            Watermark       = meta?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "(not set)",
            TotalCachedRows = byWarehouse.Sum(w => w.Count),
            ByWarehouse     = byWarehouse.OrderBy(w => w.WhsCode)
        });
    }

    // ── Mirror health ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compares SQLite and Neon row counts for BinInventory.
    /// Exposes: BinInventory.Source watermark, SQLite count, Neon count, row difference, lag seconds.
    /// Health: HEALTHY | ROW_COUNT_MISMATCH | MIRROR_LAGGING | MIRROR_STALE
    ///
    /// A gap like SQLite=15,506 vs Neon=13,293 is classified as ROW_COUNT_MISMATCH and visible here.
    /// </summary>
    [HttpGet("mirror-health")]
    public async Task<IActionResult> MirrorHealth()
    {
        var meta = await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source");

        int sqliteCount = await _db.BinInventories.CountAsync();

        int neonCount = 0;
        string neonError = "";
        try
        {
            var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
            if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
            if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""BinInventory""", conn);
            neonCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        catch (Exception ex)
        {
            neonError = ex.Message;
            _log.LogWarning(ex, "[BinInv] mirror-health: Neon count query failed.");
        }

        double? lagSeconds = null;
        string watermark   = "(not set)";
        if (meta is not null)
        {
            watermark  = meta.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");
            lagSeconds = (DateTime.UtcNow - meta.LastSyncedAt).TotalSeconds;
        }

        int diff = sqliteCount - neonCount;

        string status;
        if (!string.IsNullOrEmpty(neonError))
            status = "MIRROR_STALE";
        else if (diff != 0)
            status = "ROW_COUNT_MISMATCH";
        else if (lagSeconds > 3600)
            status = "MIRROR_LAGGING";
        else
            status = "HEALTHY";

        var recon = await _sync.BuildReconciliationReportAsync();

        return Ok(new
        {
            Status              = status,
            Watermark           = watermark,
            LagSeconds          = lagSeconds.HasValue ? Math.Round(lagSeconds.Value, 1) : (double?)null,
            SqliteRowCount      = sqliteCount,
            NeonRowCount        = neonCount,
            RowCountDifference  = diff,
            NeonError           = neonError.Length > 0 ? neonError : null,
            Reconciliation = new
            {
                recon.Checked,
                recon.Matched,
                recon.Mismatched,
                recon.MissingBinInventory
            }
        });
    }

    // ── Spot-check ────────────────────────────────────────────────────────────

    /// <summary>
    /// Spot-check: always inspects SAP live OIBQ, SQLite BinInventory, Neon BinInventory,
    /// and WarehouseInventory. Does NOT return 404 before checking SAP.
    ///
    /// Status codes:
    ///   IN_SYNC                    — SAP, SQLite, and Neon all agree
    ///   SQLITE_MISSING             — SAP has rows; SQLite missing
    ///   NEON_MISSING               — SQLite has rows; Neon missing
    ///   SQLITE_AND_NEON_MISSING    — SAP has rows; both caches missing
    ///   BIN_TOTAL_MISMATCH         — totals differ between sources
    ///   SAP_NO_BIN_STOCK           — no positive bin stock in SAP OIBQ
    ///   SAP_LOOKUP_FAILED          — SAP read threw exception
    ///   BIN_CACHE_MISSING          — WH.OnHand > 0 and IsBinManaged but no bins found anywhere
    /// </summary>
    [HttpGet("spot-check/{itemCode}")]
    public async Task<IActionResult> SpotCheck(string itemCode)
    {
        var upper = itemCode.Trim().ToUpperInvariant();

        // 1. SAP live OIBQ
        List<BinInventoryRow> live;
        string sapError = "";
        try
        {
            live = _sap.GetBinInventorySnapshotForItems(new[] { itemCode });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BinInv] spot-check SAP lookup failed for {Item}", itemCode);
            return Ok(new { Status = "SAP_LOOKUP_FAILED", ItemCode = itemCode, Error = ex.Message });
        }

        live = live.Where(r => r.ItemCode.Equals(upper, StringComparison.OrdinalIgnoreCase)).ToList();

        // 2. SQLite BinInventory
        var cached = await _db.BinInventories
            .Where(b => b.ItemCode == itemCode || b.ItemCode.ToUpper() == upper)
            .OrderBy(b => b.WhsCode).ThenBy(b => b.BinCode)
            .ToListAsync();

        // 3. WarehouseInventory
        var whRows = await _db.WarehouseInventories
            .Where(w => (w.ItemCode == itemCode || w.ItemCode.ToUpper() == upper)
                     && new[] { "001", "002", "003", "004" }.Contains(w.WhsCode))
            .ToListAsync();

        decimal whOnHand    = whRows.Sum(w => w.OnHand);
        bool isBinManaged   = whRows.Any(w => w.IsBinManaged);

        // 4. Neon BinInventory
        List<(string WhsCode, int BinAbsEntry, string BinCode, decimal BinOnHand)> neonRows = new();
        string neonError2 = "";
        try
        {
            var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
            if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
            if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(@"
SELECT ""WhsCode"",""BinAbsEntry"",""BinCode"",""BinOnHand""
FROM ""BinInventory""
WHERE ""ItemCode""=@ic
ORDER BY ""WhsCode"",""BinCode""", conn);
            cmd.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, upper);
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                neonRows.Add((rdr.GetString(0), rdr.GetInt32(1), rdr.GetString(2), rdr.GetDecimal(3)));
        }
        catch (Exception ex)
        {
            neonError2 = ex.Message;
            _log.LogWarning(ex, "[BinInv] spot-check Neon query failed for {Item}", itemCode);
        }

        // 5. Classify status
        string status;
        bool sapHasRows     = live.Count > 0;
        bool sqliteHasRows  = cached.Count > 0;
        bool neonHasRows    = neonRows.Count > 0;

        if (!sapHasRows && whOnHand > 0 && isBinManaged)
            status = "BIN_CACHE_MISSING";
        else if (!sapHasRows)
            status = "SAP_NO_BIN_STOCK";
        else if (!sqliteHasRows && !neonHasRows)
            status = "SQLITE_AND_NEON_MISSING";
        else if (!sqliteHasRows)
            status = "SQLITE_MISSING";
        else if (!neonHasRows)
            status = "NEON_MISSING";
        else
        {
            // Compare totals
            decimal sapTotal    = live.Sum(r => r.BinOnHand);
            decimal sqliteTotal = cached.Sum(b => b.BinOnHand);
            decimal neonTotal   = neonRows.Sum(r => r.BinOnHand);
            if (Math.Abs(sapTotal - sqliteTotal) > 0.001m || Math.Abs(sapTotal - neonTotal) > 0.001m)
                status = "BIN_TOTAL_MISMATCH";
            else
                status = "IN_SYNC";
        }

        return Ok(new
        {
            Status       = status,
            ItemCode     = itemCode,
            WhOnHand     = whOnHand,
            IsBinManaged = isBinManaged,
            SapBinCount  = live.Count,
            SqliteBinCount = cached.Count,
            NeonBinCount   = neonRows.Count,
            SapTotal     = live.Count > 0 ? live.Sum(r => r.BinOnHand) : 0m,
            SqliteTotal  = cached.Count > 0 ? cached.Sum(b => b.BinOnHand) : 0m,
            NeonTotal    = neonRows.Count > 0 ? neonRows.Sum(r => r.BinOnHand) : 0m,
            NeonError    = neonError2.Length > 0 ? neonError2 : null,
            SapRows = live.Select(r => new { r.WhsCode, r.BinAbsEntry, r.BinCode, r.BinOnHand }),
            SqliteRows = cached.Select(b => new { b.WhsCode, b.BinAbsEntry, b.BinCode, b.BinOnHand, b.LastUpdated }),
            NeonRows   = neonRows.Select(r => new { r.WhsCode, r.BinAbsEntry, r.BinCode, r.BinOnHand })
        });
    }

    // ── Self-healing repair ───────────────────────────────────────────────────

    /// <summary>
    /// Targeted self-healing: if WarehouseInventory.OnHand > 0 AND IsBinManaged AND Neon bins are missing,
    /// triggers: SAP OIBQ read → SQLite refresh → Neon targeted push.
    /// Only executes after a successful SAP read — does NOT mutate SAP.
    /// Returns structured result with before/after counts.
    /// </summary>
    [HttpPost("repair/{itemCode}")]
    public async Task<IActionResult> Repair(string itemCode, CancellationToken ct)
    {
        var upper = itemCode.Trim().ToUpperInvariant();
        _log.LogInformation("[BinInv] Repair requested for {Item}", itemCode);

        // Check preconditions: WH.OnHand > 0 AND IsBinManaged
        var whRows = await _db.WarehouseInventories
            .Where(w => (w.ItemCode == itemCode || w.ItemCode.ToUpper() == upper)
                     && new[] { "001", "002", "003", "004" }.Contains(w.WhsCode)
                     && w.IsBinManaged)
            .ToListAsync(ct);

        decimal whOnHand = whRows.Sum(w => w.OnHand);

        if (whOnHand <= 0)
        {
            return Ok(new { Skipped = true, Reason = $"WarehouseInventory.OnHand={whOnHand} — no stock to repair.", ItemCode = itemCode });
        }

        // Check Neon bins before repair
        int neonBefore = 0;
        try
        {
            var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
            if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
            if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync(ct);
            using var cmd = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""BinInventory"" WHERE ""ItemCode""=@ic", conn);
            cmd.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, upper);
            neonBefore = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BinInv] Repair: Neon pre-check failed for {Item}", itemCode);
        }

        // Execute targeted bin refresh: SAP → SQLite → Neon
        var result = await _inv.RefreshTargetedBinInventoryAsync(new[] { itemCode }, ct);

        // Re-read Neon count after repair
        int neonAfter = 0;
        try
        {
            var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
            if (conn.State == System.Data.ConnectionState.Broken) await conn.CloseAsync();
            if (conn.State != System.Data.ConnectionState.Open)   await conn.OpenAsync(ct);
            using var cmd = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""BinInventory"" WHERE ""ItemCode""=@ic", conn);
            cmd.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, upper);
            neonAfter = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BinInv] Repair: Neon post-check failed for {Item}", itemCode);
        }

        if (!result.Success)
        {
            _log.LogError("[BinInv] Repair failed for {Item}: {Error}", itemCode, result.Error);
            return StatusCode(500, new
            {
                Success  = false,
                ItemCode = itemCode,
                Error    = result.Error,
                NeonRowsBefore = neonBefore
            });
        }

        _log.LogInformation("[BinInv] Repair complete for {Item}: SapRows={S} NeonBefore={NB} NeonAfter={NA}",
            itemCode, result.SapRowsRead, neonBefore, neonAfter);

        return Ok(new
        {
            Success        = true,
            ItemCode       = itemCode,
            WhOnHand       = whOnHand,
            SapRowsRead    = result.SapRowsRead,
            SqliteUpserted = result.SqliteRowsUpserted,
            SqliteRemoved  = result.SqliteRowsRemoved,
            NeonUpserted   = result.NeonRowsUpserted,
            NeonRemoved    = result.NeonRowsRemoved,
            NeonRowsBefore = neonBefore,
            NeonRowsAfter  = neonAfter
        });
    }

    // ── Reconciliation ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs reconciliation report against current SQLite state.
    /// Detects: MISSING_BIN_INVENTORY (WH.OnHand > 0, IsBinManaged, but BinInventory rows = 0).
    /// This is the pattern observed for BM12867/MB101381.
    /// </summary>
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation()
    {
        var recon = await _sync.BuildReconciliationReportAsync();
        return Ok(new
        {
            recon.Checked,
            recon.Matched,
            recon.Mismatched,
            recon.MissingBinInventory
        });
    }
}
