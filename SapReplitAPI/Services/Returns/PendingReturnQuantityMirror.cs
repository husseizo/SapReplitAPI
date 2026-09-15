using System.Data.Common;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Returns;

/// <summary>
/// Pending return quantities are derived from open ORRR lines linked to invoice lines.
/// Invoice refreshes do not own this value, so preserve before replacement and recompute from mirror data.
/// </summary>
public static class PendingReturnQuantityMirror
{
    public static async Task PreserveAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyCollection<CachedInvoiceLine> lines, CancellationToken ct = default)
    {
        var target = lines.ToDictionary(l => (l.DocEntry, l.LineNum));
        foreach (var chunk in lines.Select(l => l.DocEntry).Distinct().Chunk(400))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT \"DocEntry\", \"LineNum\", \"PendingReturnQty\" FROM \"InvoiceLines\" WHERE \"DocEntry\" IN ({string.Join(",", chunk)})";
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (target.TryGetValue((reader.GetInt32(0), reader.GetInt32(1)), out var line))
                    line.PendingReturnQty = Convert.ToDecimal(reader.GetValue(2));
            }
        }
    }

    public const string RecomputeSql = """
        UPDATE "InvoiceLines"
        SET "PendingReturnQty" = (
            SELECT COALESCE(SUM(rrl."OpenQty"), 0)
            FROM "ReturnRequestLines" rrl
            JOIN "ReturnRequests" rr ON rr."DocEntry" = rrl."DocEntry"
            WHERE rrl."BaseType" = 13
              AND rrl."BaseEntry" = "InvoiceLines"."DocEntry"
              AND rrl."BaseLine" = "InvoiceLines"."LineNum"
              AND rr."Canceled" = 'N'
              AND rr."DocStatus" = 'O'
              AND rrl."LineStatus" = 'O'
        )
        """;

    public static async Task RecomputeForInvoiceDocEntriesAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyCollection<int> invoiceDocEntries, CancellationToken ct = default)
    {
        if (invoiceDocEntries.Count == 0) return;

        foreach (var chunk in invoiceDocEntries.Distinct().Chunk(300))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                UPDATE "InvoiceLines"
                SET "PendingReturnQty" = (
                    SELECT COALESCE(SUM(rrl."OpenQty"), 0)
                    FROM "ReturnRequestLines" rrl
                    JOIN "ReturnRequests" rr ON rr."DocEntry" = rrl."DocEntry"
                    WHERE rrl."BaseType" = 13
                      AND rrl."BaseEntry" = "InvoiceLines"."DocEntry"
                      AND rrl."BaseLine" = "InvoiceLines"."LineNum"
                      AND rr."Canceled" = 'N'
                      AND rr."DocStatus" = 'O'
                      AND rrl."LineStatus" = 'O'
                )
                WHERE "DocEntry" IN ({string.Join(",", chunk)})
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
