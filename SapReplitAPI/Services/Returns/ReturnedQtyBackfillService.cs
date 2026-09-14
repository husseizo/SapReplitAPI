using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Returns;

public sealed record ReturnedQtyStoreReport(int InvoiceLinesScanned, int InvoiceLinesChanged,
    decimal PathAQty, decimal PathBQty);

public sealed class ReturnedQtyBackfillReport
{
    public ReturnedQtyStoreReport? SQLite { get; set; }
    public ReturnedQtyStoreReport? Neon { get; set; }
    public int CreditMemosScanned { get; set; }
    public List<string> Errors { get; } = new();
    public bool Complete => Errors.Count == 0 && SQLite != null && Neon != null;
}

/// <summary>Explicit, repeatable maintenance operation. Reads SAP; only writes mirror
/// credit memo snapshots and InvoiceLines.ReturnedQty. Does not start scheduled jobs.</summary>
public sealed class ReturnedQtyBackfillService(SapService sap, CacheDbContext sqlite,
    NeonDbContext neon, CreditMemoCacheService creditCache, NeonCreditMemoWriteService creditNeon)
{
    public async Task<ReturnedQtyBackfillReport> RunAsync(CancellationToken ct = default)
    {
        var report = new ReturnedQtyBackfillReport();
        try
        {
            // Capture before snapshots: event writers also recompute invoice quantities.
            var sqliteBefore = await ReadAsync(sqlite.Database.GetDbConnection(), null, ct);
            var neonBefore = await ReadAsync(neon.Database.GetDbConnection(), null, ct);
            // Include canceled documents so historical cancellation flags are refreshed.
            foreach (var id in sap.GetCreditMemoIdsForReturnedQtyBackfill())
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = sap.GetCreditMemoSnapshotByDocEntry(id)
                    ?? throw new InvalidOperationException($"Missing SAP credit memo {id}; backfill must be retried.");
                await creditCache.RefreshSingleAsync(snapshot.header, snapshot.lines, ct);
                await creditNeon.UpsertCreditMemoAsync(snapshot.header, snapshot.lines, ct);
                report.CreditMemosScanned++;
            }
            report.SQLite = await RecomputeAsync(sqlite.Database.GetDbConnection(), sqliteBefore, ct);
            report.Neon = await RecomputeAsync(neon.Database.GetDbConnection(), neonBefore, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Both mirrors are independently transactional. A failed run is never marked complete.
            report.Errors.Add(ex.Message);
        }
        return report;
    }

    public static async Task<Dictionary<(int, int), decimal>> ReadAsync(DbConnection connection,
        DbTransaction? transaction, CancellationToken ct = default)
    {
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT \"DocEntry\", \"LineNum\", \"ReturnedQty\" FROM \"InvoiceLines\"";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new Dictionary<(int, int), decimal>();
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)), Convert.ToDecimal(reader.GetValue(2)));
        return rows;
    }

    public static async Task<ReturnedQtyStoreReport> RecomputeAsync(DbConnection connection,
        Dictionary<(int, int), decimal>? before = null, CancellationToken ct = default)
    {
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        if (connection is Npgsql.NpgsqlConnection)
        {
            // Same order as CM writers; prevent CM changes while their totals are being read.
            cmd.CommandText = "LOCK TABLE \"CreditMemoHeaders\", \"CreditMemoLines\", \"InvoiceLines\" IN SHARE ROW EXCLUSIVE MODE";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        before ??= await ReadAsync(connection, tx, ct);
        cmd.CommandText = ReturnedQuantityMirror.RecomputeSql;
        await cmd.ExecuteNonQueryAsync(ct);
        var after = await ReadAsync(connection, tx, ct);
        cmd.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN c."BaseType" = 13 THEN c."Quantity" ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN c."BaseType" = 234000031 THEN c."Quantity" ELSE 0 END), 0)
            FROM "CreditMemoLines" c JOIN "CreditMemoHeaders" h ON h."DocEntry" = c."DocEntry"
            JOIN "InvoiceLines" i ON i."DocEntry" = c."InvoiceDocEntry" AND i."LineNum" = c."InvoiceLineNum"
            WHERE h."Canceled" = 'N'
            """;
        decimal pathA, pathB;
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            pathA = Convert.ToDecimal(reader.GetValue(0));
            pathB = Convert.ToDecimal(reader.GetValue(1));
        }
        await tx.CommitAsync(ct);
        return new(after.Count, after.Count(x => !before.TryGetValue(x.Key, out var old) || old != x.Value), pathA, pathB);
    }
}
