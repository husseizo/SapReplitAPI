using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Quartz;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Quartz job that mirrors the local SQLite cache to Neon (PostgreSQL) every 5 minutes.
///
/// Rules:
///   • [DisallowConcurrentExecution] — two runs never overlap.
///   • Every per-table failure is caught + logged; the job keeps going for the other tables.
///   • TRUNCATE + INSERT inside a single transaction per parent table so Neon readers
///     always see a consistent snapshot. Child tables (lines) are already cleared by the
///     parent's ON DELETE CASCADE, so they just INSERT without a separate TRUNCATE.
///   • Zero changes to any existing SQLite sync service.
/// </summary>
[DisallowConcurrentExecution]
public class NeonSyncJob : IJob
{
    private readonly CacheDbContext _sqlite;
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonSyncJob> _log;

    public NeonSyncJob(CacheDbContext sqlite, NeonDbContext neon, ILogger<NeonSyncJob> log)
    {
        _sqlite = sqlite;
        _neon = neon;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogInformation("☁️ [NeonSync] Starting mirror push...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await RunSafe("Products", SyncProductsAsync);
        await RunSafe("Customers", SyncCustomersAsync);
        await RunSafe("OrderHeaders+Lines", SyncOrdersAsync);
        await RunSafe("Invoices+Lines", SyncInvoicesAsync);
        await RunSafe("InvoicePayments", SyncInvoicePaymentsAsync);
        await RunSafe("TodayOrders", SyncTodayOrdersAsync);
        await RunSafe("OpenOrders", SyncOpenOrdersAsync);
        await RunSafe("InvoiceStatusCache", SyncInvoiceStatusCacheAsync);

        sw.Stop();
        _log.LogInformation("✅ [NeonSync] Done in {Sec:F1}s", sw.Elapsed.TotalSeconds);
    }

    private async Task RunSafe(string label, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [NeonSync] {Label} failed — local SQLite sync unaffected.", label);
        }
    }

    private NpgsqlConnection Conn()
    {
        var c = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (c.State != System.Data.ConnectionState.Open) c.Open();
        return c;
    }

    // ── Products ─────────────────────────────────────────────────────────────

    private async Task SyncProductsAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();
        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""Products"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""Products""
    (""ItemCode"",""ItemName"",""U_Article_No"",""U_MdlTEST"",""U_Item_Name"",
     ""Price"",""Price05"",""TotalOnHand"",""OnHand"",""OnHandQty"",""WhsCode"",""LastUpdated"",
     ""Whs_001"",""Whs_002"",""Whs_003"",""Whs_004"")
VALUES
    (@ic,@in,@ua,@um,@ui,@pr,@p5,@to,@oh,@oq,@wc,@lu,@w1,@w2,@w3,@w4)", conn, tx))
        {
            cmd.Parameters.Add("@ic", NpgsqlDbType.Text);
            cmd.Parameters.Add("@in", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ua", NpgsqlDbType.Text);
            cmd.Parameters.Add("@um", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ui", NpgsqlDbType.Text);
            cmd.Parameters.Add("@pr", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@p5", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@to", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@oh", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@oq", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@wc", NpgsqlDbType.Text);
            cmd.Parameters.Add("@lu", NpgsqlDbType.Timestamp);
            cmd.Parameters.Add("@w1", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@w2", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@w3", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@w4", NpgsqlDbType.Integer);
            await cmd.PrepareAsync();

            foreach (var p in rows)
            {
                cmd.Parameters["@ic"].Value = p.ItemCode ?? "";
                cmd.Parameters["@in"].Value = p.ItemName ?? "";
                cmd.Parameters["@ua"].Value = p.U_Article_No ?? "";
                cmd.Parameters["@um"].Value = p.U_MdlTEST ?? "";
                cmd.Parameters["@ui"].Value = p.U_Item_Name ?? "";
                cmd.Parameters["@pr"].Value = p.Price;
                cmd.Parameters["@p5"].Value = p.Price05;
                cmd.Parameters["@to"].Value = p.TotalOnHand;
                cmd.Parameters["@oh"].Value = p.OnHand;
                cmd.Parameters["@oq"].Value = p.OnHandQty;
                cmd.Parameters["@wc"].Value = p.WhsCode ?? "";
                cmd.Parameters["@lu"].Value = p.LastUpdated;
                cmd.Parameters["@w1"].Value = (object?)p.Whs_001 ?? DBNull.Value;
                cmd.Parameters["@w2"].Value = (object?)p.Whs_002 ?? DBNull.Value;
                cmd.Parameters["@w3"].Value = (object?)p.Whs_003 ?? DBNull.Value;
                cmd.Parameters["@w4"].Value = (object?)p.Whs_004 ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Products: {N}", rows.Count);
    }

    // ── Customers ─────────────────────────────────────────────────────────────

    private async Task SyncCustomersAsync()
    {
        var rows = await _sqlite.Customers.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();
        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""Customers"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""Customers""
    (""CardCode"",""CardName"",""Balance"",""Region"",""Phone"",""CustomerType"",
     ""SalesPersonName"",""SalesPersonCode"",""TotalSpent"",""VIN1"",""VIN2"",""VIN3"",""AddressesJson"")
VALUES
    (@cc,@cn,@bl,@rg,@ph,@ct,@sn,@sc,@ts,@v1,@v2,@v3,@aj)", conn, tx))
        {
            cmd.Parameters.Add("@cc", NpgsqlDbType.Text);
            cmd.Parameters.Add("@cn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@bl", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@rg", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ph", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ct", NpgsqlDbType.Text);
            cmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@ts", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@v1", NpgsqlDbType.Text);
            cmd.Parameters.Add("@v2", NpgsqlDbType.Text);
            cmd.Parameters.Add("@v3", NpgsqlDbType.Text);
            cmd.Parameters.Add("@aj", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var c in rows)
            {
                cmd.Parameters["@cc"].Value = c.CardCode ?? "";
                cmd.Parameters["@cn"].Value = c.CardName ?? "";
                cmd.Parameters["@bl"].Value = c.Balance;
                cmd.Parameters["@rg"].Value = c.Region ?? "";
                cmd.Parameters["@ph"].Value = c.Phone ?? "";
                cmd.Parameters["@ct"].Value = c.CustomerType ?? "";
                cmd.Parameters["@sn"].Value = c.SalesPersonName ?? "";
                cmd.Parameters["@sc"].Value = (object?)c.SalesPersonCode ?? DBNull.Value;
                cmd.Parameters["@ts"].Value = c.TotalSpent;
                cmd.Parameters["@v1"].Value = c.VIN1 ?? "";
                cmd.Parameters["@v2"].Value = c.VIN2 ?? "";
                cmd.Parameters["@v3"].Value = c.VIN3 ?? "";
                cmd.Parameters["@aj"].Value = c.AddressesJson ?? "[]";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Customers: {N}", rows.Count);
    }

    // ── Orders (headers + lines in one transaction) ───────────────────────────

    private async Task SyncOrdersAsync()
    {
        var headers = await _sqlite.OrderHeaders.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.OrderLines.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();

        // TRUNCATE headers — CASCADE removes lines automatically
        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""OrderHeaders"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""OrderHeaders""
    (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",""SlpCode"",""SlpName"",""CancellationStatus"")
VALUES (@de,@dn,@cn,@dd,@ov,@st,@sc,@sn,@cs)", conn, tx))
        {
            cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dn", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@cn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ov", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@st", NpgsqlDbType.Text);
            cmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@cs", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var h in headers)
            {
                cmd.Parameters["@de"].Value = h.DocEntry;
                cmd.Parameters["@dn"].Value = h.DocNum;
                cmd.Parameters["@cn"].Value = h.CardName ?? "";
                cmd.Parameters["@dd"].Value = h.DocDate;
                cmd.Parameters["@ov"].Value = h.OrderValue;
                cmd.Parameters["@st"].Value = h.Status ?? "";
                cmd.Parameters["@sc"].Value = h.SlpCode;
                cmd.Parameters["@sn"].Value = h.SlpName ?? "";
                cmd.Parameters["@cs"].Value = h.CancellationStatus ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""OrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES (@de,@ln,@dd,@ic,@ds,@qty,@pr,@wc,@ui,@um)", conn, tx))
        {
            cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@ln", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ic", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ds", NpgsqlDbType.Text);
            cmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@pr", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@wc", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ui", NpgsqlDbType.Text);
            cmd.Parameters.Add("@um", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in lines)
            {
                cmd.Parameters["@de"].Value = l.DocEntry;
                cmd.Parameters["@ln"].Value = l.LineNum;
                cmd.Parameters["@dd"].Value = l.DocDate;
                cmd.Parameters["@ic"].Value = l.ItemCode ?? "";
                cmd.Parameters["@ds"].Value = l.Dscription ?? "";
                cmd.Parameters["@qty"].Value = l.Quantity;
                cmd.Parameters["@pr"].Value = l.Price;
                cmd.Parameters["@wc"].Value = l.WhsCode ?? "";
                cmd.Parameters["@ui"].Value = l.U_ItemName ?? "";
                cmd.Parameters["@um"].Value = l.U_Manufacturer ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Orders: {H} headers, {L} lines", headers.Count, lines.Count);
    }

    // ── Invoices + Lines (one transaction) ────────────────────────────────────

    private async Task SyncInvoicesAsync()
    {
        var headers = await _sqlite.Invoices.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.InvoiceLines.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();

        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""Invoices"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""Invoices""
    (""DocEntry"",""DocNum"",""InvoiceDocNum"",""DocDate"",""DocStatus"",""Canceled"",
     ""CardCode"",""CardName"",""DocTotal"",""PaidToDate"",""BalanceDue"",""DaysOverdue"",
     ""SalesEmployeeCode"",""SalesEmployeeName"",""GroupNum"",""DocStatusDisplay"")
VALUES (@de,@dn,@idn,@dd,@ds,@ca,@cc,@cn,@dt,@pd,@bd,@do,@sec,@sen,@gn,@dsd)", conn, tx))
        {
            cmd.Parameters.Add("@de",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dn",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@idn", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dd",  NpgsqlDbType.Date);
            cmd.Parameters.Add("@ds",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@ca",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@cc",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@cn",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@dt",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@pd",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@bd",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@do",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sec", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sen", NpgsqlDbType.Text);
            cmd.Parameters.Add("@gn",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dsd", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var i in headers)
            {
                cmd.Parameters["@de"].Value  = i.DocEntry;
                cmd.Parameters["@dn"].Value  = i.DocNum;
                cmd.Parameters["@idn"].Value = i.InvoiceDocNum;
                cmd.Parameters["@dd"].Value  = i.DocDate;
                cmd.Parameters["@ds"].Value  = i.DocStatus ?? "";
                cmd.Parameters["@ca"].Value  = i.Canceled ?? "";
                cmd.Parameters["@cc"].Value  = i.CardCode ?? "";
                cmd.Parameters["@cn"].Value  = i.CardName ?? "";
                cmd.Parameters["@dt"].Value  = i.DocTotal;
                cmd.Parameters["@pd"].Value  = i.PaidToDate;
                cmd.Parameters["@bd"].Value  = i.BalanceDue;
                cmd.Parameters["@do"].Value  = i.DaysOverdue;
                cmd.Parameters["@sec"].Value = i.SalesEmployeeCode;
                cmd.Parameters["@sen"].Value = i.SalesEmployeeName ?? "";
                cmd.Parameters["@gn"].Value  = i.GroupNum;
                cmd.Parameters["@dsd"].Value = i.DocStatusDisplay ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoiceLines""
    (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",
     ""U_Item_Name"",""U_ItemName"",""U_MdlTEST"",""U_Manufacturer"")
VALUES (@de,@ln,@ic,@ds,@qty,@pr,@lt,@ui1,@ui2,@um1,@um2)", conn, tx))
        {
            cmd.Parameters.Add("@de",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@ln",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@ic",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@ds",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@pr",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@lt",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@ui1", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ui2", NpgsqlDbType.Text);
            cmd.Parameters.Add("@um1", NpgsqlDbType.Text);
            cmd.Parameters.Add("@um2", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in lines)
            {
                cmd.Parameters["@de"].Value  = l.DocEntry;
                cmd.Parameters["@ln"].Value  = l.LineNum;
                cmd.Parameters["@ic"].Value  = l.ItemCode ?? "";
                cmd.Parameters["@ds"].Value  = l.Dscription ?? "";
                cmd.Parameters["@qty"].Value = l.Quantity;
                cmd.Parameters["@pr"].Value  = l.Price;
                cmd.Parameters["@lt"].Value  = l.LineTotal;
                cmd.Parameters["@ui1"].Value = l.U_Item_Name ?? "";
                cmd.Parameters["@ui2"].Value = l.U_ItemName ?? "";
                cmd.Parameters["@um1"].Value = l.U_MdlTEST ?? "";
                cmd.Parameters["@um2"].Value = l.U_Manufacturer ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Invoices: {H} headers, {L} lines", headers.Count, lines.Count);
    }

    // ── InvoicePayments ───────────────────────────────────────────────────────

    private async Task SyncInvoicePaymentsAsync()
    {
        var rows = await _sqlite.InvoicePayments.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();
        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""InvoicePayments"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        using (var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoicePayments""
    (""DocEntry"",""PaymentDocEntry"",""PaymentNumber"",""InvoiceDocNum"",""PaymentDate"",
     ""CardCode"",""CardName"",""AmountApplied"",""BankTransferAmount"",""BankTransferReference"",
     ""DebitAccountCode"",""DebitAccountName"",""SalesEmployeeCode"",""SalesEmployeeName"")
VALUES (@de,@pde,@pn,@idn,@pd,@cc,@cn,@aa,@bta,@btr,@dac,@dan,@sec,@sen)", conn, tx))
        {
            cmd.Parameters.Add("@de",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@pde", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@pn",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@idn", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@pd",  NpgsqlDbType.Date);
            cmd.Parameters.Add("@cc",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@cn",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@aa",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@bta", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@btr", NpgsqlDbType.Text);
            cmd.Parameters.Add("@dac", NpgsqlDbType.Text);
            cmd.Parameters.Add("@dan", NpgsqlDbType.Text);
            cmd.Parameters.Add("@sec", NpgsqlDbType.Text);
            cmd.Parameters.Add("@sen", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var p in rows)
            {
                cmd.Parameters["@de"].Value  = p.DocEntry;
                cmd.Parameters["@pde"].Value = p.PaymentDocEntry;
                cmd.Parameters["@pn"].Value  = p.PaymentNumber;
                cmd.Parameters["@idn"].Value = p.InvoiceDocNum;
                cmd.Parameters["@pd"].Value  = p.PaymentDate;
                cmd.Parameters["@cc"].Value  = p.CardCode ?? "";
                cmd.Parameters["@cn"].Value  = p.CardName ?? "";
                cmd.Parameters["@aa"].Value  = p.AmountApplied;
                cmd.Parameters["@bta"].Value = p.BankTransferAmount;
                cmd.Parameters["@btr"].Value = p.BankTransferReference ?? "";
                cmd.Parameters["@dac"].Value = p.DebitAccountCode ?? "";
                cmd.Parameters["@dan"].Value = p.DebitAccountName ?? "";
                cmd.Parameters["@sec"].Value = p.SalesEmployeeCode ?? "";
                cmd.Parameters["@sen"].Value = p.SalesEmployeeName ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] InvoicePayments: {N}", rows.Count);
    }

    // ── Today Orders (headers + lines, one transaction) ───────────────────────

    private async Task SyncTodayOrdersAsync()
    {
        var headers = await _sqlite.TodayOrderHeaders.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.TodayOrderLines.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();

        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""TodayOrderHeaders"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        if (headers.Count > 0)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderHeaders""
    (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",
     ""SlpCode"",""SlpName"",""Cancelled"")
VALUES (@de,@dn,@cn,@dd,@ov,@st,@sc,@sn,@ca)", conn, tx);
            cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dn", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@cn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ov", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@st", NpgsqlDbType.Text);
            cmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ca", NpgsqlDbType.Boolean);
            await cmd.PrepareAsync();

            foreach (var h in headers)
            {
                cmd.Parameters["@de"].Value = h.DocEntry;
                cmd.Parameters["@dn"].Value = h.DocNum;
                cmd.Parameters["@cn"].Value = h.CardName ?? "";
                cmd.Parameters["@dd"].Value = h.DocDate;
                cmd.Parameters["@ov"].Value = h.OrderValue;
                cmd.Parameters["@st"].Value = h.Status ?? "";
                cmd.Parameters["@sc"].Value = h.SlpCode;
                cmd.Parameters["@sn"].Value = h.SlpName ?? "";
                cmd.Parameters["@ca"].Value = h.Cancelled;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // TodayOrderLine has no LineNum — use Id as ordering key only
        if (lines.Count > 0)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderLines""
    (""DocEntry"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES (@de,@dd,@ic,@ds,@qty,@pr,@wc,@ui,@um)", conn, tx);
            cmd.Parameters.Add("@de",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dd",  NpgsqlDbType.Date);
            cmd.Parameters.Add("@ic",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@ds",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@pr",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@wc",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@ui",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@um",  NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in lines)
            {
                cmd.Parameters["@de"].Value  = l.DocEntry;
                cmd.Parameters["@dd"].Value  = l.DocDate;
                cmd.Parameters["@ic"].Value  = l.ItemCode ?? "";
                cmd.Parameters["@ds"].Value  = l.Dscription ?? "";
                cmd.Parameters["@qty"].Value = l.Quantity;
                cmd.Parameters["@pr"].Value  = l.Price;
                cmd.Parameters["@wc"].Value  = l.WhsCode ?? "";
                cmd.Parameters["@ui"].Value  = l.U_ItemName ?? "";
                cmd.Parameters["@um"].Value  = l.U_Manufacturer ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] TodayOrders: {H} headers, {L} lines", headers.Count, lines.Count);
    }

    // ── Open Orders (headers + lines, one transaction) ────────────────────────

    private async Task SyncOpenOrdersAsync()
    {
        var headers = await _sqlite.OpenOrderHeaders.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.OpenOrderLines.AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();

        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""OpenOrderHeaders"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        if (headers.Count > 0)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""OpenOrderHeaders""
    (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",
     ""OrderTotal"",""SlpCode"",""SlpName"",""Status"")
VALUES (@de,@dn,@cc,@cn,@dd,@ot,@sc,@sn,@st)", conn, tx);
            cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dn", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@cc", NpgsqlDbType.Text);
            cmd.Parameters.Add("@cn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            cmd.Parameters.Add("@ot", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@st", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var h in headers)
            {
                cmd.Parameters["@de"].Value = h.DocEntry;
                cmd.Parameters["@dn"].Value = h.DocNum;
                cmd.Parameters["@cc"].Value = h.CardCode ?? "";
                cmd.Parameters["@cn"].Value = h.CardName ?? "";
                cmd.Parameters["@dd"].Value = h.DocDate;
                cmd.Parameters["@ot"].Value = h.OrderTotal;
                cmd.Parameters["@sc"].Value = h.SlpCode;
                cmd.Parameters["@sn"].Value = h.SlpName ?? "";
                cmd.Parameters["@st"].Value = h.Status ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        if (lines.Count > 0)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""OpenOrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""LineTotal"",""WhsCode"")
VALUES (@de,@ln,@dd,@ic,@ds,@qty,@pr,@lt,@wc)", conn, tx);
            cmd.Parameters.Add("@de",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@ln",  NpgsqlDbType.Integer);
            cmd.Parameters.Add("@dd",  NpgsqlDbType.Date);
            cmd.Parameters.Add("@ic",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@ds",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@pr",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@lt",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@wc",  NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var l in lines)
            {
                cmd.Parameters["@de"].Value  = l.DocEntry;
                cmd.Parameters["@ln"].Value  = l.LineNum;
                cmd.Parameters["@dd"].Value  = l.DocDate;
                cmd.Parameters["@ic"].Value  = l.ItemCode ?? "";
                cmd.Parameters["@ds"].Value  = l.Dscription ?? "";
                cmd.Parameters["@qty"].Value = l.Quantity;
                cmd.Parameters["@pr"].Value  = l.Price;
                cmd.Parameters["@lt"].Value  = l.LineTotal;
                cmd.Parameters["@wc"].Value  = l.WhsCode ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] OpenOrders: {H} headers, {L} lines", headers.Count, lines.Count);
    }

    // ── InvoiceStatusCache ────────────────────────────────────────────────────

    private async Task SyncInvoiceStatusCacheAsync()
    {
        // CacheDbContext has no DbSet property for this entity — use Set<T>()
        var rows = await _sqlite.Set<DetailedInvoiceStatusCache>().AsNoTracking().ToListAsync();
        var conn = Conn();
        using var tx = await conn.BeginTransactionAsync();
        using (var cmd = new NpgsqlCommand(@"TRUNCATE TABLE ""InvoiceStatusCache"" RESTART IDENTITY CASCADE", conn, tx))
            await cmd.ExecuteNonQueryAsync();

        if (rows.Count > 0)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoiceStatusCache""
    (""SlpCode"",""SalesName"",""PostingDate"",""InvoiceNo"",""ReinvoicedFrom"",""InvoiceStatus"",
     ""PaidDate"",""Customer"",""CashSales"",""CreditSales"",""ReturnedCashInvoice"",
     ""PaymentsStatus"",""CancellationStatus"")
VALUES (@sc,@sn,@ptd,@ino,@rf,@is,@paid,@cust,@cs,@crs,@rci,@ps,@cans)", conn, tx);
            cmd.Parameters.Add("@sc",   NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sn",   NpgsqlDbType.Text);
            cmd.Parameters.Add("@ptd",  NpgsqlDbType.Timestamp);
            cmd.Parameters.Add("@ino",  NpgsqlDbType.Text);
            cmd.Parameters.Add("@rf",   NpgsqlDbType.Text);
            cmd.Parameters.Add("@is",   NpgsqlDbType.Text);
            cmd.Parameters.Add("@paid", NpgsqlDbType.Timestamp);
            cmd.Parameters.Add("@cust", NpgsqlDbType.Text);
            cmd.Parameters.Add("@cs",   NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@crs",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@rci",  NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@ps",   NpgsqlDbType.Text);
            cmd.Parameters.Add("@cans", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var r in rows)
            {
                cmd.Parameters["@sc"].Value   = r.SlpCode;
                cmd.Parameters["@sn"].Value   = r.SalesName ?? "";
                cmd.Parameters["@ptd"].Value  = (object?)r.PostingDate ?? DBNull.Value;
                cmd.Parameters["@ino"].Value  = r.InvoiceNo ?? "";
                cmd.Parameters["@rf"].Value   = r.ReinvoicedFrom ?? "";
                cmd.Parameters["@is"].Value   = r.InvoiceStatus ?? "";
                cmd.Parameters["@paid"].Value = (object?)r.PaidDate ?? DBNull.Value;
                cmd.Parameters["@cust"].Value = r.Customer ?? "";
                cmd.Parameters["@cs"].Value   = r.CashSales;
                cmd.Parameters["@crs"].Value  = r.CreditSales;
                cmd.Parameters["@rci"].Value  = r.ReturnedCashInvoice;
                cmd.Parameters["@ps"].Value   = r.PaymentsStatus ?? "";
                cmd.Parameters["@cans"].Value = r.CancellationStatus ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] InvoiceStatusCache: {N}", rows.Count);
    }
}
