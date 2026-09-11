using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.CachedServices;

namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Writes OPKL/PKL1/PKL2 data into the SQLite pick list cache.
/// Never writes SyncMetadata watermarks — those are owned by the sync jobs.
/// Never calls SAP mutations.
/// </summary>
public class PickListCacheService
{
    private readonly SapService _sap;
    private readonly CacheDbContext _db;
    private readonly PickListTimestampReader _tsReader;
    private readonly ILogger<PickListCacheService> _log;

    public PickListCacheService(
        SapService sap,
        CacheDbContext db,
        PickListTimestampReader tsReader,
        ILogger<PickListCacheService> log)
    {
        _sap      = sap;
        _db       = db;
        _tsReader = tsReader;
        _log      = log;
    }

    // ─── Full sync ────────────────────────────────────────────────────────────

    /// <summary>Fetch all OPKL rows from SAP and upsert them into SQLite.</summary>
    public async Task FullSyncAsync(CancellationToken ct = default)
    {
        var headers = _sap.GetPickListHeaders(fromDate: null);
        _log.LogInformation("[PickListCache] Full sync: {Count} headers from SAP", headers.Count);

        foreach (var header in headers)
        {
            ct.ThrowIfCancellationRequested();
            var lines = _sap.GetPickListLines(new[] { header.AbsEntry });
            var bins  = _sap.GetPickListBinAllocations(new[] { header.AbsEntry });
            await UpsertPickListAsync(header, lines, bins, ct);
        }
        _log.LogInformation("[PickListCache] Full sync complete — {Count} pick lists written", headers.Count);
    }

    // ─── Delta sync ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch OPKL rows updated since watermarkUtc (date-only, 1-day lookback, EAT timezone).
    /// Canceled='Y' rows are included — cancellation transitions must be cached.
    /// </summary>
    public async Task<int> DeltaSyncAsync(DateTime watermarkUtc, CancellationToken ct = default)
    {
        // Pass raw UTC watermark — GetPickListHeaders performs the single UTC→EAT conversion.
        var headers = _sap.GetPickListHeaders(fromDate: watermarkUtc);
        if (headers.Count == 0) return 0;

        _log.LogInformation("[PickListCache] Delta sync watermark={Wm:u}: {Count} headers", watermarkUtc, headers.Count);

        foreach (var header in headers)
        {
            ct.ThrowIfCancellationRequested();
            var lines = _sap.GetPickListLines(new[] { header.AbsEntry });
            var bins  = _sap.GetPickListBinAllocations(new[] { header.AbsEntry });
            await UpsertPickListAsync(header, lines, bins, ct);
        }
        return headers.Count;
    }

    // ─── Targeted refresh ─────────────────────────────────────────────────────

    /// <summary>Fetch a single OPKL by AbsEntry from SAP and write it into SQLite.</summary>
    public async Task RefreshPickListAsync(int absEntry, CancellationToken ct = default)
    {
        var headers = _sap.GetPickListHeaders(fromDate: null);
        var header  = headers.FirstOrDefault(h => h.AbsEntry == absEntry);
        if (header == null)
        {
            _log.LogWarning("[PickListCache] RefreshPickList: AbsEntry {AbsEntry} not found in SAP", absEntry);
            return;
        }
        var lines = _sap.GetPickListLines(new[] { absEntry });
        var bins  = _sap.GetPickListBinAllocations(new[] { absEntry });
        await UpsertPickListAsync(header, lines, bins, ct);
        _log.LogInformation("[PickListCache] Refreshed pick list {AbsEntry}", absEntry);
    }

    /// <summary>
    /// Targeted single-AbsEntry refresh: reads OPKL, PKL1, PKL2 from SAP, writes SQLite.
    /// Returns the header/lines/bins so the caller can drive the Neon phase without a second SAP read.
    /// Returns found=false if SAP has no OPKL with this AbsEntry (e.g. not yet committed — very rare).
    /// </summary>
    public async Task<(bool found, CachedPickList? header, List<CachedPickListLine> lines, List<CachedPickListBinAllocation> bins, DateTime watermark)>
        TargetedRefreshAsync(int absEntry, CancellationToken ct = default)
    {
        var header = _sap.GetPickListHeaderByAbsEntry(absEntry);
        if (header == null)
        {
            _log.LogWarning("[PickListCache] TargetedRefresh: AbsEntry={AbsEntry} not found in SAP", absEntry);
            return (false, null, new(), new(), DateTime.UtcNow);
        }
        var lines = _sap.GetPickListLines(new[] { absEntry });
        var bins  = _sap.GetPickListBinAllocations(new[] { absEntry });
        await UpsertPickListAsync(header, lines, bins, ct);
        _log.LogInformation("[PickListCache] TargetedRefresh: AbsEntry={AbsEntry} Lines={L} Bins={B}",
            absEntry, lines.Count, bins.Count);
        return (true, header, lines, bins, header.LastSyncedAt);
    }

    // ─── Read from cache ──────────────────────────────────────────────────────

    public async Task<CachedPickList?> ReadPickListAsync(int absEntry, CancellationToken ct = default)
        => await _db.PickLists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AbsEntry == absEntry, ct);

    public async Task<List<CachedPickListLine>> ReadPickListLinesAsync(int absEntry, CancellationToken ct = default)
        => await _db.PickListLines.AsNoTracking()
            .Where(l => l.AbsEntry == absEntry)
            .OrderBy(l => l.PickEntry)
            .ToListAsync(ct);

    public async Task<List<CachedPickListBinAllocation>> ReadPickListBinsAsync(int absEntry, CancellationToken ct = default)
        => await _db.PickListBinAllocations.AsNoTracking()
            .Where(b => b.AbsEntry == absEntry)
            .OrderBy(b => b.Pkl2LinNum)
            .ToListAsync(ct);

    // ─── Cache counts ─────────────────────────────────────────────────────────

    public async Task<(int Headers, int Lines, int Bins)> GetCacheCountsAsync(CancellationToken ct = default)
    {
        var headers = await _db.PickLists.CountAsync(ct);
        var lines   = await _db.PickListLines.CountAsync(ct);
        var bins    = await _db.PickListBinAllocations.CountAsync(ct);
        return (headers, lines, bins);
    }

    // ─── Core upsert (one SQLite transaction per pick list) ───────────────────

    private async Task UpsertPickListAsync(
        CachedPickList header,
        List<CachedPickListLine> lines,
        List<CachedPickListBinAllocation> bins,
        CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Fetch PLR timestamps before the SQLite transaction to avoid holding the lock during SQL Server I/O.
        var timestamps = await _tsReader.GetTimestampsAsync(header.AbsEntry, ct);

        using var tx  = await _db.Database.BeginTransactionAsync(ct);
        var sq        = (SqliteConnection)conn;
        var sqTx      = (SqliteTransaction)tx.GetDbTransaction();

        try
        {
            // 1. UPSERT header
            using (var cmd = sq.CreateCommand())
            {
                cmd.Transaction  = sqTx;
                cmd.CommandText = @"
INSERT INTO PickLists
(AbsEntry,Name,OwnerCode,OwnerName,Status,Canceled,Remarks,PickDate,CreateDate,UpdateDate,U_ReplitId,SlpCode,SlpName,LastSyncedAt,ZoneRef,DeliveryLocation)
VALUES($ae,$nm,$oc,$on,$st,$ca,$re,$pd,$cd,$ud,$ri,$sc,$sn,$ls,$zr,$dl)
ON CONFLICT(AbsEntry) DO UPDATE SET
 Name=excluded.Name, OwnerCode=excluded.OwnerCode, OwnerName=excluded.OwnerName,
 Status=excluded.Status, Canceled=excluded.Canceled, Remarks=excluded.Remarks,
 PickDate=excluded.PickDate, CreateDate=excluded.CreateDate, UpdateDate=excluded.UpdateDate,
 U_ReplitId=excluded.U_ReplitId, SlpCode=excluded.SlpCode, SlpName=excluded.SlpName,
 LastSyncedAt=excluded.LastSyncedAt, ZoneRef=excluded.ZoneRef, DeliveryLocation=excluded.DeliveryLocation";

                cmd.Parameters.AddWithValue("$ae", header.AbsEntry);
                cmd.Parameters.AddWithValue("$nm", header.Name);
                cmd.Parameters.AddWithValue("$oc", header.OwnerCode);
                cmd.Parameters.AddWithValue("$on", header.OwnerName);
                cmd.Parameters.AddWithValue("$st", header.Status);
                cmd.Parameters.AddWithValue("$ca", header.Canceled);
                cmd.Parameters.AddWithValue("$re", header.Remarks);
                cmd.Parameters.AddWithValue("$pd", header.PickDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$cd", header.CreateDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$ud", header.UpdateDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$ri", (object?)header.U_ReplitId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sc", (object?)header.SlpCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sn", header.SlpName);
                cmd.Parameters.AddWithValue("$ls", header.LastSyncedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$zr", (object?)header.ZoneRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dl", (object?)header.DeliveryLocation ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. DELETE then INSERT lines
            using (var del = sq.CreateCommand())
            {
                del.Transaction = sqTx;
                del.CommandText = "DELETE FROM PickListLines WHERE AbsEntry=$ae";
                del.Parameters.AddWithValue("$ae", header.AbsEntry);
                await del.ExecuteNonQueryAsync(ct);
            }
            if (lines.Count > 0)
            {
                using var ins = sq.CreateCommand();
                ins.Transaction = sqTx;
                ins.CommandText = @"
INSERT INTO PickListLines
(AbsEntry,PickEntry,OrderEntry,OrderLine,BaseObject,RelQtty,PickQtty,PickStatus,PrevReleas,
 ItemCode,Dscription,WhsCode,SourceSoDocNum,ZoneRef,DeliveryLocation,U_ReplitId,CreatedTime,PickedTime)
VALUES($ae,$pe,$oe,$ol,$bo,$rq,$pq,$ps,$pr,$ic,$ds,$wh,$sn,$zr,$dl,$ur,$ct,$pt)";

                var pAe = ins.Parameters.Add("$ae", SqliteType.Integer);
                var pPe = ins.Parameters.Add("$pe", SqliteType.Integer);
                var pOe = ins.Parameters.Add("$oe", SqliteType.Integer);
                var pOl = ins.Parameters.Add("$ol", SqliteType.Integer);
                var pBo = ins.Parameters.Add("$bo", SqliteType.Integer);
                var pRq = ins.Parameters.Add("$rq", SqliteType.Real);
                var pPq = ins.Parameters.Add("$pq", SqliteType.Real);
                var pPs = ins.Parameters.Add("$ps", SqliteType.Text);
                var pPr = ins.Parameters.Add("$pr", SqliteType.Real);
                var pIc = ins.Parameters.Add("$ic", SqliteType.Text);
                var pDs = ins.Parameters.Add("$ds", SqliteType.Text);
                var pWh = ins.Parameters.Add("$wh", SqliteType.Text);
                var pSn = ins.Parameters.Add("$sn", SqliteType.Integer);
                var pZr = ins.Parameters.Add("$zr", SqliteType.Text);
                var pDl = ins.Parameters.Add("$dl", SqliteType.Text);
                var pUr = ins.Parameters.Add("$ur", SqliteType.Text);
                var pCt = ins.Parameters.Add("$ct", SqliteType.Text);
                var pPt = ins.Parameters.Add("$pt", SqliteType.Text);

                foreach (var l in lines)
                {
                    timestamps.TryGetValue((l.OrderEntry, l.OrderLine), out var ts);

                    pAe.Value = l.AbsEntry;
                    pPe.Value = l.PickEntry;
                    pOe.Value = l.OrderEntry;
                    pOl.Value = l.OrderLine;
                    pBo.Value = l.BaseObject;
                    pRq.Value = (double)l.RelQtty;
                    pPq.Value = (double)l.PickQtty;
                    pPs.Value = l.PickStatus;
                    pPr.Value = (double)l.PrevReleas;
                    pIc.Value = l.ItemCode;
                    pDs.Value = l.Dscription;
                    pWh.Value = l.WhsCode;
                    pSn.Value = l.SourceSoDocNum.HasValue ? (object)l.SourceSoDocNum.Value : DBNull.Value;
                    pZr.Value = (object?)l.ZoneRef ?? DBNull.Value;
                    pDl.Value = (object?)l.DeliveryLocation ?? DBNull.Value;
                    pUr.Value = (object?)l.U_ReplitId ?? DBNull.Value;
                    pCt.Value = ts.createdAtUtc != default ? (object)ts.createdAtUtc.ToString("o") : DBNull.Value;
                    pPt.Value = ts.pickedAtUtc.HasValue  ? (object)ts.pickedAtUtc.Value.ToString("o") : DBNull.Value;
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            // 3. DELETE then INSERT bin allocations
            using (var del = sq.CreateCommand())
            {
                del.Transaction = sqTx;
                del.CommandText = "DELETE FROM PickListBinAllocations WHERE AbsEntry=$ae";
                del.Parameters.AddWithValue("$ae", header.AbsEntry);
                await del.ExecuteNonQueryAsync(ct);
            }
            if (bins.Count > 0)
            {
                using var ins = sq.CreateCommand();
                ins.Transaction = sqTx;
                ins.CommandText = @"
INSERT INTO PickListBinAllocations
(AbsEntry,PickEntry,Pkl2LinNum,OrderEntry,OrderLine,ItemCode,WhsCode,BinAbsEntry,BinCode,
 PickQtty,RelQtty,OpenCreQty,PickListName,PickListStatus,SlpCode,SlpName,ZoneRef,DeliveryLocation,U_ReplitId)
VALUES($ae,$pe,$pln,$oe,$ol,$ic,$wh,$ba,$bc,$pq,$rq,$oq,$pln2,$pls,$sc,$sn,$zr,$dl,$ur)";

                var pAe  = ins.Parameters.Add("$ae",   SqliteType.Integer);
                var pPe  = ins.Parameters.Add("$pe",   SqliteType.Integer);
                var pPln = ins.Parameters.Add("$pln",  SqliteType.Integer);
                var pOe  = ins.Parameters.Add("$oe",   SqliteType.Integer);
                var pOl  = ins.Parameters.Add("$ol",   SqliteType.Integer);
                var pIc  = ins.Parameters.Add("$ic",   SqliteType.Text);
                var pWh  = ins.Parameters.Add("$wh",   SqliteType.Text);
                var pBa  = ins.Parameters.Add("$ba",   SqliteType.Integer);
                var pBc  = ins.Parameters.Add("$bc",   SqliteType.Text);
                var pPq  = ins.Parameters.Add("$pq",   SqliteType.Real);
                var pRq  = ins.Parameters.Add("$rq",   SqliteType.Real);
                var pOq  = ins.Parameters.Add("$oq",   SqliteType.Real);
                var pPln2= ins.Parameters.Add("$pln2", SqliteType.Text);
                var pPls = ins.Parameters.Add("$pls",  SqliteType.Text);
                var pSc  = ins.Parameters.Add("$sc",   SqliteType.Integer);
                var pSn  = ins.Parameters.Add("$sn",   SqliteType.Text);
                var pZr  = ins.Parameters.Add("$zr",   SqliteType.Text);
                var pDl  = ins.Parameters.Add("$dl",   SqliteType.Text);
                var pUr  = ins.Parameters.Add("$ur",   SqliteType.Text);

                foreach (var b in bins)
                {
                    pAe.Value  = b.AbsEntry;
                    pPe.Value  = b.PickEntry;
                    pPln.Value = b.Pkl2LinNum;
                    pOe.Value  = b.OrderEntry;
                    pOl.Value  = b.OrderLine;
                    pIc.Value  = b.ItemCode;
                    pWh.Value  = b.WhsCode;
                    pBa.Value  = b.BinAbsEntry;
                    pBc.Value  = b.BinCode;
                    pPq.Value  = (double)b.PickQtty;
                    pRq.Value  = (double)b.RelQtty;
                    pOq.Value  = (double)b.OpenCreQty;
                    pPln2.Value= b.PickListName;
                    pPls.Value = b.PickListStatus;
                    pSc.Value  = b.SlpCode.HasValue ? (object)b.SlpCode.Value : DBNull.Value;
                    pSn.Value  = b.SlpName;
                    pZr.Value  = (object?)b.ZoneRef ?? DBNull.Value;
                    pDl.Value  = (object?)b.DeliveryLocation ?? DBNull.Value;
                    pUr.Value  = (object?)b.U_ReplitId ?? DBNull.Value;
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
