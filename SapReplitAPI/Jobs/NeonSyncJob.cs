using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.Inventory;
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
    private const int BatchSize = 500;

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
                await SyncIfChangedAsync("InvoicePayments", new[] { "InvoicePayment" }, SyncInvoicePaymentsIncrementalAsync);
                await SyncIfChangedAsync("TodayOrders", new[] { "TodayOrder" }, ReplaceTodayOrdersAsync);
                await SyncIfChangedAsync("OpenOrders", new[] { "OpenOrder" }, ReplaceOpenOrdersAsync);
                await SyncIfChangedAsync("InvoiceStatusCache", new[] { "InvoiceStatusCache" }, ReplaceInvoiceStatusCacheAsync);
                await SyncIfChangedAsync("AccountStatements", new[] { "AccountStatement" }, SyncAccountStatementsIncrementalAsync);
                await SyncIfChangedAsync("WarehouseInventory", new[] { "WarehouseInventory.Source" }, ReplaceWarehouseInventoryAsync);
                await SyncIfChangedAsync("BinInventory", new[] { "BinInventory.Source" }, ReplaceBinInventoryAsync);
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
        await RunFullStepAsync("InvoicePayments", new[] { "InvoicePayment" }, ReplaceInvoicePaymentsAsync);
        await RunFullStepAsync("TodayOrders", new[] { "TodayOrder" }, ReplaceTodayOrdersAsync);
        await RunFullStepAsync("OpenOrders", new[] { "OpenOrder" }, ReplaceOpenOrdersAsync);
        await RunFullStepAsync("InvoiceStatusCache", new[] { "InvoiceStatusCache" }, ReplaceInvoiceStatusCacheAsync);
        await RunFullStepAsync("AccountStatements", new[] { "AccountStatement" }, ReplaceAccountStatementsAsync);
        await RunFullStepAsync("WarehouseInventory", new[] { "WarehouseInventory.Source" }, ReplaceWarehouseInventoryAsync);
        await RunFullStepAsync("BinInventory", new[] { "BinInventory.Source" }, ReplaceBinInventoryAsync);
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

        if (conn.State == System.Data.ConnectionState.Broken)
        {
            _log.LogWarning("[NeonSync] Connection was broken — closing and reopening.");
            await conn.CloseAsync();
        }

        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        return conn;
    }

    private static async Task TruncateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, params string[] tables)
    {
        var tableList = string.Join(", ", tables.Select(t => $@"""{t}"""));
        using var cmd = new NpgsqlCommand($"TRUNCATE {tableList}", conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    // Executes a multi-row VALUES insert in one round-trip per batch.
    // buildRow fills the NpgsqlCommand parameters for row i using the per-row suffix "_{i}".
    private static async Task BatchInsertAsync<T>(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<T> rows,
        string insertHeader,   // e.g. INSERT INTO "T" (a,b) VALUES
        string onConflict,     // e.g.  ON CONFLICT ... DO UPDATE SET ...;  (or empty for plain insert)
        int paramsPerRow,
        Action<NpgsqlCommand, T, int> bindRow)
    {
        if (rows.Count == 0) return;

        var sb = new StringBuilder(insertHeader);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('(');
            for (int p = 0; p < paramsPerRow; p++)
            {
                if (p > 0) sb.Append(',');
                sb.Append($"@p{i}_{p}");
            }
            sb.Append(')');
        }
        sb.Append(onConflict);

        using var cmd = new NpgsqlCommand(sb.ToString(), conn, tx);
        for (int i = 0; i < rows.Count; i++)
            bindRow(cmd, rows[i], i);

        await cmd.ExecuteNonQueryAsync();
    }

    // ── Products ─────────────────────────────────────────────────────────────

    private async Task SyncProductsIncrementalAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertProductsBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] Products upserted: {Count}", rows.Count);
    }

    private async Task ReplaceProductsAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "Products");
            await tx.CommitAsync();
        }

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertProductsBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] Products full reconcile: {Count}", rows.Count);
    }

    private static Task UpsertProductsBatchAsync(List<CachedProduct> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""Products"" (""ItemCode"",""ItemName"",""U_Article_No"",""U_MdlTEST"",""U_Item_Name"",""Price"",""Price05"",""TotalOnHand"",""OnHand"",""OnHandQty"",""WhsCode"",""LastUpdated"",""Whs_001"",""Whs_002"",""Whs_003"",""Whs_004"") VALUES ",
            @" ON CONFLICT (""ItemCode"") DO UPDATE SET ""ItemName""=EXCLUDED.""ItemName"",""U_Article_No""=EXCLUDED.""U_Article_No"",""U_MdlTEST""=EXCLUDED.""U_MdlTEST"",""U_Item_Name""=EXCLUDED.""U_Item_Name"",""Price""=EXCLUDED.""Price"",""Price05""=EXCLUDED.""Price05"",""TotalOnHand""=EXCLUDED.""TotalOnHand"",""OnHand""=EXCLUDED.""OnHand"",""OnHandQty""=EXCLUDED.""OnHandQty"",""WhsCode""=EXCLUDED.""WhsCode"",""LastUpdated""=EXCLUDED.""LastUpdated"",""Whs_001""=EXCLUDED.""Whs_001"",""Whs_002""=EXCLUDED.""Whs_002"",""Whs_003""=EXCLUDED.""Whs_003"",""Whs_004""=EXCLUDED.""Whs_004"";",
            16,
            (cmd, p, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Text,      p.ItemCode     ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Text,      p.ItemName     ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Text,      p.U_Article_No ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.Text,      p.U_MdlTEST   ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Text,      p.U_Item_Name  ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Numeric,   p.Price);
                cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Numeric,   p.Price05);
                cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Numeric,   p.TotalOnHand);
                cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Numeric,   p.OnHand);
                cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Numeric,   p.OnHandQty);
                cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Text,      p.WhsCode      ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Timestamp, p.LastUpdated);
                cmd.Parameters.AddWithValue($"@p{i}_12", NpgsqlDbType.Integer,   (object?)p.Whs_001 ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@p{i}_13", NpgsqlDbType.Integer,   (object?)p.Whs_002 ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@p{i}_14", NpgsqlDbType.Integer,   (object?)p.Whs_003 ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@p{i}_15", NpgsqlDbType.Integer,   (object?)p.Whs_004 ?? DBNull.Value);
            });

    // ── Customers ─────────────────────────────────────────────────────────────

    private async Task SyncCustomersIncrementalAsync()
    {
        var rows = await _sqlite.Customers.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertCustomersBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] Customers upserted: {Count}", rows.Count);
    }

    private async Task ReplaceCustomersAsync()
    {
        var rows = await _sqlite.Customers.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "Customers");
            await tx.CommitAsync();
        }

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertCustomersBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] Customers full reconcile: {Count}", rows.Count);
    }

    private static Task UpsertCustomersBatchAsync(List<CachedCustomer> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""Customers"" (""CardCode"",""CardName"",""Balance"",""Region"",""Phone"",""CustomerType"",""SalesPersonName"",""SalesPersonCode"",""TotalSpent"",""VIN1"",""VIN2"",""VIN3"",""AddressesJson"") VALUES ",
            @" ON CONFLICT (""CardCode"") DO UPDATE SET ""CardName""=EXCLUDED.""CardName"",""Balance""=EXCLUDED.""Balance"",""Region""=EXCLUDED.""Region"",""Phone""=EXCLUDED.""Phone"",""CustomerType""=EXCLUDED.""CustomerType"",""SalesPersonName""=EXCLUDED.""SalesPersonName"",""SalesPersonCode""=EXCLUDED.""SalesPersonCode"",""TotalSpent""=EXCLUDED.""TotalSpent"",""VIN1""=EXCLUDED.""VIN1"",""VIN2""=EXCLUDED.""VIN2"",""VIN3""=EXCLUDED.""VIN3"",""AddressesJson""=EXCLUDED.""AddressesJson"";",
            13,
            (cmd, c, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Text,    c.CardCode         ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Text,    c.CardName         ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Numeric, c.Balance);
                cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.Text,    c.Region           ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Text,    c.Phone            ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Text,    c.CustomerType     ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Text,    c.SalesPersonName  ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Integer, (object?)c.SalesPersonCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Numeric, c.TotalSpent);
                cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Text,    c.VIN1             ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Text,    c.VIN2             ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Text,    c.VIN3             ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_12", NpgsqlDbType.Text,    c.AddressesJson    ?? "[]");
            });

    // ── Orders ────────────────────────────────────────────────────────────────

    private async Task SyncOrdersIncrementalAsync()
    {
        var headers = await _sqlite.OrderHeaders.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.OrderLines.AsNoTracking().ToListAsync();
        var conn    = await GetConnectionAsync();

        for (int off = 0; off < headers.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = headers.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertOrderHeadersBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        conn = await GetConnectionAsync();
        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "OrderLines");
            await tx.CommitAsync();
        }
        await InsertOrderLinesBatchedAsync(lines, conn);

        _log.LogInformation("[NeonSync] Orders refreshed. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task ReplaceOrdersAsync()
    {
        var headers = await _sqlite.OrderHeaders.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.OrderLines.AsNoTracking().ToListAsync();
        var conn    = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "OrderLines", "OrderHeaders");
            await tx.CommitAsync();
        }

        for (int off = 0; off < headers.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = headers.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertOrderHeadersBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        conn = await GetConnectionAsync();
        await InsertOrderLinesBatchedAsync(lines, conn);

        _log.LogInformation("[NeonSync] Orders full reconcile. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private static Task UpsertOrderHeadersBatchAsync(List<CachedOrder> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""OrderHeaders"" (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",""SlpCode"",""SlpName"",""CancellationStatus"") VALUES ",
            @" ON CONFLICT (""DocEntry"") DO UPDATE SET ""DocNum""=EXCLUDED.""DocNum"",""CardName""=EXCLUDED.""CardName"",""DocDate""=EXCLUDED.""DocDate"",""OrderValue""=EXCLUDED.""OrderValue"",""Status""=EXCLUDED.""Status"",""SlpCode""=EXCLUDED.""SlpCode"",""SlpName""=EXCLUDED.""SlpName"",""CancellationStatus""=EXCLUDED.""CancellationStatus"";",
            9,
            (cmd, h, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Integer, h.DocEntry);
                cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Integer, h.DocNum);
                cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Text,    h.CardName           ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Date,    h.DocDate);
                cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Numeric, h.OrderValue);
                cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Text,    h.Status             ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Integer, h.SlpCode);
                cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Text,    h.SlpName            ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.Text,    h.CancellationStatus ?? "");
            });

    private async Task InsertOrderLinesBatchedAsync(List<CachedOrderLine> lines, NpgsqlConnection conn)
    {
        for (int off = 0; off < lines.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = lines.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""OrderLines"" (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"") VALUES ",
                ";",
                10,
                (cmd, l, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Integer, l.DocEntry);
                    cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Integer, l.LineNum);
                    cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Date,    l.DocDate);
                    cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Text,    l.ItemCode      ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Text,    l.Dscription    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Numeric, l.Quantity);
                    cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Numeric, l.Price);
                    cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Text,    l.WhsCode       ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.Text,    l.U_ItemName    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_9", NpgsqlDbType.Text,    l.U_Manufacturer ?? "");
                });
            await tx.CommitAsync();
        }
    }

    // ── Invoices ─────────────────────────────────────────────────────────────

    private async Task SyncInvoicesIncrementalAsync()
    {
        var headers = await _sqlite.Invoices.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.InvoiceLines.AsNoTracking().ToListAsync();
        var conn    = await GetConnectionAsync();

        for (int off = 0; off < headers.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = headers.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertInvoiceHeadersBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        conn = await GetConnectionAsync();
        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "InvoiceLines");
            await tx.CommitAsync();
        }
        await InsertInvoiceLinesBatchedAsync(lines, conn);

        _log.LogInformation("[NeonSync] Invoices refreshed. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private async Task ReplaceInvoicesAsync()
    {
        var headers = await _sqlite.Invoices.AsNoTracking().ToListAsync();
        var lines   = await _sqlite.InvoiceLines.AsNoTracking().ToListAsync();
        var conn    = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "InvoiceLines", "Invoices");
            await tx.CommitAsync();
        }

        for (int off = 0; off < headers.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = headers.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertInvoiceHeadersBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        conn = await GetConnectionAsync();
        await InsertInvoiceLinesBatchedAsync(lines, conn);

        _log.LogInformation("[NeonSync] Invoices full reconcile. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    private static Task UpsertInvoiceHeadersBatchAsync(List<CachedInvoice> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""Invoices"" (""DocEntry"",""DocNum"",""InvoiceDocNum"",""DocDate"",""DocStatus"",""Canceled"",""CardCode"",""CardName"",""DocTotal"",""PaidToDate"",""BalanceDue"",""DaysOverdue"",""SalesEmployeeCode"",""SalesEmployeeName"",""GroupNum"",""DocStatusDisplay"") VALUES ",
            @" ON CONFLICT (""DocEntry"") DO UPDATE SET ""DocNum""=EXCLUDED.""DocNum"",""InvoiceDocNum""=EXCLUDED.""InvoiceDocNum"",""DocDate""=EXCLUDED.""DocDate"",""DocStatus""=EXCLUDED.""DocStatus"",""Canceled""=EXCLUDED.""Canceled"",""CardCode""=EXCLUDED.""CardCode"",""CardName""=EXCLUDED.""CardName"",""DocTotal""=EXCLUDED.""DocTotal"",""PaidToDate""=EXCLUDED.""PaidToDate"",""BalanceDue""=EXCLUDED.""BalanceDue"",""DaysOverdue""=EXCLUDED.""DaysOverdue"",""SalesEmployeeCode""=EXCLUDED.""SalesEmployeeCode"",""SalesEmployeeName""=EXCLUDED.""SalesEmployeeName"",""GroupNum""=EXCLUDED.""GroupNum"",""DocStatusDisplay""=EXCLUDED.""DocStatusDisplay"";",
            16,
            (cmd, inv, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Integer, inv.DocEntry);
                cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Integer, inv.DocNum);
                cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Integer, inv.InvoiceDocNum);
                cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.Date,    inv.DocDate);
                cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Text,    inv.DocStatus          ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Text,    inv.Canceled           ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Text,    inv.CardCode           ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Text,    inv.CardName           ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Numeric, inv.DocTotal);
                cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Numeric, inv.PaidToDate);
                cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Numeric, inv.BalanceDue);
                cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Integer, inv.DaysOverdue);
                cmd.Parameters.AddWithValue($"@p{i}_12", NpgsqlDbType.Integer, inv.SalesEmployeeCode);
                cmd.Parameters.AddWithValue($"@p{i}_13", NpgsqlDbType.Text,    inv.SalesEmployeeName  ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_14", NpgsqlDbType.Integer, inv.GroupNum);
                cmd.Parameters.AddWithValue($"@p{i}_15", NpgsqlDbType.Text,    inv.DocStatusDisplay   ?? "");
            });

    private async Task InsertInvoiceLinesBatchedAsync(List<CachedInvoiceLine> lines, NpgsqlConnection conn)
    {
        for (int off = 0; off < lines.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = lines.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""InvoiceLines"" (""DocEntry"",""LineNum"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",""U_Item_Name"",""U_ItemName"",""U_MdlTEST"",""U_MDLTsT"",""U_Manufacturer"") VALUES ",
                ";",
                12,
                (cmd, l, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Integer, l.DocEntry);
                    cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Integer, l.LineNum);
                    cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Text,    l.ItemCode      ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.Text,    l.Dscription    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Numeric, l.Quantity);
                    cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Numeric, l.Price);
                    cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Numeric, l.LineTotal);
                    cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Text,    l.U_Item_Name   ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Text,    l.U_ItemName    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Text,    l.U_MdlTEST    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Text,    l.U_MDLTsT     ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Text,    l.U_Manufacturer ?? "");
                });
            await tx.CommitAsync();
        }
    }

    // ── Invoice Payments ─────────────────────────────────────────────────────

    private async Task SyncInvoicePaymentsIncrementalAsync()
    {
        var rows = await _sqlite.InvoicePayments.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertInvoicePaymentsBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] InvoicePayments upserted: {Count}", rows.Count);
    }

    private async Task ReplaceInvoicePaymentsAsync()
    {
        var rows = await _sqlite.InvoicePayments.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "InvoicePayments");
            await tx.CommitAsync();
        }

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertInvoicePaymentsBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] InvoicePayments full reconcile: {Count}", rows.Count);
    }

    private static Task UpsertInvoicePaymentsBatchAsync(List<CachedInvoicePayment> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""InvoicePayments"" (""DocEntry"",""PaymentDocEntry"",""PaymentNumber"",""InvoiceDocNum"",""PaymentDate"",""CardCode"",""CardName"",""AmountApplied"",""BankTransferAmount"",""BankTransferReference"",""DebitAccountCode"",""DebitAccountName"",""SalesEmployeeCode"",""SalesEmployeeName"",""ClientReference"",""Canceled"",""CounterRef"",""LastUpdated"") VALUES ",
            @" ON CONFLICT (""DocEntry"",""PaymentDocEntry"") DO UPDATE SET ""PaymentNumber""=EXCLUDED.""PaymentNumber"",""InvoiceDocNum""=EXCLUDED.""InvoiceDocNum"",""PaymentDate""=EXCLUDED.""PaymentDate"",""CardCode""=EXCLUDED.""CardCode"",""CardName""=EXCLUDED.""CardName"",""AmountApplied""=EXCLUDED.""AmountApplied"",""BankTransferAmount""=EXCLUDED.""BankTransferAmount"",""BankTransferReference""=EXCLUDED.""BankTransferReference"",""DebitAccountCode""=EXCLUDED.""DebitAccountCode"",""DebitAccountName""=EXCLUDED.""DebitAccountName"",""SalesEmployeeCode""=EXCLUDED.""SalesEmployeeCode"",""SalesEmployeeName""=EXCLUDED.""SalesEmployeeName"",""ClientReference""=EXCLUDED.""ClientReference"",""Canceled""=EXCLUDED.""Canceled"",""CounterRef""=EXCLUDED.""CounterRef"",""LastUpdated""=EXCLUDED.""LastUpdated"";",
            18,
            (cmd, p, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Integer,   p.DocEntry);
                cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Integer,   p.PaymentDocEntry);
                cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Integer,   p.PaymentNumber);
                cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.Integer,   p.InvoiceDocNum);
                cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Date,      p.PaymentDate);
                cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Text,      p.CardCode              ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Text,      p.CardName              ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Numeric,   p.AmountApplied);
                cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Numeric,   p.BankTransferAmount);
                cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Text,      p.BankTransferReference ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Text,      p.DebitAccountCode      ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Text,      p.DebitAccountName      ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_12", NpgsqlDbType.Text,      p.SalesEmployeeCode     ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_13", NpgsqlDbType.Text,      p.SalesEmployeeName     ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_14", NpgsqlDbType.Text,      p.ClientReference       ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_15", NpgsqlDbType.Boolean,   p.Canceled);
                cmd.Parameters.AddWithValue($"@p{i}_16", NpgsqlDbType.Text,      p.CounterRef            ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_17", NpgsqlDbType.Timestamp, p.LastUpdated);
            });

    // ── Today Orders ─────────────────────────────────────────────────────────

    private async Task ReplaceTodayOrdersAsync()
    {
        var headers = await _sqlite.TodayOrderHeaders.AsNoTracking().ToListAsync();
        var lines = await _sqlite.TodayOrderLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "TodayOrderLines", "TodayOrderHeaders");
            await tx.CommitAsync();
        }

        for (int off = 0; off < headers.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = headers.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""TodayOrderHeaders"" (""DocEntry"",""DocNum"",""CardName"",""DocDate"",""OrderValue"",""Status"",""SlpCode"",""SlpName"",""Cancelled"") VALUES ",
                ";",
                9,
                (cmd, h, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Integer, h.DocEntry);
                    cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Integer, h.DocNum);
                    cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Text,    h.CardName ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Date,    h.DocDate);
                    cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Numeric, h.OrderValue);
                    cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Text,    h.Status   ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Integer, (object?)h.SlpCode ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Text,    h.SlpName  ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.Boolean, h.Cancelled);
                });
            await tx.CommitAsync();
        }

        for (int off = 0; off < lines.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = lines.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""TodayOrderLines"" (""DocEntry"",""DocDate"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""WhsCode"",""U_ItemName"",""U_Manufacturer"") VALUES ",
                ";",
                9,
                (cmd, l, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Integer, l.DocEntry);
                    cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Date,    l.DocDate);
                    cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Text,    l.ItemCode      ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Text,    l.Dscription    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Numeric, l.Quantity);
                    cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Numeric, l.Price);
                    cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Text,    l.WhsCode       ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Text,    l.U_ItemName    ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.Text,    l.U_Manufacturer ?? "");
                });
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] TodayOrders replaced. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    // ── Open Orders ──────────────────────────────────────────────────────────

    private async Task ReplaceOpenOrdersAsync()
    {
        var headers = await _sqlite.OpenOrderHeaders.AsNoTracking().ToListAsync();
        var lines = await _sqlite.OpenOrderLines.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "OpenOrderLines", "OpenOrderHeaders");
            await tx.CommitAsync();
        }

        for (int off = 0; off < headers.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = headers.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""OpenOrderHeaders"" (""DocEntry"",""DocNum"",""CardCode"",""CardName"",""DocDate"",""OrderTotal"",""SlpCode"",""SlpName"",""Status"") VALUES ",
                ";",
                9,
                (cmd, h, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Integer, h.DocEntry);
                    cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Integer, h.DocNum);
                    cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Text,    h.CardCode ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Text,    h.CardName ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Date,    h.DocDate);
                    cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Numeric, h.OrderTotal);
                    cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Integer, h.SlpCode);
                    cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Text,    h.SlpName  ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.Text,    h.Status   ?? "");
                });
            await tx.CommitAsync();
        }

        for (int off = 0; off < lines.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = lines.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""OpenOrderLines"" (""DocEntry"",""LineNum"",""DocDate"",""ItemCode"",""Dscription"",""Quantity"",""Price"",""LineTotal"",""WhsCode"") VALUES ",
                ";",
                9,
                (cmd, l, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Integer, l.DocEntry);
                    cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Integer, l.LineNum);
                    cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Date,    l.DocDate);
                    cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Text,    l.ItemCode   ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Text,    l.Dscription ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Numeric, l.Quantity);
                    cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Numeric, l.Price);
                    cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Numeric, l.LineTotal);
                    cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.Text,    l.WhsCode    ?? "");
                });
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] OpenOrders replaced. Headers={Headers}, Lines={Lines}", headers.Count, lines.Count);
    }

    // ── Invoice Status Cache ──────────────────────────────────────────────────

    private async Task ReplaceInvoiceStatusCacheAsync()
    {
        var rows = await _sqlite.Set<DetailedInvoiceStatusCache>().AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "InvoiceStatusCache");
            await tx.CommitAsync();
        }

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""InvoiceStatusCache"" (""SlpCode"",""SalesName"",""PostingDate"",""InvoiceNo"",""ReinvoicedFrom"",""InvoiceStatus"",""PaidDate"",""Customer"",""CashSales"",""CreditSales"",""ReturnedCashInvoice"",""PaymentsStatus"",""CancellationStatus"") VALUES ",
                ";",
                13,
                (cmd, r, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Integer,   (object?)r.SlpCode      ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Text,      r.SalesName             ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Timestamp, (object?)r.PostingDate   ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.Text,      r.InvoiceNo             ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Text,      r.ReinvoicedFrom        ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Text,      r.InvoiceStatus         ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Timestamp, (object?)r.PaidDate      ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Text,      r.Customer              ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Numeric,   r.CashSales);
                    cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Numeric,   r.CreditSales);
                    cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Numeric,   r.ReturnedCashInvoice);
                    cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Text,      r.PaymentsStatus        ?? "");
                    cmd.Parameters.AddWithValue($"@p{i}_12", NpgsqlDbType.Text,      r.CancellationStatus    ?? "");
                });
            await tx.CommitAsync();
        }

        _log.LogInformation("[NeonSync] InvoiceStatusCache replaced: {Count}", rows.Count);
    }

    // ── Account Statements ────────────────────────────────────────────────────

    public async Task SyncAccountStatementsNowAsync() =>
        await SyncAccountStatementsIncrementalAsync();

    private async Task SyncAccountStatementsIncrementalAsync()
    {
        var rows = await _sqlite.AccountStatements.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();
        await UpsertAccountStatementsBatchedAsync(rows, conn);
        _log.LogInformation("[NeonSync] AccountStatements upserted: {Count}", rows.Count);
    }

    private async Task ReplaceAccountStatementsAsync()
    {
        var rows = await _sqlite.AccountStatements.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            await TruncateAsync(conn, tx, "AccountStatements");
            await tx.CommitAsync();
        }

        await UpsertAccountStatementsBatchedAsync(rows, conn);
        _log.LogInformation("[NeonSync] AccountStatements full reconcile: {Count}", rows.Count);
    }

    private async Task UpsertAccountStatementsBatchedAsync(List<GlAccountStatement> rows, NpgsqlConnection conn)
    {
        if (rows.Count == 0) return;

        for (int offset = 0; offset < rows.Count; offset += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(offset).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await BatchInsertAsync(conn, tx, batch,
                @"INSERT INTO ""AccountStatements"" (""TransId"",""Account"",""AccountName"",""RefDate"",""Debit"",""Credit"",""LineMemo"",""TransType"",""Ref1"",""Ref2"",""PaymentDocEntry"",""PaymentDocNum"",""InvoiceDocEntry"",""InvoiceDocNum"",""CardCode"",""CardName"") VALUES ",
                @" ON CONFLICT (""TransId"", ""Account"") DO UPDATE SET ""AccountName""=EXCLUDED.""AccountName"",""RefDate""=EXCLUDED.""RefDate"",""Debit""=EXCLUDED.""Debit"",""Credit""=EXCLUDED.""Credit"",""LineMemo""=EXCLUDED.""LineMemo"",""TransType""=EXCLUDED.""TransType"",""Ref1""=EXCLUDED.""Ref1"",""Ref2""=EXCLUDED.""Ref2"",""PaymentDocEntry""=EXCLUDED.""PaymentDocEntry"",""PaymentDocNum""=EXCLUDED.""PaymentDocNum"",""InvoiceDocEntry""=EXCLUDED.""InvoiceDocEntry"",""InvoiceDocNum""=EXCLUDED.""InvoiceDocNum"",""CardCode""=EXCLUDED.""CardCode"",""CardName""=EXCLUDED.""CardName"";",
                16,
                (cmd, r, i) =>
                {
                    cmd.Parameters.AddWithValue($"@p{i}_0",  NpgsqlDbType.Integer,    r.TransId);
                    cmd.Parameters.AddWithValue($"@p{i}_1",  NpgsqlDbType.Text,       r.Account);
                    cmd.Parameters.AddWithValue($"@p{i}_2",  NpgsqlDbType.Text,       r.AccountName);
                    cmd.Parameters.AddWithValue($"@p{i}_3",  NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(r.RefDate, DateTimeKind.Utc));
                    cmd.Parameters.AddWithValue($"@p{i}_4",  NpgsqlDbType.Numeric,    r.Debit);
                    cmd.Parameters.AddWithValue($"@p{i}_5",  NpgsqlDbType.Numeric,    r.Credit);
                    cmd.Parameters.AddWithValue($"@p{i}_6",  NpgsqlDbType.Text,       r.LineMemo);
                    cmd.Parameters.AddWithValue($"@p{i}_7",  NpgsqlDbType.Text,       r.TransType);
                    cmd.Parameters.AddWithValue($"@p{i}_8",  NpgsqlDbType.Text,       r.Ref1);
                    cmd.Parameters.AddWithValue($"@p{i}_9",  NpgsqlDbType.Text,       r.Ref2);
                    cmd.Parameters.AddWithValue($"@p{i}_10", NpgsqlDbType.Integer,    (object?)r.PaymentDocEntry ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_11", NpgsqlDbType.Integer,    (object?)r.PaymentDocNum   ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_12", NpgsqlDbType.Integer,    (object?)r.InvoiceDocEntry ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_13", NpgsqlDbType.Integer,    (object?)r.InvoiceDocNum   ?? DBNull.Value);
                    cmd.Parameters.AddWithValue($"@p{i}_14", NpgsqlDbType.Text,       r.CardCode);
                    cmd.Parameters.AddWithValue($"@p{i}_15", NpgsqlDbType.Text,       r.CardName);
                });
            await tx.CommitAsync();
            _log.LogInformation("[NeonSync] AccountStatements batch committed: {From}-{To} of {Total}",
                offset + 1, offset + batch.Count, rows.Count);
        }
    }

    // ── Warehouse Inventory ───────────────────────────────────────────────────
    // Full replace: TRUNCATE + all batch INSERTs inside ONE PostgreSQL transaction.
    // If any insert fails, the entire transaction rolls back — Neon never left empty.

    private async Task ReplaceWarehouseInventoryAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await _sqlite.WarehouseInventories.AsNoTracking().OrderBy(w => w.Id).ToListAsync();
        int batchCount = (rows.Count + BatchSize - 1) / BatchSize;

        _log.LogInformation("[NeonSync] WarehouseInventory — {Count} SQLite rows | {Batches} batches", rows.Count, batchCount);

        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using (var cmd = new NpgsqlCommand(@"TRUNCATE ""WarehouseInventory""", conn, tx))
                await cmd.ExecuteNonQueryAsync();

            for (int off = 0; off < rows.Count; off += BatchSize)
            {
                var batch = rows.Skip(off).Take(BatchSize).ToList();
                await InsertWarehouseInventoryBatchAsync(batch, conn, tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        sw.Stop();
        _log.LogInformation("[NeonSync] WarehouseInventory replaced — rows: {Count} | batches: {Batches} | duration: {S:F1}s",
            rows.Count, batchCount, sw.Elapsed.TotalSeconds);
    }

    private static Task InsertWarehouseInventoryBatchAsync(List<WarehouseInventory> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""WarehouseInventory"" (""ItemCode"",""WhsCode"",""WarehouseName"",""OnHand"",""IsCommitted"",""OnOrder"",""AvailableToSell"",""IsBinManaged"",""LastUpdated"") VALUES ",
            ";",
            9,
            (cmd, w, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Text,        w.ItemCode       ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Text,        w.WhsCode        ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Text,        w.WarehouseName  ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Numeric,     w.OnHand);
                cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Numeric,     w.IsCommitted);
                cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.Numeric,     w.OnOrder);
                cmd.Parameters.AddWithValue($"@p{i}_6", NpgsqlDbType.Numeric,     w.AvailableToSell);
                cmd.Parameters.AddWithValue($"@p{i}_7", NpgsqlDbType.Boolean,     w.IsBinManaged);
                cmd.Parameters.AddWithValue($"@p{i}_8", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(w.LastUpdated, DateTimeKind.Utc));
            });

    // ── Bin Inventory ─────────────────────────────────────────────────────────
    // Full replace: TRUNCATE + all batch INSERTs inside ONE PostgreSQL transaction.
    // If any batch fails the entire transaction rolls back — Neon never left empty.
    // Source: SQLite BinInventory (positive-stock only — zero-stock rows absent by design).

    private async Task ReplaceBinInventoryAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await _sqlite.BinInventories
            .AsNoTracking()
            .OrderBy(b => b.Id)
            .ToListAsync();

        int batchCount = (rows.Count + BatchSize - 1) / BatchSize;

        _log.LogInformation("[NeonSync] BinInventory — source: {SourceWm} | {Count} SQLite rows | {Batches} batches",
            (await _sqlite.SyncMetadata.AsNoTracking()
                .Where(m => m.Type == "BinInventory.Source")
                .Select(m => (DateTime?)m.LastSyncedAt)
                .FirstOrDefaultAsync())?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "(none)",
            rows.Count, batchCount);

        var conn = await GetConnectionAsync();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using (var cmd = new NpgsqlCommand(@"TRUNCATE ""BinInventory""", conn, tx))
                await cmd.ExecuteNonQueryAsync();

            for (int off = 0; off < rows.Count; off += BatchSize)
            {
                var batch = rows.Skip(off).Take(BatchSize).ToList();
                await InsertBinInventoryBatchAsync(batch, conn, tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        sw.Stop();
        _log.LogInformation(
            "[NeonSync] BinInventory replaced — rows: {Count} | batches: {Batches} | duration: {S:F1}s",
            rows.Count, batchCount, sw.Elapsed.TotalSeconds);
    }

    private static Task InsertBinInventoryBatchAsync(List<BinInventory> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
        => BatchInsertAsync(conn, tx, batch,
            @"INSERT INTO ""BinInventory"" (""ItemCode"",""WhsCode"",""BinAbsEntry"",""BinCode"",""BinOnHand"",""LastUpdated"") VALUES ",
            ";",
            6,
            (cmd, b, i) =>
            {
                cmd.Parameters.AddWithValue($"@p{i}_0", NpgsqlDbType.Text,        b.ItemCode ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_1", NpgsqlDbType.Text,        b.WhsCode  ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_2", NpgsqlDbType.Integer,     b.BinAbsEntry);
                cmd.Parameters.AddWithValue($"@p{i}_3", NpgsqlDbType.Text,        b.BinCode  ?? "");
                cmd.Parameters.AddWithValue($"@p{i}_4", NpgsqlDbType.Numeric,     b.BinOnHand);
                cmd.Parameters.AddWithValue($"@p{i}_5", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(b.LastUpdated, DateTimeKind.Utc));
            });
}
