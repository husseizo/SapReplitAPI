using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Returns;

public sealed class ReturnsMirrorBackfillReport
{
    public int OrrrHeaders { get; set; }
    public int OrrrLines { get; set; }
    public int OrinHeaders { get; set; }
    public int OrinLines { get; set; }
    public int AffectedInvoiceLines { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Idempotent backfill path: SAP reader -> SQLite mirror -> Neon mirror.
/// No SAP mutations; no direct ORRR reads from Neon.
/// </summary>
public sealed class ReturnsMirrorBackfillService(
    SapService sap,
    CacheDbContext sqlite,
    NeonDbContext neon,
    ReturnRequestCacheService returnCache,
    NeonReturnRequestWriteService returnNeon,
    CreditMemoCacheService creditCache,
    NeonCreditMemoWriteService creditNeon)
{
    public async Task<ReturnsMirrorBackfillReport> RunAsync(CancellationToken ct = default)
    {
        var report = new ReturnsMirrorBackfillReport();

        try
        {
            foreach (var id in sap.GetReturnRequestIdsForBackfill())
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = sap.GetReturnRequestSnapshotByDocEntry(id)
                    ?? throw new InvalidOperationException($"Missing SAP return request {id}; backfill must be retried.");

                await returnCache.RefreshSingleAsync(snapshot.header, snapshot.lines, ct);
                await returnNeon.UpsertReturnRequestAsync(snapshot.header, snapshot.lines, ct);
                report.OrrrHeaders++;
                report.OrrrLines += snapshot.lines.Count;
            }

            foreach (var id in sap.GetCreditMemoIdsForReturnedQtyBackfill())
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = sap.GetCreditMemoSnapshotByDocEntry(id)
                    ?? throw new InvalidOperationException($"Missing SAP credit memo {id}; backfill must be retried.");

                await creditCache.RefreshSingleAsync(snapshot.header, snapshot.lines, ct);
                await creditNeon.UpsertCreditMemoAsync(snapshot.header, snapshot.lines, ct);
                report.OrinHeaders++;
                report.OrinLines += snapshot.lines.Count;
            }

            report.AffectedInvoiceLines = await RecomputeAllInvoiceLineQuantitiesAsync(sqlite, neon, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report.Errors.Add(ex.Message);
        }

        return report;
    }

    private static async Task<int> RecomputeAllInvoiceLineQuantitiesAsync(
        CacheDbContext sqlite,
        NeonDbContext neon,
        CancellationToken ct)
    {
        var sqliteCount = await ExecuteRecomputeAsync(sqlite.Database.GetDbConnection(), ct);
        var neonCount = await ExecuteRecomputeAsync(neon.Database.GetDbConnection(), ct);
        return Math.Max(sqliteCount, neonCount);
    }

    private static async Task<int> ExecuteRecomputeAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var tx = await connection.BeginTransactionAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        if (connection is Npgsql.NpgsqlConnection)
        {
            cmd.CommandText = "LOCK TABLE \"CreditMemoHeaders\", \"CreditMemoLines\", \"ReturnRequests\", \"ReturnRequestLines\", \"InvoiceLines\" IN SHARE ROW EXCLUSIVE MODE";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        cmd.CommandText = ReturnedQuantityMirror.RecomputeSql;
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = PendingReturnQuantityMirror.RecomputeSql;
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = "SELECT COUNT(*) FROM \"InvoiceLines\"";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);

        await tx.CommitAsync(ct);
        return count;
    }
}
