using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Quartz;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Mirrors the local SQLite cache to Neon.
///
/// Strategy:
/// - Normal mode: use SQLite SyncMetadata watermarks to skip unchanged datasets.
/// - Incremental mode: upsert parent tables and refresh dependent line/snapshot tables only when their source changed.
/// - Fallback mode: run a full reconcile periodically or when forced via env var NEON_FULL_RECONCILE=true.
/// </summary>
[DisallowConcurrentExecution]
public class NeonSyncJob : IJob
{
    private const string ForceFullReconcileEnvVar = "NEON_FULL_RECONCILE";
    private const string FullReconcileMetadataKey = "NeonMirror:FullReconcile";
    private static readonly TimeSpan FullReconcileInterval = TimeSpan.FromHours(24);

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

        try
        {
            var forceFull = string.Equals(
                Environment.GetEnvironmentVariable(ForceFullReconcileEnvVar),
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (forceFull || await NeedsFullReconcileAsync())
            {
                _log.LogInformation("🧹 [NeonSync] Running full reconcile. Forced={Forced}", forceFull);
                await RunFullReconcileAsync();
                await SetMirrorTimestampAsync(FullReconcileMetadataKey, DateTime.UtcNow);
            }
            else
            {
                await SyncIfChangedAsync("Products", new[] { "Product" }, SyncProductsIncrementalAsync);
                await SyncIfChangedAsync("Customers", new[] { "Customer" }, SyncCustomersIncrementalAsync);
                await SyncIfChangedAsync("Orders", new[] { "Order" }, SyncOrdersIncrementalAsync);
                await SyncIfChangedAsync("Invoices", new[] { "Invoice" }, SyncInvoicesIncrementalAsync);
                await SyncIfChangedAsync("InvoicePayments", new[] { "Invoice" }, SyncInvoicePaymentsIncrementalAsync);
                await SyncIfChangedAsync("TodayOrders", new[] { "TodayOrder" }, ReplaceTodayOrdersAsync);
                await SyncIfChangedAsync("OpenOrders", new[] { "OpenOrder" }, ReplaceOpenOrdersAsync);
                await SyncIfChangedAsync("InvoiceStatusCache", new[] { "InvoiceStatusCache" }, ReplaceInvoiceStatusCacheAsync);
            }
        }
        finally
        {
            sw.Stop();
            _log.LogInformation("✅ [NeonSync] Finished in {Sec:F1}s", sw.Elapsed.TotalSeconds);
        }
    }

    private async Task SyncIfChangedAsync(string label, string[] sourceTypes, Func<Task> action)
    {
        try
        {
            var sourceTimestamp = await GetLatestSourceTimestampAsync(sourceTypes);
            if (!sourceTimestamp.HasValue)
            {
                _log.LogDebug("⏭️ [NeonSync] {Label} skipped - no source watermark yet.", label);
                return;
            }

            var mirrorKey = GetMirrorStateKey(label);
            var mirrorTimestamp = await GetMirrorTimestampAsync(mirrorKey);
            if (mirrorTimestamp.HasValue && mirrorTimestamp.Value >= sourceTimestamp.Value)
            {
                _log.LogDebug("⏭️ [NeonSync] {Label} skipped - mirror is up to date.", label);
                return;
            }

            await action();
            await SetMirrorTimestampAsync(mirrorKey, sourceTimestamp.Value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [NeonSync] {Label} failed - SQLite remains source of truth.", label);
        }
    }

    private async Task RunFullReconcileAsync()
    {
        await RunFullStepAsync("Products", new[] { "Product" }, ReplaceProductsAsync);
        await RunFullStepAsync("Customers", new[] { "Customer" }, ReplaceCustomersAsync);
        await RunFullStepAsync("Orders", new[] { "Order" }, ReplaceOrdersAsync);
        await RunFullStepAsync("Invoices", new[] { "Invoice" }, ReplaceInvoicesAsync);
        await RunFullStepAsync("InvoicePayments", new[] { "Invoice" }, ReplaceInvoicePaymentsAsync);
        await RunFullStepAsync("TodayOrders", new[] { "TodayOrder" }, ReplaceTodayOrdersAsync);
        await RunFullStepAsync("OpenOrders", new[] { "OpenOrder" }, ReplaceOpenOrdersAsync);
        await RunFullStepAsync("InvoiceStatusCache", new[] { "InvoiceStatusCache" }, ReplaceInvoiceStatusCacheAsync);
    }

    private async Task RunFullStepAsync(string label, string[] sourceTypes, Func<Task> action)
    {
        try
        {
            await action();

            var sourceTimestamp = await GetLatestSourceTimestampAsync(sourceTypes) ?? DateTime.UtcNow;
            await SetMirrorTimestampAsync(GetMirrorStateKey(label), sourceTimestamp);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [NeonSync] Full reconcile step failed for {Label}", label);
        }
    }

    private static string GetMirrorStateKey(string label) => $"NeonMirror:{label}";

    private async Task<DateTime?> GetLatestSourceTimestampAsync(IEnumerable<string> sourceTypes)
    {
        var keys = sourceTypes.ToArray();
        var timestamps = await _sqlite.SyncMetadata
            .AsNoTracking()
            .Where(m => keys.Contains(m.Type))
            .Select(m => (DateTime?)m.LastSyncedAt)
            .ToListAsync();

        return timestamps.Count == 0 ? null : timestamps.Max();
    }

    private async Task<DateTime?> GetMirrorTimestampAsync(string key)
    {
        return await _sqlite.SyncMetadata
            .AsNoTracking()
            .Where(m => m.Type == key)
            .Select(m => (DateTime?)m.LastSyncedAt)
            .FirstOrDefaultAsync();
    }

    private async Task SetMirrorTimestampAsync(string key, DateTime timestamp)
    {
        var meta = await _sqlite.SyncMetadata.FirstOrDefaultAsync(m => m.Type == key);
        if (meta == null)
            await _sqlite.SyncMetadata.AddAsync(new SyncMetadata { Type = key, LastSyncedAt = timestamp });
        else
            meta.LastSyncedAt = timestamp;

        await _sqlite.SaveChangesAsync();
    }

    private async Task<bool> NeedsFullReconcileAsync()
    {
        var lastFull = await GetMirrorTimestampAsync(FullReconcileMetadataKey);
        return !lastFull.HasValue || (DateTime.UtcNow - lastFull.Value) >= FullReconcileInterval;
    }

    private async Task<NpgsqlConnection> GetConnectionAsync()
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        return conn;
    }

    private async Task DeleteAllAsync(NpgsqlConnection conn, NpgsqlTransaction tx, params string[] tables)
    {
        foreach (var table in tables)
        {
            using var cmd = new NpgsqlCommand($@"DELETE FROM ""{table}""", conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SyncProductsIncrementalAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await UpsertProductsAsync(rows, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Products upserted: {Count}", rows.Count);
    }

    private async Task ReplaceProductsAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "Products");
        await UpsertProductsAsync(rows, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Products full reconcile: {Count}", rows.Count);
    }

    private async Task UpsertProductsAsync(List<CachedProduct> rows, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""Products""
    (""ItemCode"",""ItemName"",""U_Article_No"",""U_MdlTEST"",""U_Item_Name"",
     ""Price"",""Price05"",""TotalOnHand"",""OnHand"",""OnHandQty"",""WhsCode"",""LastUpdated"",
     ""Whs_001"",""Whs_002"",""Whs_003"",""Whs_004"")
VALUES
    (@ic,@in,@ua,@um,@ui,@pr,@p5,@to,@oh,@oq,@wc,@lu,@w1,@w2,@w3,@w4)
ON CONFLICT (""ItemCode"") DO UPDATE SET
    ""ItemName"" = EXCLUDED.""ItemName"",
    ""U_Article_No"" = EXCLUDED.""U_Article_No"",
    ""U_MdlTEST"" = EXCLUDED.""U_MdlTEST"",
    ""U_Item_Name"" = EXCLUDED.""U_Item_Name"",
    ""Price"" = EXCLUDED.""Price"",
    ""Price05"" = EXCLUDED.""Price05"",
    ""TotalOnHand"" = EXCLUDED.""TotalOnHand"",
    ""OnHand"" = EXCLUDED.""OnHand"",
    ""OnHandQty"" = EXCLUDED.""OnHandQty"",
    ""WhsCode"" = EXCLUDED.""WhsCode"",
    ""LastUpdated"" = EXCLUDED.""LastUpdated"",
    ""Whs_001"" = EXCLUDED.""Whs_001"",
    ""Whs_002"" = EXCLUDED.""Whs_002"",
    ""Whs_003"" = EXCLUDED.""Whs_003"",
    ""Whs_004"" = EXCLUDED.""Whs_004"";", conn, tx);

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

    private async Task SyncCustomersIncrementalAsync()
    {
        var rows = await _sqlite.Customers.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await UpsertCustomersAsync(rows, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Customers upserted: {Count}", rows.Count);
    }

    private async Task ReplaceCustomersAsync()
    {
        var rows = await _sqlite.Customers.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "Customers");
        await UpsertCustomersAsync(rows, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Customers full reconcile: {Count}", rows.Count);
    }

    private async Task UpsertCustomersAsync(List<CachedCustomer> rows, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""Customers""
    (""CardCode"",""CardName"",""Balance"",""Region"",""Phone"",""CustomerType"",
     ""SalesPersonName"",""SalesPersonCode"",""TotalSpent"",""VIN1"",""VIN2"",""VIN3"",""AddressesJson"")
VALUES
    (@cc,@cn,@bl,@rg,@ph,@ct,@sn,@sc,@ts,@v1,@v2,@v3,@aj)
ON CONFLICT (""CardCode"") DO UPDATE SET
    ""CardName"" = EXCLUDED.""CardName"",
    ""Balance"" = EXCLUDED.""Balance"",
    ""Region"" = EXCLUDED.""Region"",
    ""Phone"" = EXCLUDED.""Phone"",
    ""CustomerType"" = EXCLUDED.""CustomerType"",
    ""SalesPersonName"" = EXCLUDED.""SalesPersonName"",
    ""SalesPersonCode"" = EXCLUDED.""SalesPersonCode"",
    ""TotalSpent"" = EXCLUDED.""TotalSpent"",
    ""VIN1"" = EXCLUDED.""VIN1"",
    ""VIN2"" = EXCLUDED.""VIN2"",
    ""VIN3"" = EXCLUDED.""VIN3"",
    ""AddressesJson"" = EXCLUDED.""AddressesJson"";", conn, tx);

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

    private async Task SyncOrdersIncrementalAsync()
    {
        var headers = await _sqlite.OrderHeaders.AsNoTracking().ToListAsync();
        var lines = await _sqlite.OrderLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await UpsertOrderHeadersAsync(headers, conn, tx);
        await ReplaceOrderLinesAsync(lines, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Orders refreshed. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task ReplaceOrdersAsync()
    {
        var headers = await _sqlite.OrderHeaders.AsNoTracking().ToListAsync();
        var lines = await _sqlite.OrderLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "OrderLines", "OrderHeaders");
        await UpsertOrderHeadersAsync(headers, conn, tx);
        await ReplaceOrderLinesAsync(lines, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Orders full reconcile. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task UpsertOrderHeadersAsync(List<CachedOrder> headers, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""OrderHeaders""
    (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",""SlpCode"",""SlpName"",""CancellationStatus"")
VALUES (@de,@dn,@cn,@dd,@ov,@st,@sc,@sn,@cs)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum"" = EXCLUDED.""DocNum"",
    ""CardName"" = EXCLUDED.""CardName"",
    ""DocDate"" = EXCLUDED.""DocDate"",
    ""OrderValue"" = EXCLUDED.""OrderValue"",
    ""Status"" = EXCLUDED.""Status"",
    ""SlpCode"" = EXCLUDED.""SlpCode"",
    ""SlpName"" = EXCLUDED.""SlpName"",
    ""CancellationStatus"" = EXCLUDED.""CancellationStatus"";", conn, tx);

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

    private async Task ReplaceOrderLinesAsync(List<CachedOrderLine> lines, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await DeleteAllAsync(conn, tx, "OrderLines");

        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""OrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES (@de,@ln,@dd,@ic,@ds,@qty,@pr,@wc,@ui,@um)", conn, tx);

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

    private async Task SyncInvoicesIncrementalAsync()
    {
        var headers = await _sqlite.Invoices.AsNoTracking().ToListAsync();
        var lines = await _sqlite.InvoiceLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await UpsertInvoiceHeadersAsync(headers, conn, tx);
        await ReplaceInvoiceLinesAsync(lines, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Invoices refreshed. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task ReplaceInvoicesAsync()
    {
        var headers = await _sqlite.Invoices.AsNoTracking().ToListAsync();
        var lines = await _sqlite.InvoiceLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "InvoiceLines", "Invoices");
        await UpsertInvoiceHeadersAsync(headers, conn, tx);
        await ReplaceInvoiceLinesAsync(lines, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] Invoices full reconcile. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task UpsertInvoiceHeadersAsync(List<CachedInvoice> headers, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""Invoices""
    (""DocEntry"",""DocNum"",""InvoiceDocNum"",""DocDate"",""DocStatus"",""Canceled"",
     ""CardCode"",""CardName"",""DocTotal"",""PaidToDate"",""BalanceDue"",""DaysOverdue"",
     ""SalesEmployeeCode"",""SalesEmployeeName"",""GroupNum"",""DocStatusDisplay"")
VALUES (@de,@dn,@idn,@dd,@ds,@ca,@cc,@cn,@dt,@pd,@bd,@do,@sec,@sen,@gn,@dsd)
ON CONFLICT (""DocEntry"") DO UPDATE SET
    ""DocNum"" = EXCLUDED.""DocNum"",
    ""InvoiceDocNum"" = EXCLUDED.""InvoiceDocNum"",
    ""DocDate"" = EXCLUDED.""DocDate"",
    ""DocStatus"" = EXCLUDED.""DocStatus"",
    ""Canceled"" = EXCLUDED.""Canceled"",
    ""CardCode"" = EXCLUDED.""CardCode"",
    ""CardName"" = EXCLUDED.""CardName"",
    ""DocTotal"" = EXCLUDED.""DocTotal"",
    ""PaidToDate"" = EXCLUDED.""PaidToDate"",
    ""BalanceDue"" = EXCLUDED.""BalanceDue"",
    ""DaysOverdue"" = EXCLUDED.""DaysOverdue"",
    ""SalesEmployeeCode"" = EXCLUDED.""SalesEmployeeCode"",
    ""SalesEmployeeName"" = EXCLUDED.""SalesEmployeeName"",
    ""GroupNum"" = EXCLUDED.""GroupNum"",
    ""DocStatusDisplay"" = EXCLUDED.""DocStatusDisplay"";", conn, tx);

        cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@dn", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@idn", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@dd", NpgsqlDbType.Date);
        cmd.Parameters.Add("@ds", NpgsqlDbType.Text);
        cmd.Parameters.Add("@ca", NpgsqlDbType.Text);
        cmd.Parameters.Add("@cc", NpgsqlDbType.Text);
        cmd.Parameters.Add("@cn", NpgsqlDbType.Text);
        cmd.Parameters.Add("@dt", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@pd", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@bd", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@do", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@sec", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@sen", NpgsqlDbType.Text);
        cmd.Parameters.Add("@gn", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@dsd", NpgsqlDbType.Text);
        await cmd.PrepareAsync();

        foreach (var i in headers)
        {
            cmd.Parameters["@de"].Value = i.DocEntry;
            cmd.Parameters["@dn"].Value = i.DocNum;
            cmd.Parameters["@idn"].Value = i.InvoiceDocNum;
            cmd.Parameters["@dd"].Value = i.DocDate;
            cmd.Parameters["@ds"].Value = i.DocStatus ?? "";
            cmd.Parameters["@ca"].Value = i.Canceled ?? "";
            cmd.Parameters["@cc"].Value = i.CardCode ?? "";
            cmd.Parameters["@cn"].Value = i.CardName ?? "";
            cmd.Parameters["@dt"].Value = i.DocTotal;
            cmd.Parameters["@pd"].Value = i.PaidToDate;
            cmd.Parameters["@bd"].Value = i.BalanceDue;
            cmd.Parameters["@do"].Value = i.DaysOverdue;
            cmd.Parameters["@sec"].Value = i.SalesEmployeeCode;
            cmd.Parameters["@sen"].Value = i.SalesEmployeeName ?? "";
            cmd.Parameters["@gn"].Value = i.GroupNum;
            cmd.Parameters["@dsd"].Value = i.DocStatusDisplay ?? "";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task ReplaceInvoiceLinesAsync(List<CachedInvoiceLine> lines, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await DeleteAllAsync(conn, tx, "InvoiceLines");

        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoiceLines""
    (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",
     ""U_Item_Name"",""U_ItemName"",""U_MdlTEST"",""U_Manufacturer"")
VALUES (@de,@ln,@ic,@ds,@qty,@pr,@lt,@ui1,@ui2,@um1,@um2)", conn, tx);

        cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@ln", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@ic", NpgsqlDbType.Text);
        cmd.Parameters.Add("@ds", NpgsqlDbType.Text);
        cmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@pr", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@lt", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@ui1", NpgsqlDbType.Text);
        cmd.Parameters.Add("@ui2", NpgsqlDbType.Text);
        cmd.Parameters.Add("@um1", NpgsqlDbType.Text);
        cmd.Parameters.Add("@um2", NpgsqlDbType.Text);
        await cmd.PrepareAsync();

        foreach (var l in lines)
        {
            cmd.Parameters["@de"].Value = l.DocEntry;
            cmd.Parameters["@ln"].Value = l.LineNum;
            cmd.Parameters["@ic"].Value = l.ItemCode ?? "";
            cmd.Parameters["@ds"].Value = l.Dscription ?? "";
            cmd.Parameters["@qty"].Value = l.Quantity;
            cmd.Parameters["@pr"].Value = l.Price;
            cmd.Parameters["@lt"].Value = l.LineTotal;
            cmd.Parameters["@ui1"].Value = l.U_Item_Name ?? "";
            cmd.Parameters["@ui2"].Value = l.U_ItemName ?? "";
            cmd.Parameters["@um1"].Value = l.U_MdlTEST ?? "";
            cmd.Parameters["@um2"].Value = l.U_Manufacturer ?? "";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SyncInvoicePaymentsIncrementalAsync()
    {
        var rows = await _sqlite.InvoicePayments.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await UpsertInvoicePaymentsAsync(rows, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] InvoicePayments upserted: {Count}", rows.Count);
    }

    private async Task ReplaceInvoicePaymentsAsync()
    {
        var rows = await _sqlite.InvoicePayments.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "InvoicePayments");
        await UpsertInvoicePaymentsAsync(rows, conn, tx);
        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] InvoicePayments full reconcile: {Count}", rows.Count);
    }

    private async Task UpsertInvoicePaymentsAsync(List<CachedInvoicePayment> rows, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoicePayments""
    (""DocEntry"",""PaymentDocEntry"",""PaymentNumber"",""InvoiceDocNum"",""PaymentDate"",
     ""CardCode"",""CardName"",""AmountApplied"",""BankTransferAmount"",""BankTransferReference"",
     ""DebitAccountCode"",""DebitAccountName"",""SalesEmployeeCode"",""SalesEmployeeName"")
VALUES (@de,@pde,@pn,@idn,@pd,@cc,@cn,@aa,@bta,@btr,@dac,@dan,@sec,@sen)
ON CONFLICT (""PaymentDocEntry"") DO UPDATE SET
    ""DocEntry"" = EXCLUDED.""DocEntry"",
    ""PaymentNumber"" = EXCLUDED.""PaymentNumber"",
    ""InvoiceDocNum"" = EXCLUDED.""InvoiceDocNum"",
    ""PaymentDate"" = EXCLUDED.""PaymentDate"",
    ""CardCode"" = EXCLUDED.""CardCode"",
    ""CardName"" = EXCLUDED.""CardName"",
    ""AmountApplied"" = EXCLUDED.""AmountApplied"",
    ""BankTransferAmount"" = EXCLUDED.""BankTransferAmount"",
    ""BankTransferReference"" = EXCLUDED.""BankTransferReference"",
    ""DebitAccountCode"" = EXCLUDED.""DebitAccountCode"",
    ""DebitAccountName"" = EXCLUDED.""DebitAccountName"",
    ""SalesEmployeeCode"" = EXCLUDED.""SalesEmployeeCode"",
    ""SalesEmployeeName"" = EXCLUDED.""SalesEmployeeName"";", conn, tx);

        cmd.Parameters.Add("@de", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@pde", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@pn", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@idn", NpgsqlDbType.Integer);
        cmd.Parameters.Add("@pd", NpgsqlDbType.Date);
        cmd.Parameters.Add("@cc", NpgsqlDbType.Text);
        cmd.Parameters.Add("@cn", NpgsqlDbType.Text);
        cmd.Parameters.Add("@aa", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@bta", NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@btr", NpgsqlDbType.Text);
        cmd.Parameters.Add("@dac", NpgsqlDbType.Text);
        cmd.Parameters.Add("@dan", NpgsqlDbType.Text);
        cmd.Parameters.Add("@sec", NpgsqlDbType.Text);
        cmd.Parameters.Add("@sen", NpgsqlDbType.Text);
        await cmd.PrepareAsync();

        foreach (var p in rows)
        {
            cmd.Parameters["@de"].Value = p.DocEntry;
            cmd.Parameters["@pde"].Value = p.PaymentDocEntry;
            cmd.Parameters["@pn"].Value = p.PaymentNumber;
            cmd.Parameters["@idn"].Value = p.InvoiceDocNum;
            cmd.Parameters["@pd"].Value = p.PaymentDate;
            cmd.Parameters["@cc"].Value = p.CardCode ?? "";
            cmd.Parameters["@cn"].Value = p.CardName ?? "";
            cmd.Parameters["@aa"].Value = p.AmountApplied;
            cmd.Parameters["@bta"].Value = p.BankTransferAmount;
            cmd.Parameters["@btr"].Value = p.BankTransferReference ?? "";
            cmd.Parameters["@dac"].Value = p.DebitAccountCode ?? "";
            cmd.Parameters["@dan"].Value = p.DebitAccountName ?? "";
            cmd.Parameters["@sec"].Value = p.SalesEmployeeCode ?? "";
            cmd.Parameters["@sen"].Value = p.SalesEmployeeName ?? "";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task ReplaceTodayOrdersAsync()
    {
        var headers = await _sqlite.TodayOrderHeaders.AsNoTracking().ToListAsync();
        var lines = await _sqlite.TodayOrderLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "TodayOrderLines", "TodayOrderHeaders");

        if (headers.Count > 0)
        {
            using var headerCmd = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderHeaders""
    (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",
     ""SlpCode"",""SlpName"",""Cancelled"")
VALUES (@de,@dn,@cn,@dd,@ov,@st,@sc,@sn,@ca)", conn, tx);
            headerCmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            headerCmd.Parameters.Add("@dn", NpgsqlDbType.Integer);
            headerCmd.Parameters.Add("@cn", NpgsqlDbType.Text);
            headerCmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            headerCmd.Parameters.Add("@ov", NpgsqlDbType.Numeric);
            headerCmd.Parameters.Add("@st", NpgsqlDbType.Text);
            headerCmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            headerCmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            headerCmd.Parameters.Add("@ca", NpgsqlDbType.Boolean);
            await headerCmd.PrepareAsync();

            foreach (var h in headers)
            {
                headerCmd.Parameters["@de"].Value = h.DocEntry;
                headerCmd.Parameters["@dn"].Value = h.DocNum;
                headerCmd.Parameters["@cn"].Value = h.CardName ?? "";
                headerCmd.Parameters["@dd"].Value = h.DocDate;
                headerCmd.Parameters["@ov"].Value = h.OrderValue;
                headerCmd.Parameters["@st"].Value = h.Status ?? "";
                headerCmd.Parameters["@sc"].Value = (object?)h.SlpCode ?? DBNull.Value;
                headerCmd.Parameters["@sn"].Value = h.SlpName ?? "";
                headerCmd.Parameters["@ca"].Value = h.Cancelled;
                await headerCmd.ExecuteNonQueryAsync();
            }
        }

        if (lines.Count > 0)
        {
            using var lineCmd = new NpgsqlCommand(@"
INSERT INTO ""TodayOrderLines""
    (""DocEntry"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"")
VALUES (@de,@dd,@ic,@ds,@qty,@pr,@wc,@ui,@um)", conn, tx);
            lineCmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            lineCmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            lineCmd.Parameters.Add("@ic", NpgsqlDbType.Text);
            lineCmd.Parameters.Add("@ds", NpgsqlDbType.Text);
            lineCmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
            lineCmd.Parameters.Add("@pr", NpgsqlDbType.Numeric);
            lineCmd.Parameters.Add("@wc", NpgsqlDbType.Text);
            lineCmd.Parameters.Add("@ui", NpgsqlDbType.Text);
            lineCmd.Parameters.Add("@um", NpgsqlDbType.Text);
            await lineCmd.PrepareAsync();

            foreach (var l in lines)
            {
                lineCmd.Parameters["@de"].Value = l.DocEntry;
                lineCmd.Parameters["@dd"].Value = l.DocDate;
                lineCmd.Parameters["@ic"].Value = l.ItemCode ?? "";
                lineCmd.Parameters["@ds"].Value = l.Dscription ?? "";
                lineCmd.Parameters["@qty"].Value = l.Quantity;
                lineCmd.Parameters["@pr"].Value = l.Price;
                lineCmd.Parameters["@wc"].Value = l.WhsCode ?? "";
                lineCmd.Parameters["@ui"].Value = l.U_ItemName ?? "";
                lineCmd.Parameters["@um"].Value = l.U_Manufacturer ?? "";
                await lineCmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] TodayOrders replaced. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task ReplaceOpenOrdersAsync()
    {
        var headers = await _sqlite.OpenOrderHeaders.AsNoTracking().ToListAsync();
        var lines = await _sqlite.OpenOrderLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "OpenOrderLines", "OpenOrderHeaders");

        if (headers.Count > 0)
        {
            using var headerCmd = new NpgsqlCommand(@"
INSERT INTO ""OpenOrderHeaders""
    (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",
     ""OrderTotal"",""SlpCode"",""SlpName"",""Status"")
VALUES (@de,@dn,@cc,@cn,@dd,@ot,@sc,@sn,@st)", conn, tx);
            headerCmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            headerCmd.Parameters.Add("@dn", NpgsqlDbType.Integer);
            headerCmd.Parameters.Add("@cc", NpgsqlDbType.Text);
            headerCmd.Parameters.Add("@cn", NpgsqlDbType.Text);
            headerCmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            headerCmd.Parameters.Add("@ot", NpgsqlDbType.Numeric);
            headerCmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            headerCmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            headerCmd.Parameters.Add("@st", NpgsqlDbType.Text);
            await headerCmd.PrepareAsync();

            foreach (var h in headers)
            {
                headerCmd.Parameters["@de"].Value = h.DocEntry;
                headerCmd.Parameters["@dn"].Value = h.DocNum;
                headerCmd.Parameters["@cc"].Value = h.CardCode ?? "";
                headerCmd.Parameters["@cn"].Value = h.CardName ?? "";
                headerCmd.Parameters["@dd"].Value = h.DocDate;
                headerCmd.Parameters["@ot"].Value = h.OrderTotal;
                headerCmd.Parameters["@sc"].Value = h.SlpCode;
                headerCmd.Parameters["@sn"].Value = h.SlpName ?? "";
                headerCmd.Parameters["@st"].Value = h.Status ?? "";
                await headerCmd.ExecuteNonQueryAsync();
            }
        }

        if (lines.Count > 0)
        {
            using var lineCmd = new NpgsqlCommand(@"
INSERT INTO ""OpenOrderLines""
    (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",
     ""Quantity"",""Price"",""LineTotal"",""WhsCode"")
VALUES (@de,@ln,@dd,@ic,@ds,@qty,@pr,@lt,@wc)", conn, tx);
            lineCmd.Parameters.Add("@de", NpgsqlDbType.Integer);
            lineCmd.Parameters.Add("@ln", NpgsqlDbType.Integer);
            lineCmd.Parameters.Add("@dd", NpgsqlDbType.Date);
            lineCmd.Parameters.Add("@ic", NpgsqlDbType.Text);
            lineCmd.Parameters.Add("@ds", NpgsqlDbType.Text);
            lineCmd.Parameters.Add("@qty", NpgsqlDbType.Numeric);
            lineCmd.Parameters.Add("@pr", NpgsqlDbType.Numeric);
            lineCmd.Parameters.Add("@lt", NpgsqlDbType.Numeric);
            lineCmd.Parameters.Add("@wc", NpgsqlDbType.Text);
            await lineCmd.PrepareAsync();

            foreach (var l in lines)
            {
                lineCmd.Parameters["@de"].Value = l.DocEntry;
                lineCmd.Parameters["@ln"].Value = l.LineNum;
                lineCmd.Parameters["@dd"].Value = l.DocDate;
                lineCmd.Parameters["@ic"].Value = l.ItemCode ?? "";
                lineCmd.Parameters["@ds"].Value = l.Dscription ?? "";
                lineCmd.Parameters["@qty"].Value = l.Quantity;
                lineCmd.Parameters["@pr"].Value = l.Price;
                lineCmd.Parameters["@lt"].Value = l.LineTotal;
                lineCmd.Parameters["@wc"].Value = l.WhsCode ?? "";
                await lineCmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] OpenOrders replaced. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task ReplaceInvoiceStatusCacheAsync()
    {
        var rows = await _sqlite.Set<DetailedInvoiceStatusCache>().AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        await DeleteAllAsync(conn, tx, "InvoiceStatusCache");

        if (rows.Count > 0)
        {
            using var cmd = new NpgsqlCommand(@"
INSERT INTO ""InvoiceStatusCache""
    (""SlpCode"",""SalesName"",""PostingDate"",""InvoiceNo"",""ReinvoicedFrom"",""InvoiceStatus"",
     ""PaidDate"",""Customer"",""CashSales"",""CreditSales"",""ReturnedCashInvoice"",
     ""PaymentsStatus"",""CancellationStatus"")
VALUES (@sc,@sn,@ptd,@ino,@rf,@is,@paid,@cust,@cs,@crs,@rci,@ps,@cans)", conn, tx);
            cmd.Parameters.Add("@sc", NpgsqlDbType.Integer);
            cmd.Parameters.Add("@sn", NpgsqlDbType.Text);
            cmd.Parameters.Add("@ptd", NpgsqlDbType.Timestamp);
            cmd.Parameters.Add("@ino", NpgsqlDbType.Text);
            cmd.Parameters.Add("@rf", NpgsqlDbType.Text);
            cmd.Parameters.Add("@is", NpgsqlDbType.Text);
            cmd.Parameters.Add("@paid", NpgsqlDbType.Timestamp);
            cmd.Parameters.Add("@cust", NpgsqlDbType.Text);
            cmd.Parameters.Add("@cs", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@crs", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@rci", NpgsqlDbType.Numeric);
            cmd.Parameters.Add("@ps", NpgsqlDbType.Text);
            cmd.Parameters.Add("@cans", NpgsqlDbType.Text);
            await cmd.PrepareAsync();

            foreach (var r in rows)
            {
                cmd.Parameters["@sc"].Value = (object?)r.SlpCode ?? DBNull.Value;
                cmd.Parameters["@sn"].Value = r.SalesName ?? "";
                cmd.Parameters["@ptd"].Value = (object?)r.PostingDate ?? DBNull.Value;
                cmd.Parameters["@ino"].Value = r.InvoiceNo ?? "";
                cmd.Parameters["@rf"].Value = r.ReinvoicedFrom ?? "";
                cmd.Parameters["@is"].Value = r.InvoiceStatus ?? "";
                cmd.Parameters["@paid"].Value = (object?)r.PaidDate ?? DBNull.Value;
                cmd.Parameters["@cust"].Value = r.Customer ?? "";
                cmd.Parameters["@cs"].Value = r.CashSales;
                cmd.Parameters["@crs"].Value = r.CreditSales;
                cmd.Parameters["@rci"].Value = r.ReturnedCashInvoice;
                cmd.Parameters["@ps"].Value = r.PaymentsStatus ?? "";
                cmd.Parameters["@cans"].Value = r.CancellationStatus ?? "";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        _log.LogInformation("[NeonSync] InvoiceStatusCache replaced: {Count}", rows.Count);
    }
}
