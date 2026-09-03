using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.CachedServices;

/// <summary>
/// Targeted SQLite writes for the Delivery cache.
/// Never advances SyncMetadata["Delivery"] — that is owned by DeliveryDeltaSyncJob/DeliveryFullSyncJob.
/// </summary>
public class DeliveryCacheService
{
    private readonly CacheDbContext _db;
    private readonly ILogger<DeliveryCacheService> _log;

    public DeliveryCacheService(CacheDbContext db, ILogger<DeliveryCacheService> log)
    {
        _db  = db;
        _log = log;
    }

    // Upsert header + DELETE all existing lines + INSERT fresh lines — one atomic SQLite transaction.
    public async Task UpsertDeliveryAsync(CachedDelivery delivery, CancellationToken ct = default)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var tx = await _db.Database.BeginTransactionAsync(ct);
        var sq = (SqliteConnection)conn;
        var sqTx = (SqliteTransaction)tx.GetDbTransaction();

        try
        {
            // 1. UPSERT header
            using (var cmd = sq.CreateCommand())
            {
                cmd.Transaction = sqTx;
                cmd.CommandText = @"
INSERT INTO Deliveries
(DocEntry,DocNum,DocDate,DocDueDate,TaxDate,DocStatus,Canceled,CardCode,CardName,
 DocTotal,DocCur,SlpCode,SlpName,UserSign,Comments,
 CreateDate,CreateTS,UpdateDate,UpdateTS,BPLId,U_ReplitId,DocStatusDisplay,
 ZoneRef,DeliveryLocation)
VALUES
($de,$dn,$dd,$ddd,$td,$ds,$can,$cc,$cn,$tot,$cur,$slp,$slpn,$us,$com,
 $cd,$cts,$ud,$uts,$bpl,$rid,$disp,$zr,$dloc)
ON CONFLICT(DocEntry) DO UPDATE SET
 DocNum=excluded.DocNum, DocDate=excluded.DocDate, DocDueDate=excluded.DocDueDate,
 TaxDate=excluded.TaxDate, DocStatus=excluded.DocStatus, Canceled=excluded.Canceled,
 CardCode=excluded.CardCode, CardName=excluded.CardName, DocTotal=excluded.DocTotal,
 DocCur=excluded.DocCur, SlpCode=excluded.SlpCode, SlpName=excluded.SlpName,
 UserSign=excluded.UserSign, Comments=excluded.Comments,
 CreateDate=excluded.CreateDate, CreateTS=excluded.CreateTS,
 UpdateDate=excluded.UpdateDate, UpdateTS=excluded.UpdateTS,
 BPLId=excluded.BPLId, U_ReplitId=excluded.U_ReplitId,
 DocStatusDisplay=excluded.DocStatusDisplay,
 ZoneRef=excluded.ZoneRef, DeliveryLocation=excluded.DeliveryLocation";

                cmd.Parameters.AddWithValue("$de",   delivery.DocEntry);
                cmd.Parameters.AddWithValue("$dn",   delivery.DocNum);
                cmd.Parameters.AddWithValue("$dd",   delivery.DocDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$ddd",  delivery.DocDueDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$td",   delivery.TaxDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$ds",   delivery.DocStatus);
                cmd.Parameters.AddWithValue("$can",  delivery.Canceled);
                cmd.Parameters.AddWithValue("$cc",   delivery.CardCode);
                cmd.Parameters.AddWithValue("$cn",   delivery.CardName);
                cmd.Parameters.AddWithValue("$tot",  delivery.DocTotal);
                cmd.Parameters.AddWithValue("$cur",  delivery.DocCur);
                cmd.Parameters.AddWithValue("$slp",  delivery.SlpCode);
                cmd.Parameters.AddWithValue("$slpn", delivery.SlpName);
                cmd.Parameters.AddWithValue("$us",   delivery.UserSign);
                cmd.Parameters.AddWithValue("$com",  delivery.Comments);
                cmd.Parameters.AddWithValue("$cd",   delivery.CreateDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$cts",  delivery.CreateTS);
                cmd.Parameters.AddWithValue("$ud",   delivery.UpdateDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$uts",  delivery.UpdateTS);
                cmd.Parameters.AddWithValue("$bpl",  delivery.BPLId);
                cmd.Parameters.AddWithValue("$rid",  (object?)delivery.U_ReplitId       ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$disp", delivery.DocStatusDisplay);
                cmd.Parameters.AddWithValue("$zr",   (object?)delivery.ZoneRef          ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dloc", (object?)delivery.DeliveryLocation  ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. DELETE existing lines
            using (var del = sq.CreateCommand())
            {
                del.Transaction = sqTx;
                del.CommandText = "DELETE FROM DeliveryLines WHERE DocEntry=$de";
                del.Parameters.AddWithValue("$de", delivery.DocEntry);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 3. INSERT fresh lines
            if (delivery.Lines.Count > 0)
            {
                using var ins = sq.CreateCommand();
                ins.Transaction = sqTx;
                ins.CommandText = @"
INSERT INTO DeliveryLines
(DocEntry,LineNum,ItemCode,Dscription,Quantity,OpenQty,WhsCode,Price,LineTotal,
 Currency,BaseType,BaseEntry,BaseLine,TargetType,TrgetEntry)
VALUES($de,$ln,$ic,$dsc,$qty,$oq,$whs,$pr,$lt,$cur,$bt,$be,$bl,$tt,$te)";

                var pDe  = ins.Parameters.Add("$de",  SqliteType.Integer);
                var pLn  = ins.Parameters.Add("$ln",  SqliteType.Integer);
                var pIc  = ins.Parameters.Add("$ic",  SqliteType.Text);
                var pDsc = ins.Parameters.Add("$dsc", SqliteType.Text);
                var pQty = ins.Parameters.Add("$qty", SqliteType.Real);
                var pOq  = ins.Parameters.Add("$oq",  SqliteType.Real);
                var pWhs = ins.Parameters.Add("$whs", SqliteType.Text);
                var pPr  = ins.Parameters.Add("$pr",  SqliteType.Real);
                var pLt  = ins.Parameters.Add("$lt",  SqliteType.Real);
                var pCur = ins.Parameters.Add("$cur", SqliteType.Text);
                var pBt  = ins.Parameters.Add("$bt",  SqliteType.Integer);
                var pBe  = ins.Parameters.Add("$be",  SqliteType.Integer);
                var pBl  = ins.Parameters.Add("$bl",  SqliteType.Integer);
                var pTt  = ins.Parameters.Add("$tt",  SqliteType.Integer);
                var pTe  = ins.Parameters.Add("$te",  SqliteType.Integer);

                foreach (var l in delivery.Lines)
                {
                    pDe.Value  = delivery.DocEntry;
                    pLn.Value  = l.LineNum;
                    pIc.Value  = l.ItemCode;
                    pDsc.Value = l.Dscription;
                    pQty.Value = (double)l.Quantity;
                    pOq.Value  = (double)l.OpenQty;
                    pWhs.Value = l.WhsCode;
                    pPr.Value  = (double)l.Price;
                    pLt.Value  = (double)l.LineTotal;
                    pCur.Value = l.Currency;
                    pBt.Value  = l.BaseType;
                    pBe.Value  = l.BaseEntry;
                    pBl.Value  = l.BaseLine;
                    pTt.Value  = l.TargetType;
                    pTe.Value  = l.TrgetEntry;
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

    public async Task<CachedDelivery?> GetByDocEntryAsync(int docEntry, CancellationToken ct = default)
    {
        var header = await _db.Deliveries.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocEntry == docEntry, ct);
        if (header == null) return null;

        header.Lines = await _db.DeliveryLines.AsNoTracking()
            .Where(l => l.DocEntry == docEntry)
            .OrderBy(l => l.LineNum)
            .ToListAsync(ct);

        return header;
    }

    public async Task<List<CachedDelivery>> GetDeliveriesAsync(
        string? status = null,
        string? canceled = null,
        string? cardCode = null,
        string? customer = null,
        int? slpCode = null,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        var q = _db.Deliveries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))   q = q.Where(d => d.DocStatus == status);
        if (!string.IsNullOrWhiteSpace(canceled))  q = q.Where(d => d.Canceled == canceled);
        if (!string.IsNullOrWhiteSpace(cardCode))  q = q.Where(d => d.CardCode == cardCode);
        if (!string.IsNullOrWhiteSpace(customer))  q = q.Where(d => d.CardName.Contains(customer));
        if (slpCode.HasValue)                      q = q.Where(d => d.SlpCode == slpCode.Value);
        if (from.HasValue)                         q = q.Where(d => d.DocDate >= from.Value);
        if (to.HasValue)                           q = q.Where(d => d.DocDate <= to.Value);
        return await q.OrderByDescending(d => d.DocEntry).ToListAsync(ct);
    }

    public async Task<List<CachedDelivery>> GetOpenDeliveriesAsync(CancellationToken ct = default)
        => await _db.Deliveries.AsNoTracking()
            .Where(d => d.DocStatus == "O" && d.Canceled == "N")
            .OrderByDescending(d => d.DocEntry)
            .ToListAsync(ct);

    public async Task<List<CachedDeliveryLine>> GetLinesAsync(int docEntry, CancellationToken ct = default)
        => await _db.DeliveryLines.AsNoTracking()
            .Where(l => l.DocEntry == docEntry)
            .OrderBy(l => l.LineNum)
            .ToListAsync(ct);
}
