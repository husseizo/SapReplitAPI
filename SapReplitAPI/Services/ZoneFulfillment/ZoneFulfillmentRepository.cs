using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// All MolasIntegration CRUD for zone-fulfillment tables.
/// Uses raw ADO.NET (SqlConnection per call, no EF Core).
/// Registered Scoped.
/// </summary>
public sealed class ZoneFulfillmentRepository
{
    private readonly string _cs;
    private readonly ILogger<ZoneFulfillmentRepository> _log;

    public ZoneFulfillmentRepository(IConfiguration cfg, ILogger<ZoneFulfillmentRepository> log)
    {
        _cs  = cfg.GetConnectionString("MolasIntegration")
               ?? throw new InvalidOperationException("Connection string 'MolasIntegration' is missing.");
        _log = log;
    }

    // ── Zone config ────────────────────────────────────────────────────────────

    public async Task<List<ZoneWarehouse>> GetZoneWarehousesAsync(
        string zoneName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT WhsCode, Priority
            FROM   dbo.ZoneWarehousePriority
            WHERE  ZoneName = @zone AND IsActive = 1
            ORDER  BY Priority ASC;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@zone", zoneName);

        var result = new List<ZoneWarehouse>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            result.Add(new ZoneWarehouse(rdr.GetString(0), rdr.GetInt32(1)));
        return result;
    }

    // ── Idempotency lookups ────────────────────────────────────────────────────

    /// <summary>Returns null if RequestId is not known.</summary>
    public async Task<FulfillmentOrchestrationRecord?> FindOrchestrationAsync(
        Guid requestId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT fo.Id, fo.RequestId, fo.State, fo.U_ReplitId, fo.SoDocEntry, fo.SoDocNum,
                   fo.DeliveryLocation, fo.AllocationVersion, fo.FailureKind, fo.ErrorMessage,
                   fo.CreatedAtUtc, fo.UpdatedAtUtc
            FROM   dbo.FulfillmentOrchestration fo
            WHERE  fo.RequestId = @rid;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", requestId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return ReadOrchestration(rdr);
    }

    /// <summary>Returns the stored hash for RequestId, or null if not found.</summary>
    public async Task<string?> GetPayloadHashAsync(Guid requestId, CancellationToken ct = default)
    {
        const string sql = "SELECT PayloadHash FROM dbo.FulfillmentRequest WHERE RequestId = @rid;";
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", requestId);
        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is string s ? s : null;
    }

    // ── Durable request persistence ────────────────────────────────────────────

    /// <summary>
    /// Inserts FulfillmentRequest + FulfillmentRequestLine rows + FulfillmentOrchestration.
    /// All three in one transaction. Returns the new orchestration record.
    /// </summary>
    public async Task<FulfillmentOrchestrationRecord> InsertRequestAsync(
        CreateZoneFulfillmentOrderRequest req,
        string payloadHash,
        CancellationToken ct = default)
    {
        const string sqlReq = """
            INSERT INTO dbo.FulfillmentRequest
                (RequestId, PayloadHash, CardCode, DocDate, DeliveryDate, DeliveryLocation, SlpCode, CreatedAtUtc)
            VALUES
                (@reqId, @hash, @card, @doc, @del, @loc, @slp, SYSUTCDATETIME());
            """;

        const string sqlLine = """
            INSERT INTO dbo.FulfillmentRequestLine
                (RequestId, RequestLineId, LineSeq, ItemCode, RequestedQty, UnitPrice, Description, U_ItemName, U_Manufacturer)
            VALUES
                (@reqId, @lineId, @seq, @item, @qty, @price, @desc, @uItem, @mfg);
            """;

        const string sqlOrch = """
            INSERT INTO dbo.FulfillmentOrchestration
                (RequestId, State, DeliveryLocation, AllocationVersion, CreatedAtUtc, UpdatedAtUtc)
            OUTPUT INSERTED.Id, INSERTED.CreatedAtUtc
            VALUES
                (@reqId, N'Received', @loc, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            // Header
            await using (var cmd = new SqlCommand(sqlReq, conn, tx))
            {
                cmd.Parameters.AddWithValue("@reqId", req.RequestId);
                cmd.Parameters.AddWithValue("@hash",  payloadHash);
                cmd.Parameters.AddWithValue("@card",  req.CardCode);
                cmd.Parameters.AddWithValue("@doc",   req.DocDate.ToDateTime(TimeOnly.MinValue));
                cmd.Parameters.AddWithValue("@del",   req.DeliveryDate.ToDateTime(TimeOnly.MinValue));
                cmd.Parameters.AddWithValue("@loc",   req.DeliveryLocation);
                cmd.Parameters.AddWithValue("@slp",   (object?)req.SlpCode ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Lines
            foreach (var l in req.Lines)
            {
                await using var cmd = new SqlCommand(sqlLine, conn, tx);
                cmd.Parameters.AddWithValue("@reqId",  req.RequestId);
                cmd.Parameters.AddWithValue("@lineId", l.RequestLineId);
                cmd.Parameters.AddWithValue("@seq",    l.LineSeq);
                cmd.Parameters.AddWithValue("@item",   l.ItemCode);
                cmd.Parameters.AddWithValue("@qty",    l.RequestedQty);
                cmd.Parameters.AddWithValue("@price",  l.UnitPrice);
                cmd.Parameters.AddWithValue("@desc",   (object?)l.Description   ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@uItem",  (object?)l.U_ItemName    ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@mfg",   (object?)l.U_Manufacturer ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Orchestration
            long     orchId;
            DateTime created;
            await using (var cmd = new SqlCommand(sqlOrch, conn, tx))
            {
                cmd.Parameters.AddWithValue("@reqId", req.RequestId);
                cmd.Parameters.AddWithValue("@loc",   req.DeliveryLocation);
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                await rdr.ReadAsync(ct);
                orchId  = rdr.GetInt64(0);
                created = rdr.GetDateTime(1);
            }

            tx.Commit();

            return new FulfillmentOrchestrationRecord
            {
                Id               = orchId,
                RequestId        = req.RequestId,
                State            = OrchestrationState.Received,
                DeliveryLocation = req.DeliveryLocation,
                AllocationVersion = 1,
                CreatedAtUtc     = created,
                UpdatedAtUtc     = created
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── State transitions ──────────────────────────────────────────────────────

    public async Task UpdateStateAsync(
        long orchestrationId,
        string newState,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.FulfillmentOrchestration
            SET    State = @state, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id    = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@state", newState);
        cmd.Parameters.AddWithValue("@id",    orchestrationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetFailedAsync(
        long orchestrationId,
        string failureKind,
        string errorMessage,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.FulfillmentOrchestration
            SET    State        = N'Failed',
                   FailureKind  = @kind,
                   ErrorMessage = @msg,
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@kind", failureKind);
        cmd.Parameters.AddWithValue("@msg",  errorMessage);
        cmd.Parameters.AddWithValue("@id",   orchestrationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetUnknownOutcomeAsync(
        long orchestrationId, string uReplitId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.FulfillmentOrchestration
            SET    State       = N'UnknownOutcome',
                   U_ReplitId  = @rid,
                   FailureKind = N'SapUnknownOutcome',
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", uReplitId);
        cmd.Parameters.AddWithValue("@id",  orchestrationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSalesOrderCreatedAsync(
        long   orchestrationId,
        string uReplitId,
        int    docEntry,
        int?   docNum,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.FulfillmentOrchestration
            SET    State        = N'SalesOrderCreated',
                   U_ReplitId  = @rid,
                   SoDocEntry  = @entry,
                   SoDocNum    = @num,
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid",   uReplitId);
        cmd.Parameters.AddWithValue("@entry", docEntry);
        cmd.Parameters.AddWithValue("@num",   (object?)docNum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id",    orchestrationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Allocation plan + fragments ────────────────────────────────────────────

    /// <summary>
    /// Persists AllocationPlan + AllocationFragment rows in one transaction.
    /// Returns the new AllocationPlan.Id.
    /// </summary>
    public async Task<long> InsertAllocationPlanAsync(
        long orchestrationId,
        int version,
        string reason,
        IReadOnlyList<AllocationFragment> fragments,
        CancellationToken ct = default)
    {
        const string sqlPlan = """
            INSERT INTO dbo.AllocationPlan (OrchestrationId, Version, Reason, CreatedAtUtc)
            OUTPUT INSERTED.Id
            VALUES (@oid, @ver, @reason, SYSUTCDATETIME());
            """;

        const string sqlFrag = """
            INSERT INTO dbo.AllocationFragment
                (PlanId, RequestLineId, WhsCode, AllocatedQty, UnallocatedQty)
            VALUES (@planId, @lineId, @whs, @alloc, @unalloc);
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx   = conn.BeginTransaction();
        try
        {
            long planId;
            await using (var cmd = new SqlCommand(sqlPlan, conn, tx))
            {
                cmd.Parameters.AddWithValue("@oid",    orchestrationId);
                cmd.Parameters.AddWithValue("@ver",    version);
                cmd.Parameters.AddWithValue("@reason", reason);
                planId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            foreach (var f in fragments)
            {
                await using var cmd = new SqlCommand(sqlFrag, conn, tx);
                cmd.Parameters.AddWithValue("@planId",   planId);
                cmd.Parameters.AddWithValue("@lineId",   f.RequestLineId);
                cmd.Parameters.AddWithValue("@whs",      f.WhsCode);
                cmd.Parameters.AddWithValue("@alloc",    f.AllocatedQty);
                cmd.Parameters.AddWithValue("@unalloc",  f.UnallocatedQty);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return planId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── SoLineFragment ─────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts SoLineFragment rows (one per RDR1 line) + transitions orchestration
    /// to SalesOrderCreated/Accepted in one transaction.
    /// </summary>
    public async Task InsertSoLineFragmentsAsync(
        long orchestrationId,
        long allocationPlanId,
        IReadOnlyList<SoLineFragmentRecord> fragments,
        CancellationToken ct = default)
    {
        const string sqlFrag = """
            INSERT INTO dbo.SoLineFragment
                (OrchestrationId, RequestLineId, AllocationPlanId,
                 SoDocEntry, SoLineNum, ItemCode, WhsCode,
                 SoLineQty, AllocatedQty, UnallocatedQty,
                 ReleasedQty, DeliveredQty, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@oid, @lineId, @planId,
                 @entry, @lineNum, @item, @whs,
                 @soQty, @alloc, @unalloc,
                 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx   = conn.BeginTransaction();
        try
        {
            foreach (var f in fragments)
            {
                await using var cmd = new SqlCommand(sqlFrag, conn, tx);
                cmd.Parameters.AddWithValue("@oid",     orchestrationId);
                cmd.Parameters.AddWithValue("@lineId",  f.RequestLineId);
                cmd.Parameters.AddWithValue("@planId",  allocationPlanId);
                cmd.Parameters.AddWithValue("@entry",   f.SoDocEntry);
                cmd.Parameters.AddWithValue("@lineNum", f.SoLineNum);
                cmd.Parameters.AddWithValue("@item",    f.ItemCode);
                cmd.Parameters.AddWithValue("@whs",     f.WhsCode);
                cmd.Parameters.AddWithValue("@soQty",   f.SoLineQty);
                cmd.Parameters.AddWithValue("@alloc",   f.AllocatedQty);
                cmd.Parameters.AddWithValue("@unalloc", f.UnallocatedQty);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── Status query ───────────────────────────────────────────────────────────

    public async Task<List<SoLineFragmentRecord>> GetSoLineFragmentsAsync(
        long orchestrationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, OrchestrationId, RequestLineId, AllocationPlanId,
                   SoDocEntry, SoLineNum, ItemCode, WhsCode,
                   SoLineQty, AllocatedQty, UnallocatedQty, ReleasedQty, DeliveredQty
            FROM   dbo.SoLineFragment
            WHERE  OrchestrationId = @oid
            ORDER  BY SoLineNum;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@oid", orchestrationId);

        var list = new List<SoLineFragmentRecord>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new SoLineFragmentRecord
            {
                Id               = rdr.GetInt64(0),
                OrchestrationId  = rdr.GetInt64(1),
                RequestLineId    = rdr.GetGuid(2),
                AllocationPlanId = rdr.GetInt64(3),
                SoDocEntry       = rdr.GetInt32(4),
                SoLineNum        = rdr.GetInt32(5),
                ItemCode         = rdr.GetString(6),
                WhsCode          = rdr.GetString(7),
                SoLineQty        = rdr.GetDecimal(8),
                AllocatedQty     = rdr.GetDecimal(9),
                UnallocatedQty   = rdr.GetDecimal(10),
                ReleasedQty      = rdr.GetDecimal(11),
                DeliveredQty     = rdr.GetDecimal(12)
            });
        }
        return list;
    }

    public async Task<List<FulfillmentRequestLine>> GetRequestLinesAsync(
        Guid requestId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT RequestLineId, LineSeq, ItemCode, RequestedQty, UnitPrice,
                   Description, U_ItemName, U_Manufacturer
            FROM   dbo.FulfillmentRequestLine
            WHERE  RequestId = @rid
            ORDER  BY LineSeq;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", requestId);

        var list = new List<FulfillmentRequestLine>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new FulfillmentRequestLine
            {
                RequestLineId  = rdr.GetGuid(0),
                LineSeq        = rdr.GetInt32(1),
                ItemCode       = rdr.GetString(2),
                RequestedQty   = rdr.GetDecimal(3),
                UnitPrice      = rdr.GetDecimal(4),
                Description    = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                U_ItemName     = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                U_Manufacturer = rdr.IsDBNull(7) ? null : rdr.GetString(7)
            });
        }
        return list;
    }

    // ── UnknownOutcome recovery ────────────────────────────────────────────────

    /// <summary>
    /// Checks if U_ReplitId already exists in SAP by querying MOLAS_Live_2021.ORDR.
    /// Returns (DocEntry, DocNum) if found, null otherwise.
    /// Used for UnknownOutcome recovery only.
    /// </summary>
    public async Task<(int DocEntry, int DocNum)?> FindSapOrderByReplitIdAsync(
        string uReplitId, CancellationToken ct = default)
    {
        // NOTE: This queries MOLAS_Live_2021 via the same SQL Server instance (localhost).
        // MolasIntegration and MOLAS_Live_2021 are on the same SQL Server.
        const string sql = """
            SELECT T0.DocEntry, T0.DocNum
            FROM   MOLAS_Live_2021.dbo.ORDR T0
            WHERE  T0.U_ReplitId = @rid
              AND  T0.CANCELED   = N'N';
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", uReplitId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return (rdr.GetInt32(0), rdr.GetInt32(1));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static FulfillmentOrchestrationRecord ReadOrchestration(SqlDataReader rdr) =>
        new()
        {
            Id               = rdr.GetInt64(0),
            RequestId        = rdr.GetGuid(1),
            State            = rdr.GetString(2),
            U_ReplitId       = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            SoDocEntry       = rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
            SoDocNum         = rdr.IsDBNull(5) ? null : rdr.GetInt32(5),
            DeliveryLocation = rdr.GetString(6),
            AllocationVersion = rdr.GetInt32(7),
            FailureKind      = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            ErrorMessage     = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            CreatedAtUtc     = rdr.GetDateTime(10),
            UpdatedAtUtc     = rdr.GetDateTime(11)
        };
}

/// <summary>Local read model for FulfillmentRequestLine rows.</summary>
public sealed class FulfillmentRequestLine
{
    public Guid    RequestLineId  { get; set; }
    public int     LineSeq        { get; set; }
    public string  ItemCode       { get; set; } = "";
    public decimal RequestedQty   { get; set; }
    public decimal UnitPrice      { get; set; }
    public string? Description    { get; set; }
    public string? U_ItemName     { get; set; }
    public string? U_Manufacturer { get; set; }
}
