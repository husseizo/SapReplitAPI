using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.CachedServices;

/// <summary>
/// Targeted SQLite write for the Credit Memo (ORIN/RIN1) cache.
/// Called by CreditMemoEventHandler on 14/A, 14/U, 14/C events.
/// Idempotent: UPSERT header, DELETE+INSERT lines — no duplicates.
/// Never TRUNCATEs; never advances SyncMetadata.
/// NeonSyncJob remains a reconciliation fallback only.
/// </summary>
public sealed class CreditMemoCacheService
{
    private readonly CacheDbContext _db;
    private readonly ILogger<CreditMemoCacheService> _log;

    private static readonly SemaphoreSlim _lock = new(1, 1);

    public CreditMemoCacheService(CacheDbContext db, ILogger<CreditMemoCacheService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task RefreshSingleAsync(
        CachedCreditMemo header,
        IReadOnlyList<CachedCreditMemoLine> lines,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var tx = await _db.Database.BeginTransactionAsync(ct);
            var sq    = (SqliteConnection)conn;
            var sqTx  = (SqliteTransaction)tx.GetDbTransaction();

            try
            {
                // 1. UPSERT header
                using (var cmd = sq.CreateCommand())
                {
                    cmd.Transaction = sqTx;
                    cmd.CommandText = @"
INSERT INTO CreditMemoHeaders
(DocEntry,DocNum,CardCode,CardName,DocDate,DocDueDate,DocStatus,Canceled,
 DocTotal,Comments,SlpCode,SlpName,U_AppRef,CreateDate,UpdateDate)
VALUES
($de,$dn,$cc,$cn,$dd,$ddd,$ds,$can,$tot,$com,$slp,$slpn,$ref,$cd,$ud)
ON CONFLICT(DocEntry) DO UPDATE SET
 DocNum=excluded.DocNum, CardCode=excluded.CardCode, CardName=excluded.CardName,
 DocDate=excluded.DocDate, DocDueDate=excluded.DocDueDate,
 DocStatus=excluded.DocStatus, Canceled=excluded.Canceled,
 DocTotal=excluded.DocTotal, Comments=excluded.Comments,
 SlpCode=excluded.SlpCode, SlpName=excluded.SlpName,
 U_AppRef=excluded.U_AppRef, CreateDate=excluded.CreateDate,
 UpdateDate=excluded.UpdateDate";

                    cmd.Parameters.AddWithValue("$de",   header.DocEntry);
                    cmd.Parameters.AddWithValue("$dn",   header.DocNum);
                    cmd.Parameters.AddWithValue("$cc",   header.CardCode);
                    cmd.Parameters.AddWithValue("$cn",   header.CardName);
                    cmd.Parameters.AddWithValue("$dd",   header.DocDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$ddd",  header.DocDueDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$ds",   header.DocStatus);
                    cmd.Parameters.AddWithValue("$can",  header.Canceled);
                    cmd.Parameters.AddWithValue("$tot",  header.DocTotal);
                    cmd.Parameters.AddWithValue("$com",  header.Comments);
                    cmd.Parameters.AddWithValue("$slp",  header.SlpCode);
                    cmd.Parameters.AddWithValue("$slpn", header.SlpName);
                    cmd.Parameters.AddWithValue("$ref",  (object?)header.U_AppRef ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$cd",   header.CreateDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$ud",   header.UpdateDate.ToString("yyyy-MM-dd"));
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 2. DELETE existing lines for this DocEntry
                using (var del = sq.CreateCommand())
                {
                    del.Transaction  = sqTx;
                    del.CommandText  = "DELETE FROM CreditMemoLines WHERE DocEntry=$de";
                    del.Parameters.AddWithValue("$de", header.DocEntry);
                    await del.ExecuteNonQueryAsync(ct);
                }

                // 3. INSERT fresh lines (including resolved InvoiceDocEntry/InvoiceLineNum)
                foreach (var l in lines)
                {
                    using var ins = sq.CreateCommand();
                    ins.Transaction = sqTx;
                    ins.CommandText = @"
INSERT INTO CreditMemoLines
(DocEntry,LineNum,ItemCode,Dscription,Quantity,Price,LineTotal,WhsCode,BaseType,BaseEntry,BaseLine,InvoiceDocEntry,InvoiceLineNum)
VALUES
($de,$ln,$ic,$dsc,$qty,$prc,$tot,$whs,$bt,$be,$bl,$ide,$iln)";
                    ins.Parameters.AddWithValue("$de",  l.DocEntry);
                    ins.Parameters.AddWithValue("$ln",  l.LineNum);
                    ins.Parameters.AddWithValue("$ic",  l.ItemCode);
                    ins.Parameters.AddWithValue("$dsc", l.Dscription);
                    ins.Parameters.AddWithValue("$qty", l.Quantity);
                    ins.Parameters.AddWithValue("$prc", l.Price);
                    ins.Parameters.AddWithValue("$tot", l.LineTotal);
                    ins.Parameters.AddWithValue("$whs", l.WhsCode);
                    ins.Parameters.AddWithValue("$bt",  l.BaseType);
                    ins.Parameters.AddWithValue("$be",  l.BaseEntry);
                    ins.Parameters.AddWithValue("$bl",  l.BaseLine);
                    ins.Parameters.AddWithValue("$ide", (object?)l.InvoiceDocEntry ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$iln", (object?)l.InvoiceLineNum  ?? DBNull.Value);
                    await ins.ExecuteNonQueryAsync(ct);
                }

                // 4. Recalculate ReturnedQty on all affected InvoiceLines (idempotent)
                //    Runs inside the same transaction so the recalc sees the fresh CM lines just inserted.
                var affectedInvoices = lines
                    .Where(l => l.InvoiceDocEntry.HasValue)
                    .Select(l => l.InvoiceDocEntry!.Value)
                    .Distinct();

                foreach (var invDocEntry in affectedInvoices)
                {
                    using var upd = sq.CreateCommand();
                    upd.Transaction = sqTx;
                    upd.CommandText = @"
UPDATE InvoiceLines
SET ReturnedQty = (
    SELECT COALESCE(SUM(cl.Quantity), 0)
    FROM CreditMemoLines cl
    JOIN CreditMemoHeaders ch ON cl.DocEntry = ch.DocEntry
    WHERE cl.InvoiceDocEntry = InvoiceLines.DocEntry
      AND cl.InvoiceLineNum  = InvoiceLines.LineNum
      AND ch.Canceled = 'N'
)
WHERE DocEntry = $invDocEntry";
                    upd.Parameters.AddWithValue("$invDocEntry", invDocEntry);
                    await upd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                _log.LogDebug("[CreditMemoCache] SQLite refreshed DocEntry={DocEntry} Lines={Lines} AffectedInvoices={Inv}",
                    header.DocEntry, lines.Count, affectedInvoices.Count());
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
