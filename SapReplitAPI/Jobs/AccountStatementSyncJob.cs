using Microsoft.EntityFrameworkCore;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services;
using System.Diagnostics;

namespace SapReplitAPI.Jobs;

/// <summary>
/// SAP B1 → SQLite sync for GL account statements (JDT1/OJDT) for the 5 payment
/// accounts (Cash on Hand, CRDB, M-Pesa Lipa, AAL NMB, Tigo Lipa).
///
/// Runs hourly. Incremental: fetches from (lastSync - 1 day) to today and upserts
/// on (TransId, Account). NeonSyncJob picks up from SQLite and mirrors to Neon.
/// </summary>
[DisallowConcurrentExecution]
public class AccountStatementSyncJob : IJob
{
    private const string WatermarkKey = "AccountStatement";
    private static readonly DateTime DefaultFrom = new(2024, 1, 1);

    private readonly SapService _sapService;
    private readonly CacheDbContext _sqlite;
    private readonly ILogger<AccountStatementSyncJob> _log;

    public AccountStatementSyncJob(
        SapService sapService,
        CacheDbContext sqlite,
        ILogger<AccountStatementSyncJob> log)
    {
        _sapService = sapService;
        _sqlite     = sqlite;
        _log        = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogInformation("📊 [AccountStatementSync] Starting SAP → SQLite sync...");
        var sw = Stopwatch.StartNew();

        try
        {
            var lastSync = await GetWatermarkAsync();
            var from = lastSync?.AddDays(-1) ?? DefaultFrom; // 1-day overlap for late postings
            var to   = DateTime.Today;

            _log.LogInformation("📊 [AccountStatementSync] SAP query {From:yyyy-MM-dd} → {To:yyyy-MM-dd}", from, to);

            var rows = _sapService.GetGlAccountStatements(from, to);

            if (rows.Count > 0)
                await UpsertToSqliteAsync(rows);

            await SetWatermarkAsync(to);

            _log.LogInformation("✅ [AccountStatementSync] {Count} rows → SQLite in {Sec:F1}s",
                rows.Count, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [AccountStatementSync] Failed");
        }
    }

    private async Task UpsertToSqliteAsync(List<GlAccountStatement> rows)
    {
        using var tx = await _sqlite.Database.BeginTransactionAsync();

        foreach (var r in rows)
        {
            await _sqlite.Database.ExecuteSqlRawAsync(@"
INSERT INTO ""AccountStatements""
    (""TransId"",""Account"",""AccountName"",""RefDate"",""Debit"",""Credit"",""LineMemo"",
     ""TransType"",""Ref1"",""Ref2"",""PaymentDocEntry"",""PaymentDocNum"",
     ""InvoiceDocEntry"",""InvoiceDocNum"",""CardCode"",""CardName"")
VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15})
ON CONFLICT(""TransId"",""Account"") DO UPDATE SET
    ""AccountName""     = excluded.""AccountName"",
    ""RefDate""         = excluded.""RefDate"",
    ""Debit""           = excluded.""Debit"",
    ""Credit""          = excluded.""Credit"",
    ""LineMemo""        = excluded.""LineMemo"",
    ""TransType""       = excluded.""TransType"",
    ""Ref1""            = excluded.""Ref1"",
    ""Ref2""            = excluded.""Ref2"",
    ""PaymentDocEntry"" = excluded.""PaymentDocEntry"",
    ""PaymentDocNum""   = excluded.""PaymentDocNum"",
    ""InvoiceDocEntry"" = excluded.""InvoiceDocEntry"",
    ""InvoiceDocNum""   = excluded.""InvoiceDocNum"",
    ""CardCode""        = excluded.""CardCode"",
    ""CardName""        = excluded.""CardName""",
                r.TransId,
                r.Account,
                r.AccountName,
                r.RefDate.ToString("yyyy-MM-dd HH:mm:ss"),
                r.Debit,
                r.Credit,
                r.LineMemo,
                r.TransType,
                r.Ref1,
                r.Ref2,
                (object?)r.PaymentDocEntry ?? DBNull.Value,
                (object?)r.PaymentDocNum   ?? DBNull.Value,
                (object?)r.InvoiceDocEntry ?? DBNull.Value,
                (object?)r.InvoiceDocNum   ?? DBNull.Value,
                r.CardCode,
                r.CardName);
        }

        await tx.CommitAsync();
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
}
