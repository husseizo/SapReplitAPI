using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Neon;
using System.Diagnostics;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Syncs GL account statement entries for the 5 payment accounts directly
/// from SAP B1 (JDT1/OJDT) into Neon's "AccountStatements" table.
///
/// Each run is incremental: fetches rows with RefDate >= (lastSync - 1 day)
/// and upserts on (TransId, Account) — safe to re-run.
/// </summary>
[DisallowConcurrentExecution]
public class AccountStatementSyncJob : IJob
{
    private const string WatermarkKey = "AccountStatement";
    private static readonly DateTime DefaultFrom = new(2024, 1, 1);

    private readonly SapService _sapService;
    private readonly NeonDbContext _neon;
    private readonly CacheDbContext _sqlite;
    private readonly ILogger<AccountStatementSyncJob> _log;

    public AccountStatementSyncJob(
        SapService sapService,
        NeonDbContext neon,
        CacheDbContext sqlite,
        ILogger<AccountStatementSyncJob> log)
    {
        _sapService = sapService;
        _neon       = neon;
        _sqlite     = sqlite;
        _log        = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogInformation("📊 [AccountStatementSync] Starting...");
        var sw = Stopwatch.StartNew();

        try
        {
            var lastSync = await GetWatermarkAsync();
            var from = (lastSync?.AddDays(-1) ?? DefaultFrom); // 1-day overlap catches late postings
            var to   = DateTime.Today;

            _log.LogInformation("📊 [AccountStatementSync] SAP query {From:yyyy-MM-dd} → {To:yyyy-MM-dd}", from, to);

            var rows = _sapService.GetGlAccountStatements(from, to);

            if (rows.Count > 0)
                await UpsertToNeonAsync(rows);

            await SetWatermarkAsync(to);

            _log.LogInformation("✅ [AccountStatementSync] {Count} rows synced in {Sec:F1}s",
                rows.Count, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [AccountStatementSync] Failed");
        }
    }

    private async Task<DateTime?> GetWatermarkAsync() =>
        await _sqlite.SyncMetadata
            .AsNoTracking()
            .Where(m => m.Type == WatermarkKey)
            .Select(m => (DateTime?)m.LastSyncedAt)
            .FirstOrDefaultAsync();

    private async Task SetWatermarkAsync(DateTime ts)
    {
        var meta = await _sqlite.SyncMetadata.FirstOrDefaultAsync(m => m.Type == WatermarkKey);
        if (meta == null)
            await _sqlite.SyncMetadata.AddAsync(new SyncMetadata { Type = WatermarkKey, LastSyncedAt = ts });
        else
            meta.LastSyncedAt = ts;
        await _sqlite.SaveChangesAsync();
    }

    private async Task UpsertToNeonAsync(List<GlAccountStatement> rows)
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();

        using var cmd = new NpgsqlCommand(@"
INSERT INTO ""AccountStatements""
    (""TransId"",""Account"",""AccountName"",""RefDate"",""Debit"",""Credit"",""LineMemo"",
     ""TransType"",""Ref1"",""Ref2"",""PaymentDocEntry"",""PaymentDocNum"",
     ""InvoiceDocEntry"",""InvoiceDocNum"",""CardCode"",""CardName"")
VALUES (@tid,@acc,@anm,@rdt,@dbt,@crd,@lmm,@ttyp,@r1,@r2,@pde,@pdn,@ide,@idn,@cc,@cn)
ON CONFLICT (""TransId"", ""Account"") DO UPDATE SET
    ""AccountName""     = EXCLUDED.""AccountName"",
    ""RefDate""         = EXCLUDED.""RefDate"",
    ""Debit""           = EXCLUDED.""Debit"",
    ""Credit""          = EXCLUDED.""Credit"",
    ""LineMemo""        = EXCLUDED.""LineMemo"",
    ""TransType""       = EXCLUDED.""TransType"",
    ""Ref1""            = EXCLUDED.""Ref1"",
    ""Ref2""            = EXCLUDED.""Ref2"",
    ""PaymentDocEntry"" = EXCLUDED.""PaymentDocEntry"",
    ""PaymentDocNum""   = EXCLUDED.""PaymentDocNum"",
    ""InvoiceDocEntry"" = EXCLUDED.""InvoiceDocEntry"",
    ""InvoiceDocNum""   = EXCLUDED.""InvoiceDocNum"",
    ""CardCode""        = EXCLUDED.""CardCode"",
    ""CardName""        = EXCLUDED.""CardName"";", conn, tx);

        cmd.Parameters.Add("@tid",  NpgsqlDbType.Integer);
        cmd.Parameters.Add("@acc",  NpgsqlDbType.Text);
        cmd.Parameters.Add("@anm",  NpgsqlDbType.Text);
        cmd.Parameters.Add("@rdt",  NpgsqlDbType.TimestampTz);
        cmd.Parameters.Add("@dbt",  NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@crd",  NpgsqlDbType.Numeric);
        cmd.Parameters.Add("@lmm",  NpgsqlDbType.Text);
        cmd.Parameters.Add("@ttyp", NpgsqlDbType.Text);
        cmd.Parameters.Add("@r1",   NpgsqlDbType.Text);
        cmd.Parameters.Add("@r2",   NpgsqlDbType.Text);
        cmd.Parameters.Add("@pde",  NpgsqlDbType.Integer);
        cmd.Parameters.Add("@pdn",  NpgsqlDbType.Integer);
        cmd.Parameters.Add("@ide",  NpgsqlDbType.Integer);
        cmd.Parameters.Add("@idn",  NpgsqlDbType.Integer);
        cmd.Parameters.Add("@cc",   NpgsqlDbType.Text);
        cmd.Parameters.Add("@cn",   NpgsqlDbType.Text);

        foreach (var r in rows)
        {
            cmd.Parameters["@tid"].Value  = r.TransId;
            cmd.Parameters["@acc"].Value  = r.Account;
            cmd.Parameters["@anm"].Value  = r.AccountName;
            cmd.Parameters["@rdt"].Value  = DateTime.SpecifyKind(r.RefDate, DateTimeKind.Utc);
            cmd.Parameters["@dbt"].Value  = r.Debit;
            cmd.Parameters["@crd"].Value  = r.Credit;
            cmd.Parameters["@lmm"].Value  = r.LineMemo;
            cmd.Parameters["@ttyp"].Value = r.TransType;
            cmd.Parameters["@r1"].Value   = r.Ref1;
            cmd.Parameters["@r2"].Value   = r.Ref2;
            cmd.Parameters["@pde"].Value  = (object?)r.PaymentDocEntry ?? DBNull.Value;
            cmd.Parameters["@pdn"].Value  = (object?)r.PaymentDocNum   ?? DBNull.Value;
            cmd.Parameters["@ide"].Value  = (object?)r.InvoiceDocEntry ?? DBNull.Value;
            cmd.Parameters["@idn"].Value  = (object?)r.InvoiceDocNum   ?? DBNull.Value;
            cmd.Parameters["@cc"].Value   = r.CardCode;
            cmd.Parameters["@cn"].Value   = r.CardName;
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}
