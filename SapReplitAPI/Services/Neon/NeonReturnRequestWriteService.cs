using Npgsql;
using NpgsqlTypes;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Returns;

namespace SapReplitAPI.Services.Neon;

/// <summary>
/// Targeted Neon write for one ORRR snapshot. Idempotent UPSERT header + replace lines,
/// followed by authoritative PendingReturnQty recomputation on affected invoice lines.
/// </summary>
public sealed class NeonReturnRequestWriteService
{
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonReturnRequestWriteService> _log;

    public NeonReturnRequestWriteService(NeonDbContext neon, ILogger<NeonReturnRequestWriteService> log)
    {
        _neon = neon;
        _log = log;
    }

    private async Task<NpgsqlConnection> GetConnectionAsync(CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State == System.Data.ConnectionState.Broken)
            await conn.CloseAsync();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        return conn;
    }

    public async Task UpsertReturnRequestAsync(
        CachedReturnRequest header,
        IReadOnlyList<CachedReturnRequestLine> lines,
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var affectedInvoiceDocEntries = new HashSet<int>();

            await using (var readOld = new NpgsqlCommand(@"
SELECT DISTINCT ""BaseEntry""
FROM ""ReturnRequestLines""
WHERE ""DocEntry"" = @de AND ""BaseType"" = 13 AND ""BaseEntry"" > 0", conn, tx))
            {
                readOld.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, header.DocEntry);
                await using var rdr = await readOld.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    affectedInvoiceDocEntries.Add(rdr.GetInt32(0));
            }

            const string headerSql = @"
INSERT INTO ""ReturnRequests""
    (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",""DocStatus"",""Canceled"",""DocTotal"",""U_AppRef"",""U_ReplitId"",""Comments"")
VALUES
    (@de,@dn,@cc,@cn,@dd,@ds,@can,@tot,@ref,@rid,@com)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum"" = EXCLUDED.""DocNum"",
    ""CardCode"" = EXCLUDED.""CardCode"",
    ""CardName"" = EXCLUDED.""CardName"",
    ""DocDate"" = EXCLUDED.""DocDate"",
    ""DocStatus"" = EXCLUDED.""DocStatus"",
    ""Canceled"" = EXCLUDED.""Canceled"",
    ""DocTotal"" = EXCLUDED.""DocTotal"",
    ""U_AppRef"" = EXCLUDED.""U_AppRef"",
    ""U_ReplitId"" = EXCLUDED.""U_ReplitId"",
    ""Comments"" = EXCLUDED.""Comments"";";

            await using (var cmd = new NpgsqlCommand(headerSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, header.DocEntry);
                cmd.Parameters.AddWithValue("@dn", NpgsqlDbType.Integer, header.DocNum);
                cmd.Parameters.AddWithValue("@cc", NpgsqlDbType.Text, header.CardCode);
                cmd.Parameters.AddWithValue("@cn", NpgsqlDbType.Text, header.CardName);
                cmd.Parameters.AddWithValue("@dd", NpgsqlDbType.Date, header.DocDate);
                cmd.Parameters.AddWithValue("@ds", NpgsqlDbType.Text, header.DocStatus);
                cmd.Parameters.AddWithValue("@can", NpgsqlDbType.Text, header.Canceled);
                cmd.Parameters.AddWithValue("@tot", NpgsqlDbType.Numeric, header.DocTotal);
                cmd.Parameters.AddWithValue("@ref", NpgsqlDbType.Text, (object?)header.U_AppRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rid", NpgsqlDbType.Text, (object?)header.U_ReplitId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@com", NpgsqlDbType.Text, header.Comments);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var del = new NpgsqlCommand(
                @"DELETE FROM ""ReturnRequestLines"" WHERE ""DocEntry"" = @de", conn, tx))
            {
                del.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, header.DocEntry);
                await del.ExecuteNonQueryAsync(ct);
            }

            const string lineSql = @"
INSERT INTO ""ReturnRequestLines""
    (""DocEntry"",""LineNum"",""BaseType"",""BaseEntry"",""BaseLine"",""ItemCode"",""Dscription"",""Quantity"",""OpenQty"",""WhsCode"",""LineStatus"")
VALUES
    (@de,@ln,@bt,@be,@bl,@ic,@dsc,@qty,@oq,@whs,@ls)";

            foreach (var l in lines)
            {
                if (l.BaseType == 13 && l.BaseEntry > 0)
                    affectedInvoiceDocEntries.Add(l.BaseEntry);

                await using var ins = new NpgsqlCommand(lineSql, conn, tx);
                ins.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, l.DocEntry);
                ins.Parameters.AddWithValue("@ln", NpgsqlDbType.Integer, l.LineNum);
                ins.Parameters.AddWithValue("@bt", NpgsqlDbType.Integer, l.BaseType);
                ins.Parameters.AddWithValue("@be", NpgsqlDbType.Integer, l.BaseEntry);
                ins.Parameters.AddWithValue("@bl", NpgsqlDbType.Integer, l.BaseLine);
                ins.Parameters.AddWithValue("@ic", NpgsqlDbType.Text, l.ItemCode);
                ins.Parameters.AddWithValue("@dsc", NpgsqlDbType.Text, l.Dscription);
                ins.Parameters.AddWithValue("@qty", NpgsqlDbType.Numeric, l.Quantity);
                ins.Parameters.AddWithValue("@oq", NpgsqlDbType.Numeric, l.OpenQty);
                ins.Parameters.AddWithValue("@whs", NpgsqlDbType.Text, l.WhsCode);
                ins.Parameters.AddWithValue("@ls", NpgsqlDbType.Text, l.LineStatus);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await PendingReturnQuantityMirror.RecomputeForInvoiceDocEntriesAsync(
                conn, tx, affectedInvoiceDocEntries.ToList(), ct);

            await tx.CommitAsync(ct);
            _log.LogDebug("[NeonReturnRequest] Upserted DocEntry={DocEntry} Lines={Lines} AffectedInvoices={Inv}",
                header.DocEntry, lines.Count, affectedInvoiceDocEntries.Count);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _log.LogError(ex, "[NeonReturnRequest] UpsertReturnRequestAsync failed for DocEntry={DocEntry}", header.DocEntry);
            throw;
        }
    }
}
