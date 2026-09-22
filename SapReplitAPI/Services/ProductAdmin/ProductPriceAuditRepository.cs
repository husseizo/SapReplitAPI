using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SapReplitAPI.Models.ProductAdmin;

namespace SapReplitAPI.Services.ProductAdmin;

/// <summary>
/// Durable write-through audit log for product price mutations.
/// Two-phase write: InsertAsync (Pending) before SAP mutation; SetTerminalAsync after.
/// Uses its own dbo.ProductPriceAuditLog table — separate from ZfAdminAuditLog.
/// </summary>
public class ProductPriceAuditRepository
{
    private readonly string _cs;

    protected ProductPriceAuditRepository() => _cs = "";

    public ProductPriceAuditRepository(IConfiguration cfg)
        => _cs = cfg.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("MolasIntegration connection string missing.");

    // ── Startup DDL ───────────────────────────────────────────────────────────

    // Runtime login requires only SELECT, INSERT, UPDATE — NOT CREATE TABLE.
    // If the table does not exist and the login lacks CREATE TABLE, this throws with SQL error 262.
    // In that case: run Scripts/ProductPriceAuditLog_dba.sql with a DBA account, then
    //   GRANT SELECT, INSERT, UPDATE ON dbo.ProductPriceAuditLog TO <runtime-login>;
    // The startup wraps this call in try/catch and logs a warning — startup continues regardless.
    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);

        const string checkSql = """
            SELECT COUNT(1) FROM sys.tables
            WHERE  name = 'ProductPriceAuditLog' AND schema_id = SCHEMA_ID('dbo');
            """;
        await using var checkCmd = new SqlCommand(checkSql, conn);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        const string ddl = """
            CREATE TABLE dbo.ProductPriceAuditLog (
                Id                   BIGINT           IDENTITY(1,1) PRIMARY KEY,
                ActionId             UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                BatchRequestId       UNIQUEIDENTIFIER NULL,
                ItemCode             NVARCHAR(50)     NOT NULL,
                PriceListNum         INT              NOT NULL,
                OldPrice             DECIMAL(18,2)    NULL,
                ExpectedCurrentPrice DECIMAL(18,2)    NULL,
                RequestedPrice       DECIMAL(18,2)    NOT NULL,
                ActualPriceAfter     DECIMAL(18,2)    NULL,
                Currency             NVARCHAR(10)     NULL,
                RequestedBy          NVARCHAR(200)    NOT NULL,
                Reason               NVARCHAR(500)    NULL,
                RequestedAtUtc       DATETIME2        NOT NULL,
                ExecutedAtUtc        DATETIME2        NULL,
                Result               NVARCHAR(40)     NOT NULL DEFAULT 'Pending',
                SapErrorCode         INT              NULL,
                SapErrorMessage      NVARCHAR(1000)   NULL,
                SqliteSyncResult     NVARCHAR(20)     NULL,
                NeonSyncResult       NVARCHAR(20)     NULL,
                EvidenceJson         NVARCHAR(MAX)    NULL
            );
            CREATE INDEX IX_ProductPriceAuditLog_ItemCode
                ON dbo.ProductPriceAuditLog (ItemCode, RequestedAtUtc DESC);
            CREATE INDEX IX_ProductPriceAuditLog_BatchRequestId
                ON dbo.ProductPriceAuditLog (BatchRequestId)
                WHERE BatchRequestId IS NOT NULL;
            CREATE INDEX IX_ProductPriceAuditLog_ActionId
                ON dbo.ProductPriceAuditLog (ActionId);
            """;
        await using var ddlCmd = new SqlCommand(ddl, conn);
        try
        {
            await ddlCmd.ExecuteNonQueryAsync(ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 262)
        {
            throw new InvalidOperationException(
                "CREATE TABLE permission denied for dbo.ProductPriceAuditLog. " +
                "Run Scripts/ProductPriceAuditLog_dba.sql with a DBA account and grant runtime access: " +
                "GRANT SELECT, INSERT, UPDATE ON dbo.ProductPriceAuditLog TO <runtime-login>;", ex);
        }
    }

    // ── Phase 1: insert Pending ───────────────────────────────────────────────

    public virtual async Task<long> InsertAsync(ProductPriceAuditEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.ProductPriceAuditLog
                (ActionId, BatchRequestId, ItemCode, PriceListNum,
                 OldPrice, ExpectedCurrentPrice, RequestedPrice,
                 Currency, RequestedBy, Reason, RequestedAtUtc, Result, EvidenceJson)
            VALUES
                (@actionId, @batchId, @itemCode, @priceListNum,
                 @oldPrice, @expectedPrice, @requestedPrice,
                 @currency, @requestedBy, @reason, @requestedAt, @result, @evidence);
            SELECT SCOPE_IDENTITY();
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@actionId",      entry.ActionId);
        cmd.Parameters.AddWithValue("@batchId",       (object?)entry.BatchRequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@itemCode",      entry.ItemCode);
        cmd.Parameters.AddWithValue("@priceListNum",  entry.PriceListNum);
        cmd.Parameters.AddWithValue("@oldPrice",      (object?)entry.OldPrice        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expectedPrice", (object?)entry.ExpectedCurrentPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestedPrice",entry.RequestedPrice);
        cmd.Parameters.AddWithValue("@currency",      (object?)entry.Currency        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestedBy",   entry.RequestedBy);
        cmd.Parameters.AddWithValue("@reason",        (object?)entry.Reason          ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestedAt",   entry.RequestedAtUtc);
        cmd.Parameters.AddWithValue("@result",        entry.Result);
        cmd.Parameters.AddWithValue("@evidence",      (object?)entry.EvidenceJson    ?? DBNull.Value);

        var scalar = await cmd.ExecuteScalarAsync(ct);
        var id = Convert.ToInt64(scalar);
        entry.Id = id;
        return id;
    }

    // ── Phase 2: terminal result ──────────────────────────────────────────────

    public virtual async Task SetTerminalAsync(
        long id,
        string result,
        decimal? actualPriceAfter,
        int? sapErrorCode,
        string? sapErrorMessage,
        string? sqliteSyncResult,
        string? neonSyncResult,
        DateTime executedAtUtc,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.ProductPriceAuditLog
            SET    Result          = @result,
                   ActualPriceAfter= @actualPrice,
                   SapErrorCode   = @sapCode,
                   SapErrorMessage= @sapMsg,
                   SqliteSyncResult=@sqliteResult,
                   NeonSyncResult = @neonResult,
                   ExecutedAtUtc  = @executedAt
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",           id);
        cmd.Parameters.AddWithValue("@result",       result);
        cmd.Parameters.AddWithValue("@actualPrice",  (object?)actualPriceAfter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sapCode",      (object?)sapErrorCode     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sapMsg",       (object?)sapErrorMessage  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sqliteResult", (object?)sqliteSyncResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@neonResult",   (object?)neonSyncResult   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@executedAt",   executedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Idempotency check ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first already-completed audit record for (batchRequestId, itemCode, priceListNum),
    /// or null if none found (i.e., safe to proceed with this item).
    /// </summary>
    public virtual async Task<ProductPriceAuditEntry?> FindCompletedAsync(
        Guid batchRequestId, string itemCode, int priceListNum, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1
                Id, ActionId, BatchRequestId, ItemCode, PriceListNum,
                OldPrice, ExpectedCurrentPrice, RequestedPrice, ActualPriceAfter,
                Currency, RequestedBy, Reason, RequestedAtUtc, ExecutedAtUtc,
                Result, SapErrorCode, SapErrorMessage, SqliteSyncResult, NeonSyncResult
            FROM dbo.ProductPriceAuditLog
            WHERE  BatchRequestId = @batchId
              AND  ItemCode       = @itemCode
              AND  PriceListNum   = @plNum
              AND  Result         NOT IN ('Pending')
            ORDER BY RequestedAtUtc DESC;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@batchId",  batchRequestId);
        cmd.Parameters.AddWithValue("@itemCode", itemCode);
        cmd.Parameters.AddWithValue("@plNum",    priceListNum);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return new ProductPriceAuditEntry
        {
            Id                   = rdr.GetInt64(0),
            ActionId             = rdr.GetGuid(1),
            BatchRequestId       = rdr.IsDBNull(2)  ? null : rdr.GetGuid(2),
            ItemCode             = rdr.GetString(3),
            PriceListNum         = rdr.GetInt32(4),
            OldPrice             = rdr.IsDBNull(5)  ? null : rdr.GetDecimal(5),
            ExpectedCurrentPrice = rdr.IsDBNull(6)  ? null : rdr.GetDecimal(6),
            RequestedPrice       = rdr.GetDecimal(7),
            ActualPriceAfter     = rdr.IsDBNull(8)  ? null : rdr.GetDecimal(8),
            Currency             = rdr.IsDBNull(9)  ? null : rdr.GetString(9),
            RequestedBy          = rdr.GetString(10),
            Reason               = rdr.IsDBNull(11) ? null : rdr.GetString(11),
            RequestedAtUtc       = rdr.GetDateTime(12),
            ExecutedAtUtc        = rdr.IsDBNull(13) ? null : rdr.GetDateTime(13),
            Result               = rdr.GetString(14),
            SapErrorCode         = rdr.IsDBNull(15) ? null : rdr.GetInt32(15),
            SapErrorMessage      = rdr.IsDBNull(16) ? null : rdr.GetString(16),
            SqliteSyncResult     = rdr.IsDBNull(17) ? null : rdr.GetString(17),
            NeonSyncResult       = rdr.IsDBNull(18) ? null : rdr.GetString(18),
        };
    }
}
