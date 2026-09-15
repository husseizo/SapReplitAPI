using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Returns;

namespace SapReplitAPI.Services.CachedServices;

/// <summary>
/// Targeted SQLite write for one ORRR snapshot. Idempotent UPSERT header + replace lines.
/// Recomputes InvoiceLines.PendingReturnQty for all affected invoice DocEntry values.
/// </summary>
public sealed class ReturnRequestCacheService
{
    private readonly CacheDbContext _db;
    private readonly ILogger<ReturnRequestCacheService> _log;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public ReturnRequestCacheService(CacheDbContext db, ILogger<ReturnRequestCacheService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task RefreshSingleAsync(
        CachedReturnRequest header,
        IReadOnlyList<CachedReturnRequestLine> lines,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var sqliteConn = (SqliteConnection)conn;
            var sqliteTx = (SqliteTransaction)tx.GetDbTransaction();

            try
            {
                var affectedInvoiceDocEntries = new HashSet<int>();

                // Existing references for this ORRR doc (needed so updates/removals recalc old rows too).
                using (var readOld = sqliteConn.CreateCommand())
                {
                    readOld.Transaction = sqliteTx;
                    readOld.CommandText = @"
SELECT DISTINCT BaseEntry
FROM ReturnRequestLines
WHERE DocEntry = $de AND BaseType = 13 AND BaseEntry > 0";
                    readOld.Parameters.AddWithValue("$de", header.DocEntry);
                    using var rdr = await readOld.ExecuteReaderAsync(ct);
                    while (await rdr.ReadAsync(ct))
                        affectedInvoiceDocEntries.Add(rdr.GetInt32(0));
                }

                // 1) UPSERT header
                using (var cmd = sqliteConn.CreateCommand())
                {
                    cmd.Transaction = sqliteTx;
                    cmd.CommandText = @"
INSERT INTO ReturnRequests
(DocEntry,DocNum,CardCode,CardName,DocDate,DocStatus,Canceled,DocTotal,U_AppRef,U_ReplitId,Comments)
VALUES
($de,$dn,$cc,$cn,$dd,$ds,$can,$tot,$ref,$rid,$com)
ON CONFLICT(DocEntry) DO UPDATE SET
 DocNum=excluded.DocNum, CardCode=excluded.CardCode, CardName=excluded.CardName,
 DocDate=excluded.DocDate, DocStatus=excluded.DocStatus, Canceled=excluded.Canceled,
 DocTotal=excluded.DocTotal, U_AppRef=excluded.U_AppRef, U_ReplitId=excluded.U_ReplitId,
 Comments=excluded.Comments";
                    cmd.Parameters.AddWithValue("$de", header.DocEntry);
                    cmd.Parameters.AddWithValue("$dn", header.DocNum);
                    cmd.Parameters.AddWithValue("$cc", header.CardCode);
                    cmd.Parameters.AddWithValue("$cn", header.CardName);
                    cmd.Parameters.AddWithValue("$dd", header.DocDate.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$ds", header.DocStatus);
                    cmd.Parameters.AddWithValue("$can", header.Canceled);
                    cmd.Parameters.AddWithValue("$tot", header.DocTotal);
                    cmd.Parameters.AddWithValue("$ref", (object?)header.U_AppRef ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$rid", (object?)header.U_ReplitId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$com", header.Comments);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 2) Replace lines for this ORRR doc
                using (var del = sqliteConn.CreateCommand())
                {
                    del.Transaction = sqliteTx;
                    del.CommandText = "DELETE FROM ReturnRequestLines WHERE DocEntry=$de";
                    del.Parameters.AddWithValue("$de", header.DocEntry);
                    await del.ExecuteNonQueryAsync(ct);
                }

                foreach (var l in lines)
                {
                    if (l.BaseType == 13 && l.BaseEntry > 0)
                        affectedInvoiceDocEntries.Add(l.BaseEntry);

                    using var ins = sqliteConn.CreateCommand();
                    ins.Transaction = sqliteTx;
                    ins.CommandText = @"
INSERT INTO ReturnRequestLines
(DocEntry,LineNum,BaseType,BaseEntry,BaseLine,ItemCode,Dscription,Quantity,OpenQty,WhsCode,LineStatus)
VALUES
($de,$ln,$bt,$be,$bl,$ic,$dsc,$qty,$oq,$whs,$ls)";
                    ins.Parameters.AddWithValue("$de", l.DocEntry);
                    ins.Parameters.AddWithValue("$ln", l.LineNum);
                    ins.Parameters.AddWithValue("$bt", l.BaseType);
                    ins.Parameters.AddWithValue("$be", l.BaseEntry);
                    ins.Parameters.AddWithValue("$bl", l.BaseLine);
                    ins.Parameters.AddWithValue("$ic", l.ItemCode);
                    ins.Parameters.AddWithValue("$dsc", l.Dscription);
                    ins.Parameters.AddWithValue("$qty", l.Quantity);
                    ins.Parameters.AddWithValue("$oq", l.OpenQty);
                    ins.Parameters.AddWithValue("$whs", l.WhsCode);
                    ins.Parameters.AddWithValue("$ls", l.LineStatus);
                    await ins.ExecuteNonQueryAsync(ct);
                }

                await PendingReturnQuantityMirror.RecomputeForInvoiceDocEntriesAsync(
                    sqliteConn, sqliteTx, affectedInvoiceDocEntries.ToList(), ct);

                await tx.CommitAsync(ct);
                _log.LogDebug("[ReturnRequestCache] SQLite refreshed DocEntry={DocEntry} Lines={Lines} AffectedInvoices={Inv}",
                    header.DocEntry, lines.Count, affectedInvoiceDocEntries.Count);
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
