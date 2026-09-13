using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Neon;

/// <summary>
/// Targeted Neon (PostgreSQL) write for a single ORIN: UPSERT header + replace lines.
/// Called by CreditMemoEventHandler on 14/A, 14/U, 14/C events.
/// No TRUNCATE is ever issued; only the target DocEntry rows are touched.
/// NeonSyncJob remains the reconciliation fallback.
/// </summary>
public sealed class NeonCreditMemoWriteService
{
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonCreditMemoWriteService> _log;

    public NeonCreditMemoWriteService(NeonDbContext neon, ILogger<NeonCreditMemoWriteService> log)
    {
        _neon = neon;
        _log  = log;
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

    public async Task UpsertCreditMemoAsync(
        CachedCreditMemo header,
        IReadOnlyList<CachedCreditMemoLine> lines,
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // 1. UPSERT header — ON CONFLICT DocEntry
            const string headerSql = @"
INSERT INTO ""CreditMemoHeaders""
    (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",""DocDueDate"",
     ""DocStatus"",""Canceled"",""DocTotal"",""Comments"",""SlpCode"",""SlpName"",
     ""U_AppRef"",""CreateDate"",""UpdateDate"")
VALUES
    (@de,@dn,@cc,@cn,@dd,@ddd,@ds,@can,@tot,@com,@slp,@slpn,@ref,@cd,@ud)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum""     = EXCLUDED.""DocNum"",
    ""CardCode""   = EXCLUDED.""CardCode"",
    ""CardName""   = EXCLUDED.""CardName"",
    ""DocDate""    = EXCLUDED.""DocDate"",
    ""DocDueDate"" = EXCLUDED.""DocDueDate"",
    ""DocStatus""  = EXCLUDED.""DocStatus"",
    ""Canceled""   = EXCLUDED.""Canceled"",
    ""DocTotal""   = EXCLUDED.""DocTotal"",
    ""Comments""   = EXCLUDED.""Comments"",
    ""SlpCode""    = EXCLUDED.""SlpCode"",
    ""SlpName""    = EXCLUDED.""SlpName"",
    ""U_AppRef""   = EXCLUDED.""U_AppRef"",
    ""CreateDate"" = EXCLUDED.""CreateDate"",
    ""UpdateDate"" = EXCLUDED.""UpdateDate"";";

            await using (var cmd = new NpgsqlCommand(headerSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@de",   NpgsqlDbType.Integer, header.DocEntry);
                cmd.Parameters.AddWithValue("@dn",   NpgsqlDbType.Integer, header.DocNum);
                cmd.Parameters.AddWithValue("@cc",   NpgsqlDbType.Text,    header.CardCode);
                cmd.Parameters.AddWithValue("@cn",   NpgsqlDbType.Text,    header.CardName);
                cmd.Parameters.AddWithValue("@dd",   NpgsqlDbType.Date,    header.DocDate);
                cmd.Parameters.AddWithValue("@ddd",  NpgsqlDbType.Date,    header.DocDueDate);
                cmd.Parameters.AddWithValue("@ds",   NpgsqlDbType.Text,    header.DocStatus);
                cmd.Parameters.AddWithValue("@can",  NpgsqlDbType.Text,    header.Canceled);
                cmd.Parameters.AddWithValue("@tot",  NpgsqlDbType.Numeric, header.DocTotal);
                cmd.Parameters.AddWithValue("@com",  NpgsqlDbType.Text,    header.Comments);
                cmd.Parameters.AddWithValue("@slp",  NpgsqlDbType.Integer, header.SlpCode);
                cmd.Parameters.AddWithValue("@slpn", NpgsqlDbType.Text,    header.SlpName);
                cmd.Parameters.AddWithValue("@ref",  NpgsqlDbType.Text,    (object?)header.U_AppRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cd",   NpgsqlDbType.Date,    header.CreateDate);
                cmd.Parameters.AddWithValue("@ud",   NpgsqlDbType.Date,    header.UpdateDate);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. DELETE existing lines for this DocEntry (targeted — no TRUNCATE)
            await using (var del = new NpgsqlCommand(
                @"DELETE FROM ""CreditMemoLines"" WHERE ""DocEntry"" = @de", conn, tx))
            {
                del.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, header.DocEntry);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 3. INSERT fresh lines (including resolved InvoiceDocEntry/InvoiceLineNum)
            const string lineSql = @"
INSERT INTO ""CreditMemoLines""
    (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",
     ""WhsCode"",""BaseType"",""BaseEntry"",""BaseLine"",""InvoiceDocEntry"",""InvoiceLineNum"")
VALUES
    (@de,@ln,@ic,@dsc,@qty,@prc,@tot,@whs,@bt,@be,@bl,@ide,@iln)";

            foreach (var l in lines)
            {
                await using var ins = new NpgsqlCommand(lineSql, conn, tx);
                ins.Parameters.AddWithValue("@de",  NpgsqlDbType.Integer, l.DocEntry);
                ins.Parameters.AddWithValue("@ln",  NpgsqlDbType.Integer, l.LineNum);
                ins.Parameters.AddWithValue("@ic",  NpgsqlDbType.Text,    l.ItemCode);
                ins.Parameters.AddWithValue("@dsc", NpgsqlDbType.Text,    l.Dscription);
                ins.Parameters.AddWithValue("@qty", NpgsqlDbType.Numeric, l.Quantity);
                ins.Parameters.AddWithValue("@prc", NpgsqlDbType.Numeric, l.Price);
                ins.Parameters.AddWithValue("@tot", NpgsqlDbType.Numeric, l.LineTotal);
                ins.Parameters.AddWithValue("@whs", NpgsqlDbType.Text,    l.WhsCode);
                ins.Parameters.AddWithValue("@bt",  NpgsqlDbType.Integer, l.BaseType);
                ins.Parameters.AddWithValue("@be",  NpgsqlDbType.Integer, l.BaseEntry);
                ins.Parameters.AddWithValue("@bl",  NpgsqlDbType.Integer, l.BaseLine);
                ins.Parameters.AddWithValue("@ide", NpgsqlDbType.Integer, (object?)l.InvoiceDocEntry ?? DBNull.Value);
                ins.Parameters.AddWithValue("@iln", NpgsqlDbType.Integer, (object?)l.InvoiceLineNum  ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }

            // 4. Recalculate ReturnedQty on all affected InvoiceLines (idempotent)
            var affectedInvoices = lines
                .Where(l => l.InvoiceDocEntry.HasValue)
                .Select(l => l.InvoiceDocEntry!.Value)
                .Distinct();

            const string recalcSql = @"
UPDATE ""InvoiceLines""
SET ""ReturnedQty"" = (
    SELECT COALESCE(SUM(cl.""Quantity""), 0)
    FROM ""CreditMemoLines"" cl
    JOIN ""CreditMemoHeaders"" ch ON cl.""DocEntry"" = ch.""DocEntry""
    WHERE cl.""InvoiceDocEntry"" = ""InvoiceLines"".""DocEntry""
      AND cl.""InvoiceLineNum""  = ""InvoiceLines"".""LineNum""
      AND ch.""Canceled"" = 'N'
)
WHERE ""DocEntry"" = @invDocEntry";

            foreach (var invDocEntry in affectedInvoices)
            {
                await using var upd = new NpgsqlCommand(recalcSql, conn, tx);
                upd.Parameters.AddWithValue("@invDocEntry", NpgsqlDbType.Integer, invDocEntry);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _log.LogDebug("[NeonCreditMemo] Upserted DocEntry={DocEntry} Lines={Lines} AffectedInvoices={Inv}",
                header.DocEntry, lines.Count, affectedInvoices.Count());
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _log.LogError(ex, "[NeonCreditMemo] UpsertCreditMemoAsync failed for DocEntry={DocEntry} — rolled back.",
                header.DocEntry);
            throw;
        }
    }
}
