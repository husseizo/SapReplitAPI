using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Neon;

public class NeonProductSyncService
{
    private const int BatchSize = 500;

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
    /// Truncates Neon Products table and reloads from SQLite cache.
    /// Run after a full SAP sync so Neon mirrors the clean (no frozen items) state.
    /// </summary>
    public async Task<int> FullReplaceAsync()
    {
        var rows = await _sqlite.Products.AsNoTracking().ToListAsync();

        var conn = await GetConnectionAsync();

        using (var tx = await conn.BeginTransactionAsync())
        {
            using var cmd = new NpgsqlCommand(@"TRUNCATE ""Products""", conn, tx);
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        for (int off = 0; off < rows.Count; off += BatchSize)
        {
            conn = await GetConnectionAsync();
            var batch = rows.Skip(off).Take(BatchSize).ToList();
            using var tx = await conn.BeginTransactionAsync();
            await UpsertBatchAsync(batch, conn, tx);
            await tx.CommitAsync();
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

    private static async Task UpsertBatchAsync(List<CachedProduct> batch, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        const string insertHeader =
            @"INSERT INTO ""Products"" (""ItemCode"",""ItemName"",""U_Article_No"",""U_MdlTEST"",""U_Item_Name"",""Price"",""Price05"",""TotalOnHand"",""OnHand"",""OnHandQty"",""WhsCode"",""LastUpdated"",""Whs_001"",""Whs_002"",""Whs_003"",""Whs_004"") VALUES ";
        const string onConflict =
            @" ON CONFLICT (""ItemCode"") DO UPDATE SET ""ItemName""=EXCLUDED.""ItemName"",""U_Article_No""=EXCLUDED.""U_Article_No"",""U_MdlTEST""=EXCLUDED.""U_MdlTEST"",""U_Item_Name""=EXCLUDED.""U_Item_Name"",""Price""=EXCLUDED.""Price"",""Price05""=EXCLUDED.""Price05"",""TotalOnHand""=EXCLUDED.""TotalOnHand"",""OnHand""=EXCLUDED.""OnHand"",""OnHandQty""=EXCLUDED.""OnHandQty"",""WhsCode""=EXCLUDED.""WhsCode"",""LastUpdated""=EXCLUDED.""LastUpdated"",""Whs_001""=EXCLUDED.""Whs_001"",""Whs_002""=EXCLUDED.""Whs_002"",""Whs_003""=EXCLUDED.""Whs_003"",""Whs_004""=EXCLUDED.""Whs_004"";";

        var sb = new StringBuilder(insertHeader);
        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"(@p{i}_0,@p{i}_1,@p{i}_2,@p{i}_3,@p{i}_4,@p{i}_5,@p{i}_6,@p{i}_7,@p{i}_8,@p{i}_9,@p{i}_10,@p{i}_11,@p{i}_12,@p{i}_13,@p{i}_14,@p{i}_15)");
        }
        sb.Append(onConflict);

        using var cmd = new NpgsqlCommand(sb.ToString(), conn, tx);

        for (int i = 0; i < batch.Count; i++)
        {
            var p = batch[i];
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
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
