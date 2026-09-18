using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Invoice;

/// <summary>
/// Backfills BaseType/BaseEntry/BaseLine onto every existing InvoiceLines row by reading
/// INV1 from SAP and issuing targeted UPDATE statements against SQLite and Neon.
///
/// Only the three base-ref columns are touched — Quantity, Price, ReturnedQty, and
/// PendingReturnQty are never modified.
/// </summary>
public sealed class InvoiceBaseRefBackfillService
{
    private readonly CacheDbContext _sqlite;
    private readonly SapService _sap;
    private readonly NeonDbContext? _neon;
    private readonly ILogger<InvoiceBaseRefBackfillService> _logger;

    public InvoiceBaseRefBackfillService(
        CacheDbContext sqlite,
        SapService sap,
        NeonDbContext? neon,
        ILogger<InvoiceBaseRefBackfillService> logger)
    {
        _sqlite = sqlite;
        _sap    = sap;
        _neon   = neon;
        _logger = logger;
    }

    public sealed record BackfillResult(
        int DocEntriesScanned,
        int LinesUpdated,
        int DocEntriesWithErrors,
        IReadOnlyList<string> Errors);

    public async Task<BackfillResult> RunAsync(CancellationToken ct = default)
    {
        // 1) Distinct DocEntry values that have any InvoiceLines row.
        var docEntries = await _sqlite.InvoiceLines
            .AsNoTracking()
            .Select(l => l.DocEntry)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(ct);

        _logger.LogInformation("[InvoiceBackfill] Starting base-ref backfill for {Count} invoices.", docEntries.Count);

        int totalLines    = 0;
        int errorCount    = 0;
        var errors        = new List<string>();

        // Open connections once.
        var sqliteConn = (SqliteConnection)_sqlite.Database.GetDbConnection();
        if (sqliteConn.State != ConnectionState.Open)
            await sqliteConn.OpenAsync(ct);

        NpgsqlConnection? neonConn = null;
        if (_neon is not null)
        {
            neonConn = (NpgsqlConnection)_neon.Database.GetDbConnection();
            if (neonConn.State != ConnectionState.Open)
                await neonConn.OpenAsync(ct);
        }

        foreach (var docEntry in docEntries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var refs = _sap.GetInvoiceLineBaseRefs(docEntry);
                if (refs.Count == 0) continue;

                // Update SQLite
                await UpdateSqliteAsync(sqliteConn, docEntry, refs, ct);

                // Update Neon
                if (neonConn is not null)
                    await UpdateNeonAsync(neonConn, docEntry, refs, ct);

                totalLines += refs.Count;
            }
            catch (Exception ex)
            {
                errorCount++;
                var msg = $"DocEntry={docEntry}: {ex.GetType().Name}: {ex.Message}";
                errors.Add(msg);
                _logger.LogWarning("[InvoiceBackfill] {Error}", msg);
            }
        }

        _logger.LogInformation(
            "[InvoiceBackfill] Complete: {DocCount} invoices, {Lines} lines, {Errors} errors.",
            docEntries.Count, totalLines, errorCount);

        return new BackfillResult(docEntries.Count, totalLines, errorCount, errors);
    }

    private static async Task UpdateSqliteAsync(
        SqliteConnection conn,
        int docEntry,
        List<(int LineNum, int BaseType, int? BaseEntry, int? BaseLine)> refs,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE ""InvoiceLines""
SET    ""BaseType""  = $BaseType,
       ""BaseEntry"" = $BaseEntry,
       ""BaseLine""  = $BaseLine
WHERE  ""DocEntry""  = $DocEntry
  AND  ""LineNum""   = $LineNum";

        var pDocEntry  = cmd.Parameters.Add("$DocEntry",  SqliteType.Integer);
        var pLineNum   = cmd.Parameters.Add("$LineNum",   SqliteType.Integer);
        var pBaseType  = cmd.Parameters.Add("$BaseType",  SqliteType.Integer);
        var pBaseEntry = cmd.Parameters.Add("$BaseEntry", SqliteType.Integer);
        var pBaseLine  = cmd.Parameters.Add("$BaseLine",  SqliteType.Integer);

        foreach (var (lineNum, bt, be, bl) in refs)
        {
            pDocEntry.Value  = docEntry;
            pLineNum.Value   = lineNum;
            pBaseType.Value  = bt;
            pBaseEntry.Value = (object?)be  ?? DBNull.Value;
            pBaseLine.Value  = (object?)bl  ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpdateNeonAsync(
        NpgsqlConnection conn,
        int docEntry,
        List<(int LineNum, int BaseType, int? BaseEntry, int? BaseLine)> refs,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE ""InvoiceLines""
SET    ""BaseType""  = @BaseType,
       ""BaseEntry"" = @BaseEntry,
       ""BaseLine""  = @BaseLine
WHERE  ""DocEntry""  = @DocEntry
  AND  ""LineNum""   = @LineNum";

        foreach (var (lineNum, bt, be, bl) in refs)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@DocEntry",  NpgsqlDbType.Integer, docEntry);
            cmd.Parameters.AddWithValue("@LineNum",   NpgsqlDbType.Integer, lineNum);
            cmd.Parameters.AddWithValue("@BaseType",  NpgsqlDbType.Integer, bt);
            cmd.Parameters.AddWithValue("@BaseEntry", be.HasValue ? (object)be.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@BaseLine",  bl.HasValue ? (object)bl.Value : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
