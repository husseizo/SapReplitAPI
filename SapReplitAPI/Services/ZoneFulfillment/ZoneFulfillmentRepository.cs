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

    /// <summary>
    /// Persists auto-pick-list creation error without changing State (stays Accepted).
    /// The admin /pick-lists endpoint can still recover because State=Accepted is required.
    /// </summary>
    public async Task SetPickListCreationWarningAsync(
        long orchestrationId,
        string errorMessage,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.FulfillmentOrchestration
            SET    FailureKind  = N'PickListAutoCreationFailed',
                   ErrorMessage = @msg,
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@msg", errorMessage);
        cmd.Parameters.AddWithValue("@id",  orchestrationId);
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

    // ── Pick List traceability ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the most recent PickListRecord for the given SoLineFragment + WhsCode, or null.
    /// Multiple rows may exist after repick (same fragment, different AbsEntry). Returns latest by Id.
    /// </summary>
    public async Task<PickListRecordModel?> FindPickListRecordAsync(
        long soLineFragmentId, string whsCode, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1 Id, OrchestrationId, SoLineFragmentId, SoDocEntry, SoLineNum,
                   WhsCode, PickListAbsEntry, ReleasedQty, PickedQty, Status, CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.PickListRecord
            WHERE  SoLineFragmentId = @fragId AND WhsCode = @whs
            ORDER BY Id DESC;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fragId", soLineFragmentId);
        cmd.Parameters.AddWithValue("@whs",    whsCode);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return new PickListRecordModel
        {
            Id               = rdr.GetInt64(0),
            OrchestrationId  = rdr.GetInt64(1),
            SoLineFragmentId = rdr.GetInt64(2),
            SoDocEntry       = rdr.GetInt32(3),
            SoLineNum        = rdr.GetInt32(4),
            WhsCode          = rdr.GetString(5),
            PickListAbsEntry = rdr.GetInt32(6),
            ReleasedQty      = rdr.GetDecimal(7),
            PickedQty        = rdr.GetDecimal(8),
            Status           = rdr.GetString(9),
            CreatedAtUtc     = rdr.GetDateTime(10),
            UpdatedAtUtc     = rdr.GetDateTime(11)
        };
    }

    /// <summary>
    /// Inserts a new PickListRecord and updates SoLineFragment.ReleasedQty in one transaction.
    /// Sets record.Id from OUTPUT INSERTED.Id after insert.
    /// Throws SqlException 2627/2601 on duplicate (OrchestrationId, SoLineFragmentId, PickListAbsEntry).
    /// </summary>
    public async Task InsertPickListRecordAsync(
        PickListRecordModel record, CancellationToken ct = default)
    {
        const string sqlInsert = """
            INSERT INTO dbo.PickListRecord
                (OrchestrationId, SoLineFragmentId, SoDocEntry, SoLineNum,
                 WhsCode, PickListAbsEntry, ReleasedQty, Status, CreatedAtUtc, UpdatedAtUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@orchId, @fragId, @entry, @lineNum,
                 @whs, @absEntry, @relQty, @status, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        const string sqlUpdateFrag = """
            UPDATE dbo.SoLineFragment
            SET    ReleasedQty  = @relQty,
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @fragId;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            await using (var cmd = new SqlCommand(sqlInsert, conn, tx))
            {
                cmd.Parameters.AddWithValue("@orchId",   record.OrchestrationId);
                cmd.Parameters.AddWithValue("@fragId",   record.SoLineFragmentId);
                cmd.Parameters.AddWithValue("@entry",    record.SoDocEntry);
                cmd.Parameters.AddWithValue("@lineNum",  record.SoLineNum);
                cmd.Parameters.AddWithValue("@whs",      record.WhsCode);
                cmd.Parameters.AddWithValue("@absEntry", record.PickListAbsEntry);
                cmd.Parameters.AddWithValue("@relQty",   record.ReleasedQty);
                cmd.Parameters.AddWithValue("@status",   record.Status);
                var inserted = await cmd.ExecuteScalarAsync(ct);
                record.Id = Convert.ToInt64(inserted);
            }

            await using (var cmd = new SqlCommand(sqlUpdateFrag, conn, tx))
            {
                cmd.Parameters.AddWithValue("@relQty", record.ReleasedQty);
                cmd.Parameters.AddWithValue("@fragId", record.SoLineFragmentId);
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

    /// <summary>
    /// Inserts one PickListFragmentRecord row after a successful multi-line OPKL creation.
    /// UNIQUE(PickListRecordId, SoLineFragmentId) prevents duplicate rows for the same OPKL line.
    /// SapPickEntry is null at creation; updated during pick execution via UpdatePickListFragmentPickEntryAsync.
    /// </summary>
    public async Task InsertPickListFragmentRecordAsync(
        long   pickListRecordId,
        long   soLineFragmentId,
        int    soDocEntry,
        int    soLineNum,
        string itemCode,
        string whsCode,
        decimal releasedQty,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.PickListFragmentRecord
                (PickListRecordId, SoLineFragmentId, SoDocEntry, SoLineNum, ItemCode, WhsCode,
                 ReleasedQty, PickedQty, SapPickEntry, PickStatus, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@plrId, @fragId, @docEntry, @lineNum, @itemCode, @whsCode,
                 @relQty, 0, NULL, N'Created', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@plrId",    pickListRecordId);
        cmd.Parameters.AddWithValue("@fragId",   soLineFragmentId);
        cmd.Parameters.AddWithValue("@docEntry", soDocEntry);
        cmd.Parameters.AddWithValue("@lineNum",  soLineNum);
        cmd.Parameters.AddWithValue("@itemCode", itemCode);
        cmd.Parameters.AddWithValue("@whsCode",  whsCode);
        cmd.Parameters.AddWithValue("@relQty",   releasedQty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the most recent PickListRecord per SoLineFragmentId for an orchestration.
    /// After repick a fragment has two rows (historical + new); only the latest is returned so
    /// callers building a SoLineFragmentId→PLR dictionary never encounter duplicate keys.
    /// </summary>
    public async Task<List<PickListRecordModel>> GetPickListRecordsAsync(
        long orchestrationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT plr.Id, plr.OrchestrationId, plr.SoLineFragmentId, plr.SoDocEntry, plr.SoLineNum,
                   plr.WhsCode, plr.PickListAbsEntry, plr.ReleasedQty, plr.PickedQty, plr.Status,
                   plr.CreatedAtUtc, plr.UpdatedAtUtc
            FROM   dbo.PickListRecord plr
            WHERE  plr.OrchestrationId = @orchId
              AND  plr.Id = (
                SELECT MAX(plr2.Id)
                FROM   dbo.PickListRecord plr2
                WHERE  plr2.OrchestrationId = plr.OrchestrationId
                  AND  plr2.SoLineFragmentId = plr.SoLineFragmentId
              );
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", orchestrationId);
        await using var rdr  = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PickListRecordModel>();
        while (await rdr.ReadAsync(ct))
            result.Add(new PickListRecordModel
            {
                Id               = rdr.GetInt64(0),
                OrchestrationId  = rdr.GetInt64(1),
                SoLineFragmentId = rdr.GetInt64(2),
                SoDocEntry       = rdr.GetInt32(3),
                SoLineNum        = rdr.GetInt32(4),
                WhsCode          = rdr.GetString(5),
                PickListAbsEntry = rdr.GetInt32(6),
                ReleasedQty      = rdr.GetDecimal(7),
                PickedQty        = rdr.GetDecimal(8),
                Status           = rdr.GetString(9),
                CreatedAtUtc     = rdr.GetDateTime(10),
                UpdatedAtUtc     = rdr.GetDateTime(11)
            });
        return result;
    }

    /// <summary>
    /// Returns the PickListRecord for a specific PickListAbsEntry belonging to the given orchestration.
    /// Used by ExecutePickAsync to resolve which row to update.
    /// </summary>
    public async Task<PickListRecordModel?> FindPickListRecordByAbsEntryAsync(
        long orchestrationId, int pickListAbsEntry, CancellationToken ct = default)
    {
        // Return the first unpicked line first (ascending Id), then any remaining if all picked.
        // OPKL with multiple lines (one PLR per SO line) requires sequential per-line picking.
        const string sql = """
            SELECT TOP 1 Id, OrchestrationId, SoLineFragmentId, SoDocEntry, SoLineNum,
                   WhsCode, PickListAbsEntry, ReleasedQty, PickedQty, Status, CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.PickListRecord
            WHERE  OrchestrationId  = @orchId
              AND  PickListAbsEntry = @absEntry
            ORDER BY CASE WHEN Status <> 'Picked' THEN 0 ELSE 1 END, Id;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId",   orchestrationId);
        cmd.Parameters.AddWithValue("@absEntry", pickListAbsEntry);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return new PickListRecordModel
        {
            Id               = rdr.GetInt64(0),
            OrchestrationId  = rdr.GetInt64(1),
            SoLineFragmentId = rdr.GetInt64(2),
            SoDocEntry       = rdr.GetInt32(3),
            SoLineNum        = rdr.GetInt32(4),
            WhsCode          = rdr.GetString(5),
            PickListAbsEntry = rdr.GetInt32(6),
            ReleasedQty      = rdr.GetDecimal(7),
            PickedQty        = rdr.GetDecimal(8),
            Status           = rdr.GetString(9),
            CreatedAtUtc     = rdr.GetDateTime(10),
            UpdatedAtUtc     = rdr.GetDateTime(11)
        };
    }

    /// <summary>
    /// Updates PickedQty, Status, and UpdatedAtUtc on the target PickListRecord.
    /// Uses targeted UPDATE — only touches the intended row.
    /// </summary>
    public async Task UpdatePickListPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string status, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.PickListRecord
            SET    PickedQty    = @pickedQty,
                   Status       = @status,
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",       pickListRecordId);
        cmd.Parameters.AddWithValue("@pickedQty", pickedQty);
        cmd.Parameters.AddWithValue("@status",    status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Updates PickedQty, PickStatus, and UpdatedAtUtc on the PickListFragmentRecord row
    /// that belongs to the given PickListRecordId. Returns rows affected (0 = no PLFR for this PLR;
    /// pre-PLFR history is safe — callers must not treat 0 as an error).
    /// </summary>
    public async Task<int> UpdatePickListFragmentPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string pickStatus, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.PickListFragmentRecord
            SET    PickedQty    = @pickedQty,
                   PickStatus   = @pickStatus,
                   UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  PickListRecordId = @plrId;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@plrId",     pickListRecordId);
        cmd.Parameters.AddWithValue("@pickedQty", pickedQty);
        cmd.Parameters.AddWithValue("@pickStatus", pickStatus);
        return await cmd.ExecuteNonQueryAsync(ct);
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

    // ── DeliveryRecord CRUD ────────────────────────────────────────────────────

    public async Task<DeliveryRecordModel?> FindDeliveryRecordAsync(
        long orchestrationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, OrchestrationId, SapDocEntry, SapDocNum, ZoneRef,
                   DeliveryLocation, CardCode, Status, SapErrorMessage,
                   CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.DeliveryRecord
            WHERE  OrchestrationId = @orchId;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", orchestrationId);
        await using var rdr  = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return ReadDeliveryRecord(rdr);
    }

    /// <summary>Returns ALL DeliveryRecords for an orchestration, ordered by Id ascending (newest last).</summary>
    public async Task<List<DeliveryRecordModel>> GetDeliveryRecordsAsync(
        long orchestrationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, OrchestrationId, SapDocEntry, SapDocNum, ZoneRef,
                   DeliveryLocation, CardCode, Status, SapErrorMessage,
                   CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.DeliveryRecord
            WHERE  OrchestrationId = @orchId
            ORDER  BY Id ASC;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", orchestrationId);
        await using var rdr  = await cmd.ExecuteReaderAsync(ct);
        var result = new List<DeliveryRecordModel>();
        while (await rdr.ReadAsync(ct))
            result.Add(ReadDeliveryRecord(rdr));
        return result;
    }

    /// <summary>Returns a DeliveryRecord by SapDocEntry. Returns null if not found.</summary>
    public async Task<DeliveryRecordModel?> FindDeliveryRecordBySapDocEntryAsync(
        int sapDocEntry, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, OrchestrationId, SapDocEntry, SapDocNum, ZoneRef,
                   DeliveryLocation, CardCode, Status, SapErrorMessage,
                   CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.DeliveryRecord
            WHERE  SapDocEntry = @de;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@de", sapDocEntry);
        await using var rdr  = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return ReadDeliveryRecord(rdr);
    }

    /// <summary>Inserts a DeliveryRecord with Status=Pending. Returns the new Id.</summary>
    public async Task<long> InsertDeliveryRecordAsync(
        DeliveryRecordModel record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.DeliveryRecord
                (OrchestrationId, ZoneRef, DeliveryLocation, CardCode, Status)
            OUTPUT INSERTED.Id
            VALUES
                (@orchId, @zr, @dloc, @card, N'Pending');
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", record.OrchestrationId);
        cmd.Parameters.AddWithValue("@zr",     record.ZoneRef);
        cmd.Parameters.AddWithValue("@dloc", record.DeliveryLocation);
        cmd.Parameters.AddWithValue("@card", record.CardCode);
        var id = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(id);
    }

    public async Task UpdateDeliveryRecordAsync(
        long    id,
        string  status,
        int?    sapDocEntry,
        int?    sapDocNum,
        string? sapErrorMessage,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.DeliveryRecord
            SET    Status          = @status,
                   SapDocEntry     = @de,
                   SapDocNum       = @dn,
                   SapErrorMessage = @err,
                   UpdatedAtUtc    = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",     id);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@de",     (object?)sapDocEntry     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dn",     (object?)sapDocNum       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err",    (object?)sapErrorMessage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── InvoiceRecord CRUD ────────────────────────────────────────────────────

    /// <summary>Returns InvoiceRecord by ODLN DocEntry. Returns null if not found.</summary>
    public async Task<InvoiceRecordModel?> FindInvoiceRecordByDeliveryDocEntryAsync(
        int deliveryDocEntry, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, OrchestrationId, DeliveryRecordId, DeliveryDocEntry,
                   SapDocEntry, SapDocNum, Status, SapErrorMessage,
                   CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.InvoiceRecord
            WHERE  DeliveryDocEntry = @de;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@de", deliveryDocEntry);
        await using var rdr  = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return ReadInvoiceRecord(rdr);
    }

    /// <summary>Inserts an InvoiceRecord with Status=Pending. Returns the new Id.</summary>
    public async Task<long> InsertInvoiceRecordAsync(
        InvoiceRecordModel record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.InvoiceRecord
                (OrchestrationId, DeliveryRecordId, DeliveryDocEntry, Status)
            OUTPUT INSERTED.Id
            VALUES
                (@orchId, @drid, @de, N'Pending');
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", record.OrchestrationId);
        cmd.Parameters.AddWithValue("@drid",   record.DeliveryRecordId);
        cmd.Parameters.AddWithValue("@de",     record.DeliveryDocEntry);
        var id = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(id);
    }

    public async Task UpdateInvoiceRecordAsync(
        long    id,
        string  status,
        int?    sapDocEntry,
        int?    sapDocNum,
        string? sapErrorMessage,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.InvoiceRecord
            SET    Status          = @status,
                   SapDocEntry     = @de,
                   SapDocNum       = @dn,
                   SapErrorMessage = @err,
                   UpdatedAtUtc    = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",     id);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@de",     (object?)sapDocEntry     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dn",     (object?)sapDocNum       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err",    (object?)sapErrorMessage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static InvoiceRecordModel ReadInvoiceRecord(SqlDataReader rdr) => new()
    {
        Id               = rdr.GetInt64(0),
        OrchestrationId  = rdr.GetInt64(1),
        DeliveryRecordId = rdr.GetInt64(2),
        DeliveryDocEntry = rdr.GetInt32(3),
        SapDocEntry      = rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
        SapDocNum        = rdr.IsDBNull(5) ? null : rdr.GetInt32(5),
        Status           = rdr.GetString(6),
        SapErrorMessage  = rdr.IsDBNull(7) ? null : rdr.GetString(7),
        CreatedAtUtc     = rdr.GetDateTime(8),
        UpdatedAtUtc     = rdr.GetDateTime(9)
    };

    /// <summary>Inserts a DeliveryFragmentRecord and all its bin rows in a single transaction.</summary>
    public async Task<long> InsertDeliveryFragmentRecordAsync(
        DeliveryFragmentRecordModel fragment, CancellationToken ct = default)
    {
        const string sqlFrag = """
            INSERT INTO dbo.DeliveryFragmentRecord
                (DeliveryRecordId, FragmentId, SoDocEntry, SoLineNum,
                 ItemCode, WhsCode, PickedQty, DeliveredQty, PickListAbsEntry, DlnLineNum)
            OUTPUT INSERTED.Id
            VALUES
                (@drid, @fid, @sde, @sln, @ic, @whs, @qty, @dqty, @plae, @dlnln);
            """;
        const string sqlBin = """
            INSERT INTO dbo.DeliveryFragmentBinRecord
                (DeliveryFragmentRecordId, BinAbsEntry, BinCode, Quantity)
            VALUES (@dfrid, @bae, @bc, @qty);
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx   = conn.BeginTransaction();
        try
        {
            long fragId;
            await using (var cmd = new SqlCommand(sqlFrag, conn, tx))
            {
                cmd.Parameters.AddWithValue("@drid",  fragment.DeliveryRecordId);
                cmd.Parameters.AddWithValue("@fid",   fragment.FragmentId);
                cmd.Parameters.AddWithValue("@sde",   fragment.SoDocEntry);
                cmd.Parameters.AddWithValue("@sln",   fragment.SoLineNum);
                cmd.Parameters.AddWithValue("@ic",    fragment.ItemCode);
                cmd.Parameters.AddWithValue("@whs",   fragment.WhsCode);
                cmd.Parameters.AddWithValue("@qty",   fragment.PickedQty);
                cmd.Parameters.AddWithValue("@dqty",  fragment.DeliveredQty);
                cmd.Parameters.AddWithValue("@plae",  fragment.PickListAbsEntry);
                cmd.Parameters.AddWithValue("@dlnln", (object?)fragment.DlnLineNum ?? DBNull.Value);
                var id = await cmd.ExecuteScalarAsync(ct);
                fragId = Convert.ToInt64(id);
            }

            foreach (var bin in fragment.Bins)
            {
                await using var cmd = new SqlCommand(sqlBin, conn, tx);
                cmd.Parameters.AddWithValue("@dfrid", fragId);
                cmd.Parameters.AddWithValue("@bae",   bin.BinAbsEntry);
                cmd.Parameters.AddWithValue("@bc",    bin.BinCode);
                cmd.Parameters.AddWithValue("@qty",   bin.Quantity);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return fragId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task UpdateSoLineFragmentDeliveredQtyAsync(
        long    fragmentId,
        decimal deliveredQty,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.SoLineFragment
            SET    DeliveredQty = @qty, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",  fragmentId);
        cmd.Parameters.AddWithValue("@qty", deliveredQty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Picker assignment ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the single active+default PickerAssignment for the given warehouse.
    /// Throws PickerAssignmentNotFoundException if none found.
    /// Throws PickerAssignmentInvalidException if multiple active+default rows exist,
    /// if IsActive=false, or if SapUserId is null.
    /// </summary>
    public async Task<PickerAssignmentModel> GetPickerAssignmentAsync(
        string whsCode, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, WhsCode, UserId, UserName, SapUserId, IsDefault, IsActive
            FROM   dbo.PickerAssignment
            WHERE  WhsCode   = @whs
              AND  IsActive  = 1
              AND  IsDefault = 1;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@whs", whsCode);

        var rows = new List<PickerAssignmentModel>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PickerAssignmentModel
            {
                Id        = rdr.GetInt32(0),
                WhsCode   = rdr.GetString(1),
                UserId    = rdr.GetString(2),
                UserName  = rdr.GetString(3),
                SapUserId = rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
                IsDefault = rdr.GetBoolean(5),
                IsActive  = rdr.GetBoolean(6)
            });
        }

        if (rows.Count == 0)
            throw new PickerAssignmentNotFoundException(whsCode);

        if (rows.Count > 1)
            throw new PickerAssignmentInvalidException(whsCode,
                $"multiple active+default rows found ({rows.Count})");

        var assignment = rows[0];

        if (!assignment.IsActive)
            throw new PickerAssignmentInvalidException(whsCode, "IsActive=false");

        if (assignment.SapUserId is null)
            throw new PickerAssignmentInvalidException(whsCode, "SapUserId is null");

        return assignment;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DeliveryRecordModel ReadDeliveryRecord(SqlDataReader rdr) =>
        new()
        {
            Id               = rdr.GetInt64(0),
            OrchestrationId  = rdr.GetInt64(1),
            SapDocEntry      = rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
            SapDocNum        = rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
            ZoneRef          = rdr.GetString(4),
            DeliveryLocation = rdr.GetString(5),
            CardCode         = rdr.GetString(6),
            Status           = rdr.GetString(7),
            SapErrorMessage  = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            CreatedAtUtc     = rdr.GetDateTime(9),
            UpdatedAtUtc     = rdr.GetDateTime(10)
        };

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
