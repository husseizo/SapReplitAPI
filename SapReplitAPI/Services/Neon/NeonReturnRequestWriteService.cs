using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Returns;

namespace SapReplitAPI.Services.Neon;

/// <summary>
/// Targeted Neon (PostgreSQL) write for ORRR + RRR1 sync.
/// Called by ReturnRequestEventHandler on ORRR A/U/C events.
///
/// Operations:
/// 1. UPSERT ReturnRequests header (DocEntry, DocNum, CardCode, DocStatus, CANCELED, etc.)
/// 2. DELETE existing ReturnRequestLines for this ORRR
/// 3. INSERT fresh RRR1 lines (BaseType, BaseEntry, BaseLine, Quantity, OpenQty, LineStatus)
/// 4. Trigger Neon view/computation to update InvoiceLines.PendingReturnQty
///
/// Atomicity: All operations in single transaction. On error, full rollback.
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
        ReturnRequestDto header,
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // 1. UPSERT ReturnRequests header — ON CONFLICT DocEntry
            const string headerSql = @"
INSERT INTO ""ReturnRequests""
    (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",
     ""DocStatus"",""Canceled"",""DocTotal"",""Comments"",""U_AppRef"",""U_ReplitId"",
     ""UpdatedAtUtc"")
VALUES
    (@de,@dn,@cc,@cn,@dd,@ds,@can,@tot,@com,@ref,@repl,@upd)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum""       = EXCLUDED.""DocNum"",
    ""CardCode""     = EXCLUDED.""CardCode"",
    ""CardName""     = EXCLUDED.""CardName"",
    ""DocDate""      = EXCLUDED.""DocDate"",
    ""DocStatus""    = EXCLUDED.""DocStatus"",
    ""Canceled""     = EXCLUDED.""Canceled"",
    ""DocTotal""     = EXCLUDED.""DocTotal"",
    ""Comments""     = EXCLUDED.""Comments"",
    ""U_AppRef""     = EXCLUDED.""U_AppRef"",
    ""U_ReplitId""   = EXCLUDED.""U_ReplitId"",
    ""UpdatedAtUtc"" = EXCLUDED.""UpdatedAtUtc"";";

            await using (var cmd = new NpgsqlCommand(headerSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@de",   NpgsqlDbType.Integer, header.DocEntry);
                cmd.Parameters.AddWithValue("@dn",   NpgsqlDbType.Integer, header.DocNum);
                cmd.Parameters.AddWithValue("@cc",   NpgsqlDbType.Text,    header.CardCode ?? "");
                cmd.Parameters.AddWithValue("@cn",   NpgsqlDbType.Text,    header.CardName ?? "");
                cmd.Parameters.AddWithValue("@dd",   NpgsqlDbType.Date,    header.DocDate);
                cmd.Parameters.AddWithValue("@ds",   NpgsqlDbType.Text,    header.DocStatus ?? "");
                cmd.Parameters.AddWithValue("@can",  NpgsqlDbType.Text,    header.Canceled ?? "N");
                cmd.Parameters.AddWithValue("@tot",  NpgsqlDbType.Numeric, header.DocTotal);
                cmd.Parameters.AddWithValue("@com",  NpgsqlDbType.Text,    header.Comments ?? "");
                cmd.Parameters.AddWithValue("@ref",  NpgsqlDbType.Text,    (object?)header.U_AppRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@repl", NpgsqlDbType.Text,    (object?)header.U_ReplitId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@upd",  NpgsqlDbType.Timestamp, DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. DELETE existing lines for this DocEntry (targeted — no TRUNCATE)
            await using (var del = new NpgsqlCommand(
                @"DELETE FROM ""ReturnRequestLines"" WHERE ""DocEntry"" = @de", conn, tx))
            {
                del.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, header.DocEntry);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 3. INSERT fresh RRR1 lines
            const string lineSql = @"
INSERT INTO ""ReturnRequestLines""
    (""DocEntry"",""LineNum"",""BaseType"",""BaseEntry"",""BaseLine"",
     ""ItemCode"",""Dscription"",""Quantity"",""OpenQty"",""WhsCode"",""LineStatus"",
     ""UpdatedAtUtc"")
VALUES
    (@de,@ln,@bt,@be,@bl,@ic,@dsc,@qty,@oqty,@whs,@ls,@upd)";

            foreach (var line in header.Lines)
            {
                await using var ins = new NpgsqlCommand(lineSql, conn, tx);
                ins.Parameters.AddWithValue("@de",   NpgsqlDbType.Integer,  header.DocEntry);
                ins.Parameters.AddWithValue("@ln",   NpgsqlDbType.Integer,  line.LineNum);
                ins.Parameters.AddWithValue("@bt",   NpgsqlDbType.Integer,  line.BaseType);
                ins.Parameters.AddWithValue("@be",   NpgsqlDbType.Integer,  line.BaseEntry);
                ins.Parameters.AddWithValue("@bl",   NpgsqlDbType.Integer,  line.BaseLine);
                ins.Parameters.AddWithValue("@ic",   NpgsqlDbType.Text,     line.ItemCode ?? "");
                ins.Parameters.AddWithValue("@dsc",  NpgsqlDbType.Text,     line.Dscription ?? "");
                ins.Parameters.AddWithValue("@qty",  NpgsqlDbType.Numeric,  line.Quantity);
                ins.Parameters.AddWithValue("@oqty", NpgsqlDbType.Numeric,  line.OpenQty);
                ins.Parameters.AddWithValue("@whs",  NpgsqlDbType.Text,     line.WhsCode ?? "");
                ins.Parameters.AddWithValue("@ls",   NpgsqlDbType.Text,     line.LineStatus ?? "O");
                ins.Parameters.AddWithValue("@upd",  NpgsqlDbType.Timestamp, DateTime.UtcNow);
                await ins.ExecuteNonQueryAsync(ct);
            }

            // 4. Recalculate PendingReturnQty on all affected InvoiceLines (BaseType=13 only)
            var affectedInvoices = header.Lines
                .Where(l => l.BaseType == 13 && l.BaseEntry > 0)
                .Select(l => l.BaseEntry)
                .Distinct();

            const string recalcSql = @"
UPDATE ""InvoiceLines""
SET ""PendingReturnQty"" = (
    SELECT COALESCE(SUM(rr.""OpenQty""), 0)
    FROM ""ReturnRequestLines"" rr
    JOIN ""ReturnRequests"" rh ON rr.""DocEntry"" = rh.""DocEntry""
    WHERE rr.""BaseEntry"" = ""InvoiceLines"".""DocEntry""
      AND rr.""BaseLine""  = ""InvoiceLines"".""LineNum""
      AND rr.""BaseType""  = 13
      AND rh.""Canceled"" = 'N'
      AND rh.""DocStatus"" = 'O'
      AND rr.""LineStatus"" = 'O'
)
WHERE ""DocEntry"" = @invDocEntry";

            foreach (var invDocEntry in affectedInvoices)
            {
                await using var upd = new NpgsqlCommand(recalcSql, conn, tx);
                upd.Parameters.AddWithValue("@invDocEntry", NpgsqlDbType.Integer, invDocEntry);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _log.LogDebug("[NeonReturnRequest] Upserted DocEntry={DocEntry} Lines={Lines} AffectedInvoices={Inv}",
                header.DocEntry, header.Lines.Count, affectedInvoices.Count());
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _log.LogError(ex, "[NeonReturnRequest] UpsertReturnRequestAsync failed for DocEntry={DocEntry} — rolled back.",
                header.DocEntry);
            throw;
        }
    }
}
