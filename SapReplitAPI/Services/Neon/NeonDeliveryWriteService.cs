using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Neon;

/// <summary>
/// Targeted Neon (PostgreSQL) write for a single delivery: UPSERT header + replace lines.
/// Called from event handlers immediately after SQLite write — delivers Neon freshness in
/// the same event-driven fast path, without waiting for NeonSyncJob (up to 3-min lag).
/// NeonSyncJob remains the reconciliation fallback and still runs on schedule.
/// </summary>
public sealed class NeonDeliveryWriteService
{
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonDeliveryWriteService> _logger;

    public NeonDeliveryWriteService(NeonDbContext neon, ILogger<NeonDeliveryWriteService> logger)
    {
        _neon   = neon;
        _logger = logger;
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

    /// <summary>
    /// UPSERT the delivery header and replace all lines in Neon — one transaction.
    /// Lines are deleted then re-inserted so OpenQty and other mutable fields
    /// always reflect the freshest SAP state without requiring ON CONFLICT on lines.
    /// </summary>
    public async Task UpsertDeliveryAsync(CachedDelivery delivery, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // 1. UPSERT header (22 cols, ON CONFLICT DocEntry)
            const string headerSql = @"
INSERT INTO ""Deliveries""
    (""DocEntry"",""DocNum"",""DocDate"",""DocDueDate"",""TaxDate"",
     ""DocStatus"",""Canceled"",""CardCode"",""CardName"",""DocTotal"",
     ""DocCur"",""SlpCode"",""SlpName"",""UserSign"",""Comments"",
     ""CreateDate"",""CreateTS"",""UpdateDate"",""UpdateTS"",""BPLId"",
     ""U_ReplitId"",""DocStatusDisplay"",""ZoneRef"",""DeliveryLocation"")
VALUES
    (@de,@dn,@dd,@ddd,@td,@ds,@can,@cc,@cn,@tot,@cur,@slp,@slpn,@us,@com,
     @cd,@cts,@ud,@uts,@bpl,@rid,@disp,@zr,@dloc)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum""           = EXCLUDED.""DocNum"",
    ""DocDate""          = EXCLUDED.""DocDate"",
    ""DocDueDate""       = EXCLUDED.""DocDueDate"",
    ""TaxDate""          = EXCLUDED.""TaxDate"",
    ""DocStatus""        = EXCLUDED.""DocStatus"",
    ""Canceled""         = EXCLUDED.""Canceled"",
    ""CardCode""         = EXCLUDED.""CardCode"",
    ""CardName""         = EXCLUDED.""CardName"",
    ""DocTotal""         = EXCLUDED.""DocTotal"",
    ""DocCur""           = EXCLUDED.""DocCur"",
    ""SlpCode""          = EXCLUDED.""SlpCode"",
    ""SlpName""          = EXCLUDED.""SlpName"",
    ""UserSign""         = EXCLUDED.""UserSign"",
    ""Comments""         = EXCLUDED.""Comments"",
    ""CreateDate""       = EXCLUDED.""CreateDate"",
    ""CreateTS""         = EXCLUDED.""CreateTS"",
    ""UpdateDate""       = EXCLUDED.""UpdateDate"",
    ""UpdateTS""         = EXCLUDED.""UpdateTS"",
    ""BPLId""            = EXCLUDED.""BPLId"",
    ""U_ReplitId""       = EXCLUDED.""U_ReplitId"",
    ""DocStatusDisplay"" = EXCLUDED.""DocStatusDisplay"",
    ""ZoneRef""          = EXCLUDED.""ZoneRef"",
    ""DeliveryLocation"" = EXCLUDED.""DeliveryLocation""";

            using (var cmd = new NpgsqlCommand(headerSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@de",   NpgsqlDbType.Integer, delivery.DocEntry);
                cmd.Parameters.AddWithValue("@dn",   NpgsqlDbType.Integer, delivery.DocNum);
                cmd.Parameters.AddWithValue("@dd",   NpgsqlDbType.Date,    delivery.DocDate);
                cmd.Parameters.AddWithValue("@ddd",  NpgsqlDbType.Date,    delivery.DocDueDate);
                cmd.Parameters.AddWithValue("@td",   NpgsqlDbType.Date,    delivery.TaxDate);
                cmd.Parameters.AddWithValue("@ds",   NpgsqlDbType.Text,    delivery.DocStatus   ?? "");
                cmd.Parameters.AddWithValue("@can",  NpgsqlDbType.Text,    delivery.Canceled    ?? "");
                cmd.Parameters.AddWithValue("@cc",   NpgsqlDbType.Text,    delivery.CardCode    ?? "");
                cmd.Parameters.AddWithValue("@cn",   NpgsqlDbType.Text,    delivery.CardName    ?? "");
                cmd.Parameters.AddWithValue("@tot",  NpgsqlDbType.Numeric, delivery.DocTotal);
                cmd.Parameters.AddWithValue("@cur",  NpgsqlDbType.Text,    delivery.DocCur      ?? "");
                cmd.Parameters.AddWithValue("@slp",  NpgsqlDbType.Integer, delivery.SlpCode);
                cmd.Parameters.AddWithValue("@slpn", NpgsqlDbType.Text,    delivery.SlpName     ?? "");
                cmd.Parameters.AddWithValue("@us",   NpgsqlDbType.Integer, delivery.UserSign);
                cmd.Parameters.AddWithValue("@com",  NpgsqlDbType.Text,    (object?)delivery.Comments ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cd",   NpgsqlDbType.Date,    delivery.CreateDate);
                cmd.Parameters.AddWithValue("@cts",  NpgsqlDbType.Integer, delivery.CreateTS);
                cmd.Parameters.AddWithValue("@ud",   NpgsqlDbType.Date,    delivery.UpdateDate);
                cmd.Parameters.AddWithValue("@uts",  NpgsqlDbType.Integer, delivery.UpdateTS);
                cmd.Parameters.AddWithValue("@bpl",  NpgsqlDbType.Integer, delivery.BPLId);
                cmd.Parameters.AddWithValue("@rid",  NpgsqlDbType.Text, (object?)delivery.U_ReplitId       ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@disp", NpgsqlDbType.Text, delivery.DocStatusDisplay           ?? "");
                cmd.Parameters.AddWithValue("@zr",   NpgsqlDbType.Text, (object?)delivery.ZoneRef           ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@dloc", NpgsqlDbType.Text, (object?)delivery.DeliveryLocation  ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. DELETE existing lines for this DocEntry
            using (var del = new NpgsqlCommand(
                @"DELETE FROM ""DeliveryLines"" WHERE ""DocEntry"" = @de", conn, tx))
            {
                del.Parameters.AddWithValue("@de", NpgsqlDbType.Integer, delivery.DocEntry);
                await del.ExecuteNonQueryAsync(ct);
            }

            // 3. INSERT fresh lines
            const string lineSql = @"
INSERT INTO ""DeliveryLines""
    (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""OpenQty"",
     ""WhsCode"",""Price"",""LineTotal"",""Currency"",""BaseType"",""BaseEntry"",
     ""BaseLine"",""TargetType"",""TrgetEntry"")
VALUES
    (@de,@ln,@ic,@dsc,@qty,@oq,@whs,@pr,@lt,@cur,@bt,@be,@bl,@tt,@te)";

            foreach (var l in delivery.Lines)
            {
                using var ins = new NpgsqlCommand(lineSql, conn, tx);
                ins.Parameters.AddWithValue("@de",  NpgsqlDbType.Integer, l.DocEntry);
                ins.Parameters.AddWithValue("@ln",  NpgsqlDbType.Integer, l.LineNum);
                ins.Parameters.AddWithValue("@ic",  NpgsqlDbType.Text,    l.ItemCode   ?? "");
                ins.Parameters.AddWithValue("@dsc", NpgsqlDbType.Text,    l.Dscription ?? "");
                ins.Parameters.AddWithValue("@qty", NpgsqlDbType.Numeric, l.Quantity);
                ins.Parameters.AddWithValue("@oq",  NpgsqlDbType.Numeric, l.OpenQty);
                ins.Parameters.AddWithValue("@whs", NpgsqlDbType.Text,    l.WhsCode    ?? "");
                ins.Parameters.AddWithValue("@pr",  NpgsqlDbType.Numeric, l.Price);
                ins.Parameters.AddWithValue("@lt",  NpgsqlDbType.Numeric, l.LineTotal);
                ins.Parameters.AddWithValue("@cur", NpgsqlDbType.Text,    l.Currency   ?? "");
                ins.Parameters.AddWithValue("@bt",  NpgsqlDbType.Integer, l.BaseType);
                ins.Parameters.AddWithValue("@be",  NpgsqlDbType.Integer, l.BaseEntry);
                ins.Parameters.AddWithValue("@bl",  NpgsqlDbType.Integer, l.BaseLine);
                ins.Parameters.AddWithValue("@tt",  NpgsqlDbType.Integer, l.TargetType);
                ins.Parameters.AddWithValue("@te",  NpgsqlDbType.Integer, l.TrgetEntry);
                await ins.ExecuteNonQueryAsync(ct);
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
