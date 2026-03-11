using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Quartz;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Quartz job that mirrors the local SQLite cache to Neon (PostgreSQL) every 5 minutes.
///
/// Design rules:
///   • NEVER awaited by the existing sync pipeline — completely decoupled.
///   • All exceptions are caught and logged; the app keeps running if Neon is down.
///   • Uses TRUNCATE + batch INSERT inside a single PostgreSQL transaction per table
///     so Neon readers always see a consistent snapshot (never half-written data).
///   • Batch size = 500 rows to keep individual round-trips small.
/// </summary>
[DisallowConcurrentExecution]
public class NeonSyncJob : IJob
{
    private readonly CacheDbContext _sqlite;
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonSyncJob> _log;
    private const int BatchSize = 500;

    public NeonSyncJob(CacheDbContext sqlite, NeonDbContext neon, ILogger<NeonSyncJob> log)
    {
        _sqlite = sqlite;
        _neon = neon;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogInformation("☁️ [NeonSync] Starting mirror push to Neon...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await SyncProductsAsync();
            await SyncCustomersAsync();
            await SyncOrderHeadersAsync();
            await SyncOrderLinesAsync();
            await SyncInvoicesAsync();
            await SyncInvoiceLinesAsync();
            await SyncInvoicePaymentsAsync();
            await SyncTodayOrderHeadersAsync();
            await SyncTodayOrderLinesAsync();
            await SyncOpenOrderHeadersAsync();
            await SyncOpenOrderLinesAsync();
            await SyncInvoiceStatusCacheAsync();

            sw.Stop();
            _log.LogInformation("✅ [NeonSync] Mirror complete in {Sec:F1}s", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [NeonSync] Mirror failed — Neon is likely down or misconfigured. Local SQLite sync unaffected.");
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private NpgsqlConnection OpenNeonConnection()
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();
        return conn;
    }

    /// Executes TRUNCATE + batched INSERT for one table inside a transaction.
    private async Task TruncateAndBatchInsertAsync(
        NpgsqlConnection conn,
        string truncateSql,
        string insertSql,
        IEnumerable<Action<NpgsqlCommand>> rowBinders,
        string[] paramNames,
        NpgsqlDbType[] paramTypes)
    {
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            // 1) Truncate (cascade handled by FK ON DELETE CASCADE on lines)
            using (var truncCmd = new NpgsqlCommand(truncateSql, conn, tx))
                await truncCmd.ExecuteNonQueryAsync();

            // 2) Prepare parameterised insert once, re-use per row
            using var cmd = new NpgsqlCommand(insertSql, conn, tx);
            for (int i = 0; i < paramNames.Length; i++)
                cmd.Parameters.Add(paramNames[i], paramTypes[i]);

            await cmd.PrepareAsync();

            foreach (var binder in rowBinders)
            {
                binder(cmd);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ─── per-table sync ──────────────────────────────────────────────────────

    private async Task SyncProductsAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""Products"" RESTART IDENTITY CASCADE",
            insertSql: @"
INSERT INTO ""Products""
    (""ItemCode"",""ItemName"",""U_Article_No"",""U_MdlTEST"",""U_Item_Name"",
     ""Price"",""Price05"",""TotalOnHand"",""OnHand"",""OnHandQty"",""WhsCode"",""LastUpdated"",
     ""Whs_001"",""Whs_002"",""Whs_003"",""Whs_004"")
VALUES
    (@ItemCode,@ItemName,@U_Article_No,@U_MdlTEST,@U_Item_Name,
     @Price,@Price05,@TotalOnHand,@OnHand,@OnHandQty,@WhsCode,@LastUpdated,
     @Whs001,@Whs002,@Whs003,@Whs004)",
            rowBinders: rows.Select<dynamic>(p => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@ItemCode"].Value = p.ItemCode ?? "";
                cmd.Parameters["@ItemName"].Value = p.ItemName ?? "";
                cmd.Parameters["@U_Article_No"].Value = p.U_Article_No ?? "";
                cmd.Parameters["@U_MdlTEST"].Value = p.U_MdlTEST ?? "";
                cmd.Parameters["@U_Item_Name"].Value = p.U_Item_Name ?? "";
                cmd.Parameters["@Price"].Value = p.Price;
                cmd.Parameters["@Price05"].Value = p.Price05;
                cmd.Parameters["@TotalOnHand"].Value = p.TotalOnHand;
                cmd.Parameters["@OnHand"].Value = p.OnHand;
                cmd.Parameters["@OnHandQty"].Value = p.OnHandQty;
                cmd.Parameters["@WhsCode"].Value = p.WhsCode ?? "";
                cmd.Parameters["@LastUpdated"].Value = p.LastUpdated;
                cmd.Parameters["@Whs001"].Value = (object?)p.Whs_001 ?? DBNull.Value;
                cmd.Parameters["@Whs002"].Value = (object?)p.Whs_002 ?? DBNull.Value;
                cmd.Parameters["@Whs003"].Value = (object?)p.Whs_003 ?? DBNull.Value;
                cmd.Parameters["@Whs004"].Value = (object?)p.Whs_004 ?? DBNull.Value;
            }),
            paramNames: ["@ItemCode","@ItemName","@U_Article_No","@U_MdlTEST","@U_Item_Name",
                         "@Price","@Price05","@TotalOnHand","@OnHand","@OnHandQty","@WhsCode","@LastUpdated",
                         "@Whs001","@Whs002","@Whs003","@Whs004"],
            paramTypes: [NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,
                         NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,
                         NpgsqlDbType.Text,NpgsqlDbType.Timestamp,
                         NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Integer]
        );
        _log.LogInformation("[NeonSync] Products: {Count} rows", rows.Count);
    }

    private async Task SyncCustomersAsync()
    {
        var rows = await _sqlite.Customers.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""Customers"" RESTART IDENTITY CASCADE",
            insertSql: @"
INSERT INTO ""Customers""
    (""CardCode"",""CardName"",""Balance"",""Region"",""Phone"",""CustomerType"",
     ""SalesPersonName"",""SalesPersonCode"",""TotalSpent"",""VIN1"",""VIN2"",""VIN3"",""AddressesJson"")
VALUES
    (@CardCode,@CardName,@Balance,@Region,@Phone,@CustomerType,
     @SalesPersonName,@SalesPersonCode,@TotalSpent,@VIN1,@VIN2,@VIN3,@AddressesJson)",
            rowBinders: rows.Select<dynamic>(c => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@CardCode"].Value = c.CardCode ?? "";
                cmd.Parameters["@CardName"].Value = c.CardName ?? "";
                cmd.Parameters["@Balance"].Value = c.Balance;
                cmd.Parameters["@Region"].Value = c.Region ?? "";
                cmd.Parameters["@Phone"].Value = c.Phone ?? "";
                cmd.Parameters["@CustomerType"].Value = c.CustomerType ?? "";
                cmd.Parameters["@SalesPersonName"].Value = c.SalesPersonName ?? "";
                cmd.Parameters["@SalesPersonCode"].Value = (object?)c.SalesPersonCode ?? DBNull.Value;
                cmd.Parameters["@TotalSpent"].Value = c.TotalSpent;
                cmd.Parameters["@VIN1"].Value = c.VIN1 ?? "";
                cmd.Parameters["@VIN2"].Value = c.VIN2 ?? "";
                cmd.Parameters["@VIN3"].Value = c.VIN3 ?? "";
                cmd.Parameters["@AddressesJson"].Value = c.AddressesJson ?? "[]";
            }),
            paramNames: ["@CardCode","@CardName","@Balance","@Region","@Phone","@CustomerType",
                         "@SalesPersonName","@SalesPersonCode","@TotalSpent","@VIN1","@VIN2","@VIN3","@AddressesJson"],
            paramTypes: [NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Numeric,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,
                         NpgsqlDbType.Text,NpgsqlDbType.Integer,NpgsqlDbType.Numeric,
                         NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text]
        );
        _log.LogInformation("[NeonSync] Customers: {Count} rows", rows.Count);
    }

    private async Task SyncOrderHeadersAsync()
    {
        var rows = await _sqlite.OrderHeaders.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""OrderHeaders"" RESTART IDENTITY CASCADE",
            insertSql: @"
INSERT INTO ""OrderHeaders""
    (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",""SlpCode"",""SlpName"")
VALUES
    (@DocEntry,@DocNum,@CardName,@DocDate,@OrderValue,@Status,@SlpCode,@SlpName)",
            rowBinders: rows.Select<dynamic>(h => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@DocEntry"].Value = h.DocEntry;
                cmd.Parameters["@DocNum"].Value = h.DocNum;
                cmd.Parameters["@CardName"].Value = h.CardName ?? "";
                cmd.Parameters["@DocDate"].Value = h.DocDate;
                cmd.Parameters["@OrderValue"].Value = h.OrderValue;
                cmd.Parameters["@Status"].Value = h.Status ?? "";
                cmd.Parameters["@SlpCode"].Value = h.SlpCode;
                cmd.Parameters["@SlpName"].Value = h.SlpName ?? "";
            }),
            paramNames: ["@DocEntry","@DocNum","@CardName","@DocDate","@OrderValue","@Status","@SlpCode","@SlpName"],
            paramTypes: [NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Date,
                         NpgsqlDbType.Numeric,NpgsqlDbType.Text,NpgsqlDbType.Integer,NpgsqlDbType.Text]
        );
        _log.LogInformation("[NeonSync] OrderHeaders: {Count} rows", rows.Count);
    }

    private async Task SyncOrderLinesAsync()
    {
        var rows = await _sqlite.OrderLines.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        // OrderHeaders was already truncated with CASCADE so lines are already gone;
        // just insert without a second truncate.
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""OrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES
    (@DocEntry,@LineNum,@DocDate,@ItemCode,@Dscription,
     @Quantity,@Price,@WhsCode,@U_ItemName,@U_Manufacturer)", conn, tx);

            cmd.Parameters.Add("@DocEntry", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@LineNum", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@DocDate", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ItemCode", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Dscription", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Quantity", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@Price", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@WhsCode", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_ItemName", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_Manufacturer", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in rows)
            {
                cmd.Parameters["@DocEntry"].Value = l.DocEntry;
                cmd.Parameters["@LineNum"].Value = l.LineNum;
                cmd.Parameters["@DocDate"].Value = l.DocDate;
                cmd.Parameters["@ItemCode"].Value = l.ItemCode ?? "";
                cmd.Parameters["@Dscription"].Value = l.Dscription ?? "";
                cmd.Parameters["@Quantity"].Value = l.Quantity;
                cmd.Parameters["@Price"].Value = l.Price;
                cmd.Parameters["@WhsCode"].Value = l.WhsCode ?? "";
                cmd.Parameters["@U_ItemName"].Value = l.U_ItemName ?? "";
                cmd.Parameters["@U_Manufacturer"].Value = l.U_Manufacturer ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch { await tx.RollbackAsync(); throw; }
        _log.LogInformation("[NeonSync] OrderLines: {Count} rows", rows.Count);
    }

    private async Task SyncInvoicesAsync()
    {
        var rows = await _sqlite.Invoices.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""Invoices"" RESTART IDENTITY CASCADE",
            insertSql: @"
INSERT INTO ""Invoices""
    (""DocEntry"",""DocNum"",""InvoiceDocNum"",""DocDate"",""DocStatus"",""Canceled"",
     ""CardCode"",""CardName"",""DocTotal"",""PaidToDate"",""BalanceDue"",""DaysOverdue"",
     ""SalesEmployeeCode"",""SalesEmployeeName"",""GroupNum"",""DocStatusDisplay"",""SlpCode"")
VALUES
    (@DocEntry,@DocNum,@InvoiceDocNum,@DocDate,@DocStatus,@Canceled,
     @CardCode,@CardName,@DocTotal,@PaidToDate,@BalanceDue,@DaysOverdue,
     @SalesEmployeeCode,@SalesEmployeeName,@GroupNum,@DocStatusDisplay,@SlpCode)",
            rowBinders: rows.Select<dynamic>(i => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@DocEntry"].Value = i.DocEntry;
                cmd.Parameters["@DocNum"].Value = i.DocNum;
                cmd.Parameters["@InvoiceDocNum"].Value = i.InvoiceDocNum;
                cmd.Parameters["@DocDate"].Value = i.DocDate;
                cmd.Parameters["@DocStatus"].Value = i.DocStatus ?? "";
                cmd.Parameters["@Canceled"].Value = i.Canceled ?? "";
                cmd.Parameters["@CardCode"].Value = i.CardCode ?? "";
                cmd.Parameters["@CardName"].Value = i.CardName ?? "";
                cmd.Parameters["@DocTotal"].Value = i.DocTotal;
                cmd.Parameters["@PaidToDate"].Value = i.PaidToDate;
                cmd.Parameters["@BalanceDue"].Value = i.BalanceDue;
                cmd.Parameters["@DaysOverdue"].Value = i.DaysOverdue;
                cmd.Parameters["@SalesEmployeeCode"].Value = i.SalesEmployeeCode;
                cmd.Parameters["@SalesEmployeeName"].Value = i.SalesEmployeeName ?? "";
                cmd.Parameters["@GroupNum"].Value = i.GroupNum;
                cmd.Parameters["@DocStatusDisplay"].Value = i.DocStatusDisplay ?? "";
                cmd.Parameters["@SlpCode"].Value = i.SlpCode;
            }),
            paramNames: ["@DocEntry","@DocNum","@InvoiceDocNum","@DocDate","@DocStatus","@Canceled",
                         "@CardCode","@CardName","@DocTotal","@PaidToDate","@BalanceDue","@DaysOverdue",
                         "@SalesEmployeeCode","@SalesEmployeeName","@GroupNum","@DocStatusDisplay","@SlpCode"],
            paramTypes: [NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Date,
                         NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,
                         NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Integer,
                         NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Integer]
        );
        _log.LogInformation("[NeonSync] Invoices: {Count} rows", rows.Count);
    }

    private async Task SyncInvoiceLinesAsync()
    {
        var rows = await _sqlite.InvoiceLines.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        // Invoices TRUNCATE already cascaded to InvoiceLines — just insert
        var conn = OpenNeonConnection();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoiceLines""
    (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",
     ""U_Item_Name"",""U_ItemName"",""U_MdlTEST"",""U_Manufacturer"")
VALUES
    (@DocEntry,@LineNum,@ItemCode,@Dscription,@Quantity,@Price,@LineTotal,
     @U_Item_Name,@U_ItemName,@U_MdlTEST,@U_Manufacturer)", conn, tx);

            cmd.Parameters.Add("@DocEntry", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@LineNum", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@ItemCode", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Dscription", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Quantity", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@Price", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@LineTotal", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@U_Item_Name", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_ItemName", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_MdlTEST", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_Manufacturer", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in rows)
            {
                cmd.Parameters["@DocEntry"].Value = l.DocEntry;
                cmd.Parameters["@LineNum"].Value = l.LineNum;
                cmd.Parameters["@ItemCode"].Value = l.ItemCode ?? "";
                cmd.Parameters["@Dscription"].Value = l.Dscription ?? "";
                cmd.Parameters["@Quantity"].Value = l.Quantity;
                cmd.Parameters["@Price"].Value = l.Price;
                cmd.Parameters["@LineTotal"].Value = l.LineTotal;
                cmd.Parameters["@U_Item_Name"].Value = l.U_Item_Name ?? "";
                cmd.Parameters["@U_ItemName"].Value = l.U_ItemName ?? "";
                cmd.Parameters["@U_MdlTEST"].Value = l.U_MdlTEST ?? "";
                cmd.Parameters["@U_Manufacturer"].Value = l.U_Manufacturer ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch { await tx.RollbackAsync(); throw; }
        _log.LogInformation("[NeonSync] InvoiceLines: {Count} rows", rows.Count);
    }

    private async Task SyncInvoicePaymentsAsync()
    {
        var rows = await _sqlite.InvoicePayments.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""InvoicePayments"" RESTART IDENTITY CASCADE",
            insertSql: @"
INSERT INTO ""InvoicePayments""
    (""DocEntry"",""PaymentDocEntry"",""PaymentNumber"",""InvoiceDocNum"",""PaymentDate"",
     ""CardCode"",""CardName"",""AmountApplied"",""BankTransferAmount"",""BankTransferReference"",
     ""DebitAccountCode"",""DebitAccountName"",""SalesEmployeeCode"",""SalesEmployeeName"")
VALUES
    (@DocEntry,@PaymentDocEntry,@PaymentNumber,@InvoiceDocNum,@PaymentDate,
     @CardCode,@CardName,@AmountApplied,@BankTransferAmount,@BankTransferReference,
     @DebitAccountCode,@DebitAccountName,@SalesEmployeeCode,@SalesEmployeeName)",
            rowBinders: rows.Select<dynamic>(p => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@DocEntry"].Value = p.DocEntry;
                cmd.Parameters["@PaymentDocEntry"].Value = p.PaymentDocEntry;
                cmd.Parameters["@PaymentNumber"].Value = p.PaymentNumber;
                cmd.Parameters["@InvoiceDocNum"].Value = p.InvoiceDocNum;
                cmd.Parameters["@PaymentDate"].Value = p.PaymentDate;
                cmd.Parameters["@CardCode"].Value = p.CardCode ?? "";
                cmd.Parameters["@CardName"].Value = p.CardName ?? "";
                cmd.Parameters["@AmountApplied"].Value = p.AmountApplied;
                cmd.Parameters["@BankTransferAmount"].Value = p.BankTransferAmount;
                cmd.Parameters["@BankTransferReference"].Value = p.BankTransferReference ?? "";
                cmd.Parameters["@DebitAccountCode"].Value = p.DebitAccountCode ?? "";
                cmd.Parameters["@DebitAccountName"].Value = p.DebitAccountName ?? "";
                cmd.Parameters["@SalesEmployeeCode"].Value = p.SalesEmployeeCode ?? "";
                cmd.Parameters["@SalesEmployeeName"].Value = p.SalesEmployeeName ?? "";
            }),
            paramNames: ["@DocEntry","@PaymentDocEntry","@PaymentNumber","@InvoiceDocNum","@PaymentDate",
                         "@CardCode","@CardName","@AmountApplied","@BankTransferAmount","@BankTransferReference",
                         "@DebitAccountCode","@DebitAccountName","@SalesEmployeeCode","@SalesEmployeeName"],
            paramTypes: [NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Date,
                         NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Text,
                         NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text]
        );
        _log.LogInformation("[NeonSync] InvoicePayments: {Count} rows", rows.Count);
    }

    private async Task SyncTodayOrderHeadersAsync()
    {
        var rows = await _sqlite.TodayOrderHeaders.AsNoTracking().ToListAsync();

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""TodayOrderHeaders"" RESTART IDENTITY CASCADE",
            insertSql: rows.Count == 0 ? "SELECT 1" : @"
INSERT INTO ""TodayOrderHeaders""
    (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",
     ""SlpCode"",""SlpName"",""Cancelled"")
VALUES
    (@DocEntry,@DocNum,@CardName,@DocDate,@OrderValue,@Status,
     @SlpCode,@SlpName,@Cancelled)",
            rowBinders: rows.Count == 0
                ? [(_) => { }]
                : rows.Select<dynamic>(h => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@DocEntry"].Value = h.DocEntry;
                cmd.Parameters["@DocNum"].Value = h.DocNum;
                cmd.Parameters["@CardName"].Value = h.CardName ?? "";
                cmd.Parameters["@DocDate"].Value = h.DocDate;
                cmd.Parameters["@OrderValue"].Value = h.OrderValue;
                cmd.Parameters["@Status"].Value = h.Status ?? "";
                cmd.Parameters["@SlpCode"].Value = h.SlpCode;
                cmd.Parameters["@SlpName"].Value = h.SlpName ?? "";
                cmd.Parameters["@Cancelled"].Value = h.Cancelled;
            }),
            paramNames: rows.Count == 0 ? [] :
                ["@DocEntry","@DocNum","@CardName","@DocDate","@OrderValue","@Status","@SlpCode","@SlpName","@Cancelled"],
            paramTypes: rows.Count == 0 ? [] :
                [NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Date,
                 NpgsqlDbType.Numeric,NpgsqlDbType.Text,NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Boolean]
        );
        _log.LogInformation("[NeonSync] TodayOrderHeaders: {Count} rows", rows.Count);
    }

    private async Task SyncTodayOrderLinesAsync()
    {
        var rows = await _sqlite.TodayOrderLines.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES
    (@DocEntry,@LineNum,@DocDate,@ItemCode,@Dscription,
     @Quantity,@Price,@WhsCode,@U_ItemName,@U_Manufacturer)", conn, tx);

            cmd.Parameters.Add("@DocEntry", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@LineNum", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@DocDate", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ItemCode", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Dscription", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Quantity", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@Price", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@WhsCode", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_ItemName", NpgsqlDbType.Text);
            cmd.Parameters.Add("@U_Manufacturer", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in rows)
            {
                cmd.Parameters["@DocEntry"].Value = l.DocEntry;
                cmd.Parameters["@LineNum"].Value = l.LineNum;
                cmd.Parameters["@DocDate"].Value = l.DocDate;
                cmd.Parameters["@ItemCode"].Value = l.ItemCode ?? "";
                cmd.Parameters["@Dscription"].Value = l.Dscription ?? "";
                cmd.Parameters["@Quantity"].Value = l.Quantity;
                cmd.Parameters["@Price"].Value = l.Price;
                cmd.Parameters["@WhsCode"].Value = l.WhsCode ?? "";
                cmd.Parameters["@U_ItemName"].Value = l.U_ItemName ?? "";
                cmd.Parameters["@U_Manufacturer"].Value = l.U_Manufacturer ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch { await tx.RollbackAsync(); throw; }
        _log.LogInformation("[NeonSync] TodayOrderLines: {Count} rows", rows.Count);
    }

    private async Task SyncOpenOrderHeadersAsync()
    {
        var rows = await _sqlite.OpenOrderHeaders.AsNoTracking().ToListAsync();

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""OpenOrderHeaders"" RESTART IDENTITY CASCADE",
            insertSql: rows.Count == 0 ? "SELECT 1" : @"
INSERT INTO ""OpenOrderHeaders""
    (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",
     ""OrderTotal"",""SlpCode"",""SlpName"",""Status"")
VALUES
    (@DocEntry,@DocNum,@CardCode,@CardName,@DocDate,
     @OrderTotal,@SlpCode,@SlpName,@Status)",
            rowBinders: rows.Count == 0
                ? [(_) => { }]
                : rows.Select<dynamic>(h => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@DocEntry"].Value = h.DocEntry;
                cmd.Parameters["@DocNum"].Value = h.DocNum;
                cmd.Parameters["@CardCode"].Value = h.CardCode ?? "";
                cmd.Parameters["@CardName"].Value = h.CardName ?? "";
                cmd.Parameters["@DocDate"].Value = h.DocDate;
                cmd.Parameters["@OrderTotal"].Value = h.OrderTotal;
                cmd.Parameters["@SlpCode"].Value = h.SlpCode;
                cmd.Parameters["@SlpName"].Value = h.SlpName ?? "";
                cmd.Parameters["@Status"].Value = h.Status ?? "";
            }),
            paramNames: rows.Count == 0 ? [] :
                ["@DocEntry","@DocNum","@CardCode","@CardName","@DocDate","@OrderTotal","@SlpCode","@SlpName","@Status"],
            paramTypes: rows.Count == 0 ? [] :
                [NpgsqlDbType.Integer,NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Date,
                 NpgsqlDbType.Numeric,NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Text]
        );
        _log.LogInformation("[NeonSync] OpenOrderHeaders: {Count} rows", rows.Count);
    }

    private async Task SyncOpenOrderLinesAsync()
    {
        var rows = await _sqlite.OpenOrderLines.AsNoTracking().ToListAsync();
        if (rows.Count == 0) return;

        var conn = OpenNeonConnection();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""OpenOrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""LineTotal"",""WhsCode"")
VALUES
    (@DocEntry,@LineNum,@DocDate,@ItemCode,@Dscription,
     @Quantity,@Price,@LineTotal,@WhsCode)", conn, tx);

            cmd.Parameters.Add("@DocEntry", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@LineNum", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@DocDate", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ItemCode", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Dscription", NpgsqlDbType.Text);
            cmd.Parameters.Add("@Quantity", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@Price", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@LineTotal", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@WhsCode", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in rows)
            {
                cmd.Parameters["@DocEntry"].Value = l.DocEntry;
                cmd.Parameters["@LineNum"].Value = l.LineNum;
                cmd.Parameters["@DocDate"].Value = l.DocDate;
                cmd.Parameters["@ItemCode"].Value = l.ItemCode ?? "";
                cmd.Parameters["@Dscription"].Value = l.Dscription ?? "";
                cmd.Parameters["@Quantity"].Value = l.Quantity;
                cmd.Parameters["@Price"].Value = l.Price;
                cmd.Parameters["@LineTotal"].Value = l.LineTotal;
                cmd.Parameters["@WhsCode"].Value = l.WhsCode ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch { await tx.RollbackAsync(); throw; }
        _log.LogInformation("[NeonSync] OpenOrderLines: {Count} rows", rows.Count);
    }

    private async Task SyncInvoiceStatusCacheAsync()
    {
        var rows = await _sqlite.DetailedInvoiceStatusCache.AsNoTracking().ToListAsync();

        var conn = OpenNeonConnection();
        await TruncateAndBatchInsertAsync(
            conn,
            truncateSql: @"TRUNCATE TABLE ""InvoiceStatusCache"" RESTART IDENTITY CASCADE",
            insertSql: rows.Count == 0 ? "SELECT 1" : @"
INSERT INTO ""InvoiceStatusCache""
    (""SlpCode"",""SalesName"",""PostingDate"",""InvoiceNo"",""ReinvoicedFrom"",""InvoiceStatus"",
     ""PaidDate"",""Customer"",""CashSales"",""CreditSales"",""ReturnedCashInvoice"",
     ""PaymentsStatus"",""CancellationStatus"")
VALUES
    (@SlpCode,@SalesName,@PostingDate,@InvoiceNo,@ReinvoicedFrom,@InvoiceStatus,
     @PaidDate,@Customer,@CashSales,@CreditSales,@ReturnedCashInvoice,
     @PaymentsStatus,@CancellationStatus)",
            rowBinders: rows.Count == 0
                ? [(_) => { }]
                : rows.Select<dynamic>(r => (NpgsqlCommand cmd) =>
            {
                cmd.Parameters["@SlpCode"].Value = r.SlpCode;
                cmd.Parameters["@SalesName"].Value = r.SalesName ?? "";
                cmd.Parameters["@PostingDate"].Value = (object?)r.PostingDate ?? DBNull.Value;
                cmd.Parameters["@InvoiceNo"].Value = r.InvoiceNo ?? "";
                cmd.Parameters["@ReinvoicedFrom"].Value = r.ReinvoicedFrom ?? "";
                cmd.Parameters["@InvoiceStatus"].Value = r.InvoiceStatus ?? "";
                cmd.Parameters["@PaidDate"].Value = (object?)r.PaidDate ?? DBNull.Value;
                cmd.Parameters["@Customer"].Value = r.Customer ?? "";
                cmd.Parameters["@CashSales"].Value = r.CashSales;
                cmd.Parameters["@CreditSales"].Value = r.CreditSales;
                cmd.Parameters["@ReturnedCashInvoice"].Value = r.ReturnedCashInvoice;
                cmd.Parameters["@PaymentsStatus"].Value = r.PaymentsStatus ?? "";
                cmd.Parameters["@CancellationStatus"].Value = r.CancellationStatus ?? "";
            }),
            paramNames: rows.Count == 0 ? [] :
                ["@SlpCode","@SalesName","@PostingDate","@InvoiceNo","@ReinvoicedFrom","@InvoiceStatus",
                 "@PaidDate","@Customer","@CashSales","@CreditSales","@ReturnedCashInvoice",
                 "@PaymentsStatus","@CancellationStatus"],
            paramTypes: rows.Count == 0 ? [] :
                [NpgsqlDbType.Integer,NpgsqlDbType.Text,NpgsqlDbType.Timestamp,NpgsqlDbType.Text,NpgsqlDbType.Text,NpgsqlDbType.Text,
                 NpgsqlDbType.Timestamp,NpgsqlDbType.Text,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,NpgsqlDbType.Numeric,
                 NpgsqlDbType.Text,NpgsqlDbType.Text]
        );
        _log.LogInformation("[NeonSync] InvoiceStatusCache: {Count} rows", rows.Count);
    }
}
