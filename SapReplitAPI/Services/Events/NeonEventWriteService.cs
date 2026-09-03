using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Targeted Neon (PostgreSQL) writes for the OutboxPoller event pipeline.
/// Each method wraps its work in a single NpgsqlTransaction; exceptions trigger ROLLBACK.
///
/// IMPORTANT: These methods MUST NOT write SyncMetadata["Invoice"], SyncMetadata["InvoicePayment"],
/// NeonMirror:Invoices, or NeonMirror:InvoicePayments. Those keys are owned by NeonSyncJob.
/// The natural idempotency contract: if an event handler writes data, the next NeonSyncJob run
/// finds the SQLite watermark already updated by DeltaSyncJob and re-upserts the same row
/// (idempotent on conflict keys), leaving Neon consistent.
/// </summary>
public sealed class NeonEventWriteService
{
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonEventWriteService> _logger;

    public NeonEventWriteService(NeonDbContext neon, ILogger<NeonEventWriteService> logger)
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
    /// UPSERT a single invoice header + replace its lines in Neon — all in one tx.
    /// Lines are deleted then re-inserted (same idempotent pattern as NeonSyncJob full reconcile
    /// but scoped to one DocEntry, so surrounding lines in Neon are unaffected).
    /// </summary>
    public async Task UpsertInvoiceAsync(
        CachedInvoice header,
        IEnumerable<CachedInvoiceLine> lines,
        CancellationToken ct = default)
    {
        var lineList = lines.ToList();
        var conn     = await GetConnectionAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) UPSERT header (19 cols, ON CONFLICT DocEntry)
            const string headerSql = @"
INSERT INTO ""Invoices""
    (""DocEntry"",""DocNum"",""InvoiceDocNum"",""DocDate"",""DocStatus"",""Canceled"",
     ""CardCode"",""CardName"",""DocTotal"",""PaidToDate"",""BalanceDue"",""DaysOverdue"",
     ""SalesEmployeeCode"",""SalesEmployeeName"",""GroupNum"",""DocStatusDisplay"",
     ""ZoneRef"",""U_ReplitId"",""DeliveryLocation"")
VALUES
    (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum""            = EXCLUDED.""DocNum"",
    ""InvoiceDocNum""     = EXCLUDED.""InvoiceDocNum"",
    ""DocDate""           = EXCLUDED.""DocDate"",
    ""DocStatus""         = EXCLUDED.""DocStatus"",
    ""Canceled""          = EXCLUDED.""Canceled"",
    ""CardCode""          = EXCLUDED.""CardCode"",
    ""CardName""          = EXCLUDED.""CardName"",
    ""DocTotal""          = EXCLUDED.""DocTotal"",
    ""PaidToDate""        = EXCLUDED.""PaidToDate"",
    ""BalanceDue""        = EXCLUDED.""BalanceDue"",
    ""DaysOverdue""       = EXCLUDED.""DaysOverdue"",
    ""SalesEmployeeCode"" = EXCLUDED.""SalesEmployeeCode"",
    ""SalesEmployeeName"" = EXCLUDED.""SalesEmployeeName"",
    ""GroupNum""          = EXCLUDED.""GroupNum"",
    ""DocStatusDisplay""  = EXCLUDED.""DocStatusDisplay"",
    ""ZoneRef""           = EXCLUDED.""ZoneRef"",
    ""U_ReplitId""        = EXCLUDED.""U_ReplitId"",
    ""DeliveryLocation""  = EXCLUDED.""DeliveryLocation"";";

            await using (var cmd = new NpgsqlCommand(headerSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@p0",  NpgsqlDbType.Integer, header.DocEntry);
                cmd.Parameters.AddWithValue("@p1",  NpgsqlDbType.Integer, header.DocNum);
                cmd.Parameters.AddWithValue("@p2",  NpgsqlDbType.Integer, header.InvoiceDocNum);
                cmd.Parameters.AddWithValue("@p3",  NpgsqlDbType.Date,    header.DocDate);
                cmd.Parameters.AddWithValue("@p4",  NpgsqlDbType.Text,    header.DocStatus         ?? "");
                cmd.Parameters.AddWithValue("@p5",  NpgsqlDbType.Text,    header.Canceled          ?? "");
                cmd.Parameters.AddWithValue("@p6",  NpgsqlDbType.Text,    header.CardCode          ?? "");
                cmd.Parameters.AddWithValue("@p7",  NpgsqlDbType.Text,    header.CardName          ?? "");
                cmd.Parameters.AddWithValue("@p8",  NpgsqlDbType.Numeric, header.DocTotal);
                cmd.Parameters.AddWithValue("@p9",  NpgsqlDbType.Numeric, header.PaidToDate);
                cmd.Parameters.AddWithValue("@p10", NpgsqlDbType.Numeric, header.BalanceDue);
                cmd.Parameters.AddWithValue("@p11", NpgsqlDbType.Integer, header.DaysOverdue);
                cmd.Parameters.AddWithValue("@p12", NpgsqlDbType.Integer, header.SalesEmployeeCode);
                cmd.Parameters.AddWithValue("@p13", NpgsqlDbType.Text,    header.SalesEmployeeName ?? "");
                cmd.Parameters.AddWithValue("@p14", NpgsqlDbType.Integer, header.GroupNum);
                cmd.Parameters.AddWithValue("@p15", NpgsqlDbType.Text,    header.DocStatusDisplay  ?? "");
                cmd.Parameters.AddWithValue("@p16", NpgsqlDbType.Text,    (object?)header.ZoneRef          ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p17", NpgsqlDbType.Text,    (object?)header.U_ReplitId       ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p18", NpgsqlDbType.Text,    (object?)header.DeliveryLocation ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2) DELETE existing lines for this DocEntry
            await using (var delCmd = new NpgsqlCommand(
                @"DELETE FROM ""InvoiceLines"" WHERE ""DocEntry"" = @docEntry", conn, tx))
            {
                delCmd.Parameters.AddWithValue("@docEntry", NpgsqlDbType.Integer, header.DocEntry);
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            // 3) INSERT new lines (no ON CONFLICT — lines deleted above)
            foreach (var l in lineList)
            {
                const string lineSql = @"
INSERT INTO ""InvoiceLines""
    (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",
     ""U_Item_Name"",""U_ItemName"",""U_MdlTEST"",""U_MDLTsT"",""U_Manufacturer"")
VALUES
    (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)";

                await using var lineCmd = new NpgsqlCommand(lineSql, conn, tx);
                lineCmd.Parameters.AddWithValue("@p0",  NpgsqlDbType.Integer, l.DocEntry);
                lineCmd.Parameters.AddWithValue("@p1",  NpgsqlDbType.Integer, l.LineNum);
                lineCmd.Parameters.AddWithValue("@p2",  NpgsqlDbType.Text,    l.ItemCode       ?? "");
                lineCmd.Parameters.AddWithValue("@p3",  NpgsqlDbType.Text,    l.Dscription     ?? "");
                lineCmd.Parameters.AddWithValue("@p4",  NpgsqlDbType.Numeric, l.Quantity);
                lineCmd.Parameters.AddWithValue("@p5",  NpgsqlDbType.Numeric, l.Price);
                lineCmd.Parameters.AddWithValue("@p6",  NpgsqlDbType.Numeric, l.LineTotal);
                lineCmd.Parameters.AddWithValue("@p7",  NpgsqlDbType.Text,    l.U_Item_Name    ?? "");
                lineCmd.Parameters.AddWithValue("@p8",  NpgsqlDbType.Text,    l.U_ItemName     ?? "");
                lineCmd.Parameters.AddWithValue("@p9",  NpgsqlDbType.Text,    l.U_MdlTEST      ?? "");
                lineCmd.Parameters.AddWithValue("@p10", NpgsqlDbType.Text,    l.U_MDLTsT       ?? "");
                lineCmd.Parameters.AddWithValue("@p11", NpgsqlDbType.Text,    l.U_Manufacturer ?? "");
                await lineCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _logger.LogError(ex, "[NeonEventWrite] UpsertInvoiceAsync failed for DocEntry={DocEntry} — rolled back.",
                header.DocEntry);
            throw;
        }
    }

    /// <summary>
    /// For a single paymentDocEntry: UPSERT current payment rows, then DELETE stale rows.
    /// All in one Neon tx; ROLLBACK on any failure.
    /// Empty currentDocEntries → DELETE ALL rows for this paymentDocEntry (zero-RCT2 case).
    /// </summary>
    public async Task UpsertPaymentsAsync(
        IEnumerable<CachedInvoicePayment> currentPayments,
        int paymentDocEntry,
        IEnumerable<int> currentDocEntries,
        CancellationToken ct = default)
    {
        var current     = currentPayments.ToList();
        var docEntries  = currentDocEntries.Distinct().ToList();
        var conn        = await GetConnectionAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // 1) UPSERT current rows (18 cols, ON CONFLICT (DocEntry, PaymentDocEntry))
            const string upsertSql = @"
INSERT INTO ""InvoicePayments""
    (""DocEntry"",""PaymentDocEntry"",""PaymentNumber"",""InvoiceDocNum"",""PaymentDate"",
     ""CardCode"",""CardName"",""AmountApplied"",""BankTransferAmount"",""BankTransferReference"",
     ""DebitAccountCode"",""DebitAccountName"",""SalesEmployeeCode"",""SalesEmployeeName"",
     ""ClientReference"",""Canceled"",""CounterRef"",""LastUpdated"")
VALUES
    (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17)
ON CONFLICT (""DocEntry"",""PaymentDocEntry"") DO UPDATE SET
    ""PaymentNumber""         = EXCLUDED.""PaymentNumber"",
    ""InvoiceDocNum""         = EXCLUDED.""InvoiceDocNum"",
    ""PaymentDate""           = EXCLUDED.""PaymentDate"",
    ""CardCode""              = EXCLUDED.""CardCode"",
    ""CardName""              = EXCLUDED.""CardName"",
    ""AmountApplied""         = EXCLUDED.""AmountApplied"",
    ""BankTransferAmount""    = EXCLUDED.""BankTransferAmount"",
    ""BankTransferReference"" = EXCLUDED.""BankTransferReference"",
    ""DebitAccountCode""      = EXCLUDED.""DebitAccountCode"",
    ""DebitAccountName""      = EXCLUDED.""DebitAccountName"",
    ""SalesEmployeeCode""     = EXCLUDED.""SalesEmployeeCode"",
    ""SalesEmployeeName""     = EXCLUDED.""SalesEmployeeName"",
    ""ClientReference""       = EXCLUDED.""ClientReference"",
    ""Canceled""              = EXCLUDED.""Canceled"",
    ""CounterRef""            = EXCLUDED.""CounterRef"",
    ""LastUpdated""           = EXCLUDED.""LastUpdated"";";

            foreach (var p in current)
            {
                await using var upsCmd = new NpgsqlCommand(upsertSql, conn, tx);
                upsCmd.Parameters.AddWithValue("@p0",  NpgsqlDbType.Integer,   p.DocEntry);
                upsCmd.Parameters.AddWithValue("@p1",  NpgsqlDbType.Integer,   p.PaymentDocEntry);
                upsCmd.Parameters.AddWithValue("@p2",  NpgsqlDbType.Integer,   p.PaymentNumber);
                upsCmd.Parameters.AddWithValue("@p3",  NpgsqlDbType.Integer,   p.InvoiceDocNum);
                upsCmd.Parameters.AddWithValue("@p4",  NpgsqlDbType.Date,      p.PaymentDate);
                upsCmd.Parameters.AddWithValue("@p5",  NpgsqlDbType.Text,      p.CardCode              ?? "");
                upsCmd.Parameters.AddWithValue("@p6",  NpgsqlDbType.Text,      p.CardName              ?? "");
                upsCmd.Parameters.AddWithValue("@p7",  NpgsqlDbType.Numeric,   p.AmountApplied);
                upsCmd.Parameters.AddWithValue("@p8",  NpgsqlDbType.Numeric,   p.BankTransferAmount);
                upsCmd.Parameters.AddWithValue("@p9",  NpgsqlDbType.Text,      p.BankTransferReference ?? "");
                upsCmd.Parameters.AddWithValue("@p10", NpgsqlDbType.Text,      p.DebitAccountCode      ?? "");
                upsCmd.Parameters.AddWithValue("@p11", NpgsqlDbType.Text,      p.DebitAccountName      ?? "");
                upsCmd.Parameters.AddWithValue("@p12", NpgsqlDbType.Text,      p.SalesEmployeeCode     ?? "");
                upsCmd.Parameters.AddWithValue("@p13", NpgsqlDbType.Text,      p.SalesEmployeeName     ?? "");
                upsCmd.Parameters.AddWithValue("@p14", NpgsqlDbType.Text,      p.ClientReference       ?? "");
                upsCmd.Parameters.AddWithValue("@p15", NpgsqlDbType.Boolean,   p.Canceled);
                upsCmd.Parameters.AddWithValue("@p16", NpgsqlDbType.Text,      p.CounterRef            ?? "");
                upsCmd.Parameters.AddWithValue("@p17", NpgsqlDbType.Timestamp, DateTime.SpecifyKind(p.LastUpdated, DateTimeKind.Unspecified));
                await upsCmd.ExecuteNonQueryAsync(ct);
            }

            // 2) DELETE stale rows (empty-set branch)
            string deleteSql;
            if (docEntries.Count == 0)
            {
                deleteSql = @"DELETE FROM ""InvoicePayments"" WHERE ""PaymentDocEntry"" = @paymentDocEntry";
            }
            else
            {
                // Parameterised NOT IN — safe because docEntries are trusted internal integers
                var placeholders = string.Join(",", Enumerable.Range(0, docEntries.Count).Select(i => $"@de{i}"));
                deleteSql = $@"
DELETE FROM ""InvoicePayments""
WHERE ""PaymentDocEntry"" = @paymentDocEntry
  AND ""DocEntry"" NOT IN ({placeholders})";
            }

            await using (var delCmd = new NpgsqlCommand(deleteSql, conn, tx))
            {
                delCmd.Parameters.AddWithValue("@paymentDocEntry", NpgsqlDbType.Integer, paymentDocEntry);
                for (int i = 0; i < docEntries.Count; i++)
                    delCmd.Parameters.AddWithValue($"@de{i}", NpgsqlDbType.Integer, docEntries[i]);
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _logger.LogError(ex,
                "[NeonEventWrite] UpsertPaymentsAsync failed for PaymentDocEntry={PaymentDocEntry} — rolled back.",
                paymentDocEntry);
            throw;
        }
    }
}
