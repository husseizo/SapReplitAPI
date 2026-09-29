using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Neon;

public class NeonProductSyncService
{
    // Shared with NeonSyncJob.ReplaceProductsAsync — same batch size semantics on
    // both full-replace paths.
    private const int BatchSize = SapReplitAPI.Jobs.NeonSyncJob.BatchSize;

    private readonly CacheDbContext _sqlite;
    private readonly NeonDbContext _neon;
    private readonly ILogger<NeonProductSyncService> _log;

    public NeonProductSyncService(CacheDbContext sqlite, NeonDbContext neon, ILogger<NeonProductSyncService> log)
    {
        _sqlite = sqlite;
        _neon = neon;
        _log = log;
    }

    /// <summary>
    /// Replaces Neon Products with the current SQLite cache. Run after a full SAP
    /// sync so Neon mirrors the clean (no frozen items) state. This is the
    /// admin/manual path (POST /api/products/sync-to-neon) — it must stay atomically
    /// equivalent to NeonSyncJob.ReplaceProductsAsync (the scheduled full-reconcile
    /// path): one connection, one transaction, DELETE (not TRUNCATE — see
    /// NeonSyncJob.DeleteAllRowsAsync's doc comment on why TRUNCATE's ACCESS
    /// EXCLUSIVE lock would block every concurrent reader for the whole reload),
    /// every batch reloaded in that same transaction, one COMMIT at the end. Any
    /// failure rolls back the whole transaction, including the DELETE, so readers
    /// never observe an empty or partially-rebuilt table. Previously this method
    /// truncated in its own committed transaction and then committed each batch
    /// separately — exactly the bug NeonSyncJob.ReplaceProductsAsync was fixed for,
    /// reintroduced here because the two paths were independent implementations.
    /// Row projection (column list, ON CONFLICT clause, value binding) is now the
    /// same NeonSyncJob.UpsertProductsBatchAsync both paths share — not a second
    /// copy — so the two can no longer silently drift from each other.
    /// </summary>
    public async Task<int> FullReplaceAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();
        var conn = await GetConnectionAsync();

        using var tx = await conn.BeginTransactionAsync();
        try
        {
            await SapReplitAPI.Jobs.NeonSyncJob.DeleteAllRowsAsync(conn, tx, "Products");
            for (int off = 0; off < rows.Count; off += BatchSize)
            {
                var batch = rows.Skip(off).Take(BatchSize).ToList();
                await SapReplitAPI.Jobs.NeonSyncJob.UpsertProductsBatchAsync(batch, conn, tx);
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        _log.LogInformation("[NeonProductSync] Full replace done: {Count} products pushed to Neon", rows.Count);
        return rows.Count;
    }

    private async Task<NpgsqlConnection> GetConnectionAsync()
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();

        if (conn.State == System.Data.ConnectionState.Broken)
        {
            _log.LogWarning("[NeonProductSync] Connection broken — reopening.");
            await conn.CloseAsync();
        }

        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        return conn;
    }

    /// <summary>
    /// Updates ONLY the price column for the specified price list on a single Neon product row.
    /// Returns true if a row was updated. Does not touch stock or other price list columns.
    /// </summary>
    public async Task<bool> UpdatePriceOnlyAsync(
        string itemCode, int priceListNum, decimal newPrice,
        CancellationToken ct = default)
    {
        string column = priceListNum switch
        {
            1 => "Price01",
            2 => "Price02",
            3 => "Price",
            4 => "Price04",
            5 => "Price05",
            _ => throw new ArgumentOutOfRangeException(nameof(priceListNum))
        };

        var conn = await GetConnectionAsync();
        using var cmd = new NpgsqlCommand(
            $@"UPDATE ""Products"" SET ""{column}"" = @price, ""LastUpdated"" = @ts WHERE ""ItemCode"" = @code",
            conn);
        cmd.Parameters.AddWithValue("@price", NpgsqlDbType.Numeric, newPrice);
        cmd.Parameters.AddWithValue("@ts",    NpgsqlDbType.Timestamp, DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@code",  NpgsqlDbType.Text, itemCode);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }
}
