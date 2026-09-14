using System.Data.Common;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Returns;

/// <summary>Invoice refreshes do not own return quantities. Preserve them in the same
/// transaction as line replacement; credit memo reconciliation owns corrections.</summary>
public static class ReturnedQuantityMirror
{
    public static async Task PreserveAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyCollection<CachedInvoiceLine> lines, CancellationToken ct = default)
    {
        var target = lines.ToDictionary(l => (l.DocEntry, l.LineNum));
        foreach (var chunk in lines.Select(l => l.DocEntry).Distinct().Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT \"DocEntry\", \"LineNum\", \"ReturnedQty\" FROM \"InvoiceLines\" WHERE \"DocEntry\" IN ({string.Join(",", chunk)})";
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (target.TryGetValue((reader.GetInt32(0), reader.GetInt32(1)), out var line))
                    line.ReturnedQty = Convert.ToDecimal(reader.GetValue(2));
        }
    }

    // Resolved references include direct RIN1 -> INV1 and RIN1 -> RRR1 -> INV1.
    // Only call global recomputation after all historical credit memo snapshots are loaded.
    public const string RecomputeSql = """
        UPDATE "InvoiceLines"
        SET "ReturnedQty" = (
            SELECT COALESCE(SUM(cl."Quantity"), 0)
            FROM "CreditMemoLines" cl
            JOIN "CreditMemoHeaders" ch ON ch."DocEntry" = cl."DocEntry"
            WHERE cl."InvoiceDocEntry" = "InvoiceLines"."DocEntry"
              AND cl."InvoiceLineNum" = "InvoiceLines"."LineNum"
              AND ch."Canceled" = 'N'
        )
        """;
}
