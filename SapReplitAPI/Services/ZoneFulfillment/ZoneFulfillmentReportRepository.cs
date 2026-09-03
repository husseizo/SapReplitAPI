using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.Neon;
using System.Data;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Neon DDL + CRUD for ZoneFulfillmentReports / ZoneFulfillmentReportLines.
/// Also provides MolasIntegration context lookups (by OINV DocEntry) needed during snapshot capture.
/// </summary>
public sealed class ZoneFulfillmentReportRepository
{
    private readonly string _molasCs;
    private readonly NeonDbContext _neon;
    private readonly ILogger<ZoneFulfillmentReportRepository> _log;

    public ZoneFulfillmentReportRepository(
        IConfiguration cfg,
        NeonDbContext neon,
        ILogger<ZoneFulfillmentReportRepository> log)
    {
        _molasCs = cfg.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("Connection string 'MolasIntegration' is missing.");
        _neon = neon;
        _log  = log;
    }

    // ── Neon DDL ───────────────────────────────────────────────────────────────

    public async Task EnsureTablesAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS "ZoneFulfillmentReports" (
                "Id"                BIGSERIAL PRIMARY KEY,
                "ReportId"          UUID        NOT NULL,
                "RequestId"         UUID        NOT NULL,
                "OrchestrationId"   BIGINT      NOT NULL,
                "ReportType"        TEXT        NOT NULL DEFAULT 'FinalFulfillment',
                "Status"            TEXT        NOT NULL DEFAULT 'Pending',
                "SalesOrderDocEntry" INT        NOT NULL,
                "SalesOrderDocNum"  INT         NOT NULL,
                "DeliveryDocEntry"  INT         NOT NULL,
                "DeliveryDocNum"    INT         NOT NULL,
                "InvoiceDocEntry"   INT,
                "InvoiceDocNum"     INT,
                "CardCode"          TEXT        NOT NULL DEFAULT '',
                "DeliveryLocation"  TEXT        NOT NULL DEFAULT '',
                "ZoneRef"           TEXT        NOT NULL DEFAULT 'ZoneFulfillment',
                "U_ReplitId"        TEXT        NOT NULL DEFAULT '',
                "FileName"          TEXT,
                "MimeType"          TEXT,
                "FileSize"          BIGINT,
                "StorageProvider"   TEXT,
                "StorageKey"        TEXT,
                "Sha256"            TEXT,
                "SnapshotJson"      JSONB       NOT NULL DEFAULT '{}',
                "GeneratedAtUtc"    TIMESTAMPTZ,
                "UpdatedAtUtc"      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                "ErrorMessage"      TEXT,
                UNIQUE ("RequestId", "ReportType", "DeliveryDocEntry")
            );

            CREATE TABLE IF NOT EXISTS "ZoneFulfillmentReportLines" (
                "Id"                BIGSERIAL PRIMARY KEY,
                "ReportId"          UUID        NOT NULL,
                "LineSeq"           INT         NOT NULL,
                "ItemCode"          TEXT        NOT NULL DEFAULT '',
                "Description"       TEXT,
                "RequestedQty"      NUMERIC(18,6),
                "PickedQty"         NUMERIC(18,6),
                "DeliveredQty"      NUMERIC(18,6),
                "UnitPrice"         NUMERIC(18,2),
                "LineTotal"         NUMERIC(18,2),
                "WhsCode"           TEXT,
                "OpklAbsEntry"      INT,
                "PickerUserId"      INT,
                "PickerUserCode"    TEXT,
                "PickerName"        TEXT,
                "BinAbsEntry"       INT,
                "BinCode"           TEXT,
                "BinQty"            NUMERIC(18,6),
                "SalesOrderBaseLine" INT,
                "DeliveryLineNum"   INT,
                "InvoiceLineNum"    INT,
                "InvoiceBaseType"   INT,
                "InvoiceBaseEntry"  INT,
                "InvoiceBaseLine"   INT,
                UNIQUE ("ReportId", "LineSeq")
            );
            """;

        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogDebug("[ZF-REPORT] Neon tables ensured.");
    }

    // ── Neon report CRUD ───────────────────────────────────────────────────────

    public async Task<ZfReportRecord?> FindByUniqueKeyAsync(
        Guid requestId, string reportType, int deliveryDocEntry,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT "Id","ReportId","RequestId","OrchestrationId","ReportType","Status",
                   "SalesOrderDocEntry","SalesOrderDocNum","DeliveryDocEntry","DeliveryDocNum",
                   "InvoiceDocEntry","InvoiceDocNum","CardCode","DeliveryLocation","ZoneRef","U_ReplitId",
                   "FileName","MimeType","FileSize","StorageProvider","StorageKey","Sha256",
                   "SnapshotJson","GeneratedAtUtc","UpdatedAtUtc","ErrorMessage"
            FROM "ZoneFulfillmentReports"
            WHERE "RequestId" = @rid AND "ReportType" = @rt AND "DeliveryDocEntry" = @dde
            LIMIT 1;
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", requestId);
        cmd.Parameters.AddWithValue("@rt",  reportType);
        cmd.Parameters.AddWithValue("@dde", deliveryDocEntry);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return ReadReport(rdr);
    }

    public async Task<ZfReportRecord?> FindByReportIdAsync(
        Guid reportId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT "Id","ReportId","RequestId","OrchestrationId","ReportType","Status",
                   "SalesOrderDocEntry","SalesOrderDocNum","DeliveryDocEntry","DeliveryDocNum",
                   "InvoiceDocEntry","InvoiceDocNum","CardCode","DeliveryLocation","ZoneRef","U_ReplitId",
                   "FileName","MimeType","FileSize","StorageProvider","StorageKey","Sha256",
                   "SnapshotJson","GeneratedAtUtc","UpdatedAtUtc","ErrorMessage"
            FROM "ZoneFulfillmentReports"
            WHERE "ReportId" = @rid
            LIMIT 1;
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", reportId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return ReadReport(rdr);
    }

    public async Task<List<ZfReportRecord>> FindByRequestIdAsync(
        Guid requestId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT "Id","ReportId","RequestId","OrchestrationId","ReportType","Status",
                   "SalesOrderDocEntry","SalesOrderDocNum","DeliveryDocEntry","DeliveryDocNum",
                   "InvoiceDocEntry","InvoiceDocNum","CardCode","DeliveryLocation","ZoneRef","U_ReplitId",
                   "FileName","MimeType","FileSize","StorageProvider","StorageKey","Sha256",
                   "SnapshotJson","GeneratedAtUtc","UpdatedAtUtc","ErrorMessage"
            FROM "ZoneFulfillmentReports"
            WHERE "RequestId" = @rid
            ORDER BY "Id" DESC;
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", requestId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ZfReportRecord>();
        while (await rdr.ReadAsync(ct)) list.Add(ReadReport(rdr));
        return list;
    }

    public async Task<List<ZfReportLine>> FindLinesByReportIdAsync(
        Guid reportId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT "Id","ReportId","LineSeq","ItemCode","Description",
                   "RequestedQty","PickedQty","DeliveredQty","UnitPrice","LineTotal",
                   "WhsCode","OpklAbsEntry","PickerUserId","PickerUserCode","PickerName",
                   "BinAbsEntry","BinCode","BinQty",
                   "SalesOrderBaseLine","DeliveryLineNum","InvoiceLineNum",
                   "InvoiceBaseType","InvoiceBaseEntry","InvoiceBaseLine"
            FROM "ZoneFulfillmentReportLines"
            WHERE "ReportId" = @rid
            ORDER BY "LineSeq";
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", reportId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ZfReportLine>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new ZfReportLine
            {
                Id                = rdr.GetInt64(rdr.GetOrdinal("Id")),
                ReportId          = rdr.GetGuid(rdr.GetOrdinal("ReportId")),
                LineSeq           = rdr.GetInt32(rdr.GetOrdinal("LineSeq")),
                ItemCode          = rdr.GetString(rdr.GetOrdinal("ItemCode")),
                Description       = rdr.IsDBNull(rdr.GetOrdinal("Description"))       ? null : rdr.GetString(rdr.GetOrdinal("Description")),
                RequestedQty      = rdr.IsDBNull(rdr.GetOrdinal("RequestedQty"))       ? null : rdr.GetDecimal(rdr.GetOrdinal("RequestedQty")),
                PickedQty         = rdr.IsDBNull(rdr.GetOrdinal("PickedQty"))          ? null : rdr.GetDecimal(rdr.GetOrdinal("PickedQty")),
                DeliveredQty      = rdr.IsDBNull(rdr.GetOrdinal("DeliveredQty"))       ? null : rdr.GetDecimal(rdr.GetOrdinal("DeliveredQty")),
                UnitPrice         = rdr.IsDBNull(rdr.GetOrdinal("UnitPrice"))          ? null : rdr.GetDecimal(rdr.GetOrdinal("UnitPrice")),
                LineTotal         = rdr.IsDBNull(rdr.GetOrdinal("LineTotal"))          ? null : rdr.GetDecimal(rdr.GetOrdinal("LineTotal")),
                WhsCode           = rdr.IsDBNull(rdr.GetOrdinal("WhsCode"))            ? null : rdr.GetString(rdr.GetOrdinal("WhsCode")),
                OpklAbsEntry      = rdr.IsDBNull(rdr.GetOrdinal("OpklAbsEntry"))       ? null : rdr.GetInt32(rdr.GetOrdinal("OpklAbsEntry")),
                PickerUserId      = rdr.IsDBNull(rdr.GetOrdinal("PickerUserId"))       ? null : rdr.GetInt32(rdr.GetOrdinal("PickerUserId")),
                PickerUserCode    = rdr.IsDBNull(rdr.GetOrdinal("PickerUserCode"))     ? null : rdr.GetString(rdr.GetOrdinal("PickerUserCode")),
                PickerName        = rdr.IsDBNull(rdr.GetOrdinal("PickerName"))         ? null : rdr.GetString(rdr.GetOrdinal("PickerName")),
                BinAbsEntry       = rdr.IsDBNull(rdr.GetOrdinal("BinAbsEntry"))        ? null : rdr.GetInt32(rdr.GetOrdinal("BinAbsEntry")),
                BinCode           = rdr.IsDBNull(rdr.GetOrdinal("BinCode"))            ? null : rdr.GetString(rdr.GetOrdinal("BinCode")),
                BinQty            = rdr.IsDBNull(rdr.GetOrdinal("BinQty"))             ? null : rdr.GetDecimal(rdr.GetOrdinal("BinQty")),
                SalesOrderBaseLine= rdr.IsDBNull(rdr.GetOrdinal("SalesOrderBaseLine")) ? null : rdr.GetInt32(rdr.GetOrdinal("SalesOrderBaseLine")),
                DeliveryLineNum   = rdr.IsDBNull(rdr.GetOrdinal("DeliveryLineNum"))    ? null : rdr.GetInt32(rdr.GetOrdinal("DeliveryLineNum")),
                InvoiceLineNum    = rdr.IsDBNull(rdr.GetOrdinal("InvoiceLineNum"))     ? null : rdr.GetInt32(rdr.GetOrdinal("InvoiceLineNum")),
                InvoiceBaseType   = rdr.IsDBNull(rdr.GetOrdinal("InvoiceBaseType"))    ? null : rdr.GetInt32(rdr.GetOrdinal("InvoiceBaseType")),
                InvoiceBaseEntry  = rdr.IsDBNull(rdr.GetOrdinal("InvoiceBaseEntry"))   ? null : rdr.GetInt32(rdr.GetOrdinal("InvoiceBaseEntry")),
                InvoiceBaseLine   = rdr.IsDBNull(rdr.GetOrdinal("InvoiceBaseLine"))    ? null : rdr.GetInt32(rdr.GetOrdinal("InvoiceBaseLine"))
            });
        }
        return list;
    }

    public async Task<long> InsertPendingAsync(ZfReportRecord r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO "ZoneFulfillmentReports"
                ("ReportId","RequestId","OrchestrationId","ReportType","Status",
                 "SalesOrderDocEntry","SalesOrderDocNum","DeliveryDocEntry","DeliveryDocNum",
                 "InvoiceDocEntry","InvoiceDocNum","CardCode","DeliveryLocation","ZoneRef","U_ReplitId",
                 "SnapshotJson","UpdatedAtUtc")
            VALUES
                (@rid,@reqId,@orchId,@rt,@status,
                 @sode,@sodn,@dde,@ddn,
                 @inde,@indn,@card,@dloc,@zr,@urid,
                 '{}'::jsonb,NOW())
            ON CONFLICT ("RequestId","ReportType","DeliveryDocEntry") DO NOTHING
            RETURNING "Id";
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid",    r.ReportId);
        cmd.Parameters.AddWithValue("@reqId",  r.RequestId);
        cmd.Parameters.AddWithValue("@orchId", r.OrchestrationId);
        cmd.Parameters.AddWithValue("@rt",     r.ReportType);
        cmd.Parameters.AddWithValue("@status", r.Status);
        cmd.Parameters.AddWithValue("@sode",   r.SalesOrderDocEntry);
        cmd.Parameters.AddWithValue("@sodn",   r.SalesOrderDocNum);
        cmd.Parameters.AddWithValue("@dde",    r.DeliveryDocEntry);
        cmd.Parameters.AddWithValue("@ddn",    r.DeliveryDocNum);
        cmd.Parameters.AddWithValue("@inde",   r.InvoiceDocEntry.HasValue ? (object)r.InvoiceDocEntry.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@indn",   r.InvoiceDocNum.HasValue   ? (object)r.InvoiceDocNum.Value   : DBNull.Value);
        cmd.Parameters.AddWithValue("@card",   r.CardCode);
        cmd.Parameters.AddWithValue("@dloc",   r.DeliveryLocation);
        cmd.Parameters.AddWithValue("@zr",     r.ZoneRef);
        cmd.Parameters.AddWithValue("@urid",   r.U_ReplitId);
        var id = await cmd.ExecuteScalarAsync(ct);
        return id is null ? 0L : Convert.ToInt64(id);
    }

    public async Task SaveSnapshotAsync(
        Guid reportId, string snapshotJson, string newStatus,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE "ZoneFulfillmentReports"
            SET "SnapshotJson" = @json::jsonb,
                "Status"       = @status,
                "UpdatedAtUtc" = NOW()
            WHERE "ReportId" = @rid;
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid",    reportId);
        cmd.Parameters.AddWithValue("@json",   snapshotJson);
        cmd.Parameters.AddWithValue("@status", newStatus);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertLinesAsync(
        Guid reportId, IEnumerable<ZfReportLine> lines,
        CancellationToken ct = default)
    {
        const string delSql = """DELETE FROM "ZoneFulfillmentReportLines" WHERE "ReportId" = @rid;""";
        const string insSql = """
            INSERT INTO "ZoneFulfillmentReportLines"
                ("ReportId","LineSeq","ItemCode","Description",
                 "RequestedQty","PickedQty","DeliveredQty","UnitPrice","LineTotal",
                 "WhsCode","OpklAbsEntry","PickerUserId","PickerUserCode","PickerName",
                 "BinAbsEntry","BinCode","BinQty",
                 "SalesOrderBaseLine","DeliveryLineNum","InvoiceLineNum",
                 "InvoiceBaseType","InvoiceBaseEntry","InvoiceBaseLine")
            VALUES
                (@rid,@seq,@item,@desc,
                 @rqty,@pqty,@dqty,@price,@total,
                 @whs,@opkl,@puid,@pucode,@pname,
                 @bae,@bc,@bqty,
                 @sobl,@dlnum,@inlnum,
                 @ibt,@ibe,@ibl)
            ON CONFLICT ("ReportId","LineSeq") DO UPDATE SET
                "ItemCode"   = EXCLUDED."ItemCode",
                "Description" = EXCLUDED."Description",
                "RequestedQty" = EXCLUDED."RequestedQty",
                "PickedQty"  = EXCLUDED."PickedQty",
                "DeliveredQty" = EXCLUDED."DeliveredQty",
                "UnitPrice"  = EXCLUDED."UnitPrice",
                "LineTotal"  = EXCLUDED."LineTotal",
                "WhsCode"    = EXCLUDED."WhsCode",
                "OpklAbsEntry" = EXCLUDED."OpklAbsEntry",
                "PickerUserId" = EXCLUDED."PickerUserId",
                "PickerUserCode" = EXCLUDED."PickerUserCode",
                "PickerName" = EXCLUDED."PickerName",
                "BinAbsEntry" = EXCLUDED."BinAbsEntry",
                "BinCode"    = EXCLUDED."BinCode",
                "BinQty"     = EXCLUDED."BinQty",
                "SalesOrderBaseLine" = EXCLUDED."SalesOrderBaseLine",
                "DeliveryLineNum" = EXCLUDED."DeliveryLineNum",
                "InvoiceLineNum" = EXCLUDED."InvoiceLineNum",
                "InvoiceBaseType" = EXCLUDED."InvoiceBaseType",
                "InvoiceBaseEntry" = EXCLUDED."InvoiceBaseEntry",
                "InvoiceBaseLine" = EXCLUDED."InvoiceBaseLine";
            """;

        var conn = await GetNeonConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var del = new NpgsqlCommand(delSql, conn, tx))
            {
                del.Parameters.AddWithValue("@rid", reportId);
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var l in lines)
            {
                await using var ins = new NpgsqlCommand(insSql, conn, tx);
                ins.Parameters.AddWithValue("@rid",    reportId);
                ins.Parameters.AddWithValue("@seq",    l.LineSeq);
                ins.Parameters.AddWithValue("@item",   l.ItemCode);
                ins.Parameters.AddWithValue("@desc",   l.Description   is null ? DBNull.Value : (object)l.Description);
                ins.Parameters.AddWithValue("@rqty",   l.RequestedQty  is null ? DBNull.Value : (object)l.RequestedQty.Value);
                ins.Parameters.AddWithValue("@pqty",   l.PickedQty     is null ? DBNull.Value : (object)l.PickedQty.Value);
                ins.Parameters.AddWithValue("@dqty",   l.DeliveredQty  is null ? DBNull.Value : (object)l.DeliveredQty.Value);
                ins.Parameters.AddWithValue("@price",  l.UnitPrice     is null ? DBNull.Value : (object)l.UnitPrice.Value);
                ins.Parameters.AddWithValue("@total",  l.LineTotal     is null ? DBNull.Value : (object)l.LineTotal.Value);
                ins.Parameters.AddWithValue("@whs",    l.WhsCode       is null ? DBNull.Value : (object)l.WhsCode);
                ins.Parameters.AddWithValue("@opkl",   l.OpklAbsEntry  is null ? DBNull.Value : (object)l.OpklAbsEntry.Value);
                ins.Parameters.AddWithValue("@puid",   l.PickerUserId  is null ? DBNull.Value : (object)l.PickerUserId.Value);
                ins.Parameters.AddWithValue("@pucode", l.PickerUserCode is null ? DBNull.Value : (object)l.PickerUserCode);
                ins.Parameters.AddWithValue("@pname",  l.PickerName    is null ? DBNull.Value : (object)l.PickerName);
                ins.Parameters.AddWithValue("@bae",    l.BinAbsEntry   is null ? DBNull.Value : (object)l.BinAbsEntry.Value);
                ins.Parameters.AddWithValue("@bc",     l.BinCode       is null ? DBNull.Value : (object)l.BinCode);
                ins.Parameters.AddWithValue("@bqty",   l.BinQty        is null ? DBNull.Value : (object)l.BinQty.Value);
                ins.Parameters.AddWithValue("@sobl",   l.SalesOrderBaseLine is null ? DBNull.Value : (object)l.SalesOrderBaseLine.Value);
                ins.Parameters.AddWithValue("@dlnum",  l.DeliveryLineNum    is null ? DBNull.Value : (object)l.DeliveryLineNum.Value);
                ins.Parameters.AddWithValue("@inlnum", l.InvoiceLineNum     is null ? DBNull.Value : (object)l.InvoiceLineNum.Value);
                ins.Parameters.AddWithValue("@ibt",    l.InvoiceBaseType    is null ? DBNull.Value : (object)l.InvoiceBaseType.Value);
                ins.Parameters.AddWithValue("@ibe",    l.InvoiceBaseEntry   is null ? DBNull.Value : (object)l.InvoiceBaseEntry.Value);
                ins.Parameters.AddWithValue("@ibl",    l.InvoiceBaseLine    is null ? DBNull.Value : (object)l.InvoiceBaseLine.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetGeneratedAsync(
        Guid reportId, string sha256, long fileSize, string fileName,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE "ZoneFulfillmentReports"
            SET "Status"         = 'Generated',
                "Sha256"         = @sha,
                "FileSize"       = @size,
                "FileName"       = @fn,
                "MimeType"       = 'application/pdf',
                "StorageProvider"= 'InMemory',
                "GeneratedAtUtc" = NOW(),
                "UpdatedAtUtc"   = NOW()
            WHERE "ReportId" = @rid;
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid",  reportId);
        cmd.Parameters.AddWithValue("@sha",  sha256);
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@fn",   fileName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetFailedAsync(
        Guid reportId, string errorMessage,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE "ZoneFulfillmentReports"
            SET "Status"       = 'Failed',
                "ErrorMessage" = @err,
                "UpdatedAtUtc" = NOW()
            WHERE "ReportId" = @rid;
            """;
        var conn = await GetNeonConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@rid", reportId);
        cmd.Parameters.AddWithValue("@err", errorMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── MolasIntegration context lookups ───────────────────────────────────────

    /// <summary>
    /// Resolves all orchestration context from a known OINV DocEntry.
    /// Joins InvoiceRecord → DeliveryRecord → FulfillmentOrchestration → FulfillmentRequest.
    /// Returns null if the invoice is not from a ZF-tracked orchestration.
    /// </summary>
    public async Task<ZfOrchestrationContext?> FindOrchestrationContextByOinvAsync(
        int oinvDocEntry, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                fo.Id          AS OrchId,
                fo.RequestId   AS RequestId,
                fo.U_ReplitId  AS U_ReplitId,
                fo.SoDocEntry  AS SoDocEntry,
                fo.SoDocNum    AS SoDocNum,
                fo.DeliveryLocation AS DeliveryLocation,
                fo.CreatedAtUtc AS OrchCreatedAt,
                fr.CardCode    AS CardCode,
                fr.DocDate     AS SoDocDate,
                fr.DeliveryDate AS SoDeliveryDate,
                fr.SlpCode     AS SlpCode,
                dr.Id          AS DeliveryRecordId,
                dr.SapDocEntry AS OdlnDocEntry,
                dr.SapDocNum   AS OdlnDocNum,
                dr.CreatedAtUtc AS DeliveryCreatedAt,
                ir.Id          AS InvoiceRecordId,
                ir.SapDocEntry AS OinvDocEntry,
                ir.SapDocNum   AS OinvDocNum,
                ir.CreatedAtUtc AS InvoiceCreatedAt
            FROM dbo.InvoiceRecord ir
            JOIN dbo.DeliveryRecord dr ON dr.Id = ir.DeliveryRecordId
            JOIN dbo.FulfillmentOrchestration fo ON fo.Id = dr.OrchestrationId
            JOIN dbo.FulfillmentRequest fr ON fr.RequestId = fo.RequestId
            WHERE ir.SapDocEntry = @oinv
              AND ir.Status = N'Created';
            """;

        await using var conn = new SqlConnection(_molasCs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@oinv", oinvDocEntry);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;

        return new ZfOrchestrationContext
        {
            OrchestrationId    = rdr.GetInt64(rdr.GetOrdinal("OrchId")),
            RequestId          = rdr.GetGuid(rdr.GetOrdinal("RequestId")),
            U_ReplitId         = rdr.IsDBNull(rdr.GetOrdinal("U_ReplitId")) ? "" : rdr.GetString(rdr.GetOrdinal("U_ReplitId")),
            SoDocEntry         = rdr.IsDBNull(rdr.GetOrdinal("SoDocEntry")) ? 0 : rdr.GetInt32(rdr.GetOrdinal("SoDocEntry")),
            SoDocNum           = rdr.IsDBNull(rdr.GetOrdinal("SoDocNum"))   ? 0 : rdr.GetInt32(rdr.GetOrdinal("SoDocNum")),
            DeliveryLocation   = rdr.GetString(rdr.GetOrdinal("DeliveryLocation")),
            OrchCreatedAt      = rdr.GetDateTime(rdr.GetOrdinal("OrchCreatedAt")),
            CardCode           = rdr.GetString(rdr.GetOrdinal("CardCode")),
            SoDocDate          = rdr.GetDateTime(rdr.GetOrdinal("SoDocDate")),
            SoDeliveryDate     = rdr.GetDateTime(rdr.GetOrdinal("SoDeliveryDate")),
            SlpCode            = rdr.IsDBNull(rdr.GetOrdinal("SlpCode")) ? 0 : rdr.GetInt32(rdr.GetOrdinal("SlpCode")),
            DeliveryRecordId   = rdr.GetInt64(rdr.GetOrdinal("DeliveryRecordId")),
            OdlnDocEntry       = rdr.IsDBNull(rdr.GetOrdinal("OdlnDocEntry")) ? 0 : rdr.GetInt32(rdr.GetOrdinal("OdlnDocEntry")),
            OdlnDocNum         = rdr.IsDBNull(rdr.GetOrdinal("OdlnDocNum"))   ? 0 : rdr.GetInt32(rdr.GetOrdinal("OdlnDocNum")),
            DeliveryCreatedAt  = rdr.GetDateTime(rdr.GetOrdinal("DeliveryCreatedAt")),
            InvoiceRecordId    = rdr.GetInt64(rdr.GetOrdinal("InvoiceRecordId")),
            OinvDocEntry       = rdr.IsDBNull(rdr.GetOrdinal("OinvDocEntry")) ? (int?)null : rdr.GetInt32(rdr.GetOrdinal("OinvDocEntry")),
            OinvDocNum         = rdr.IsDBNull(rdr.GetOrdinal("OinvDocNum"))   ? (int?)null : rdr.GetInt32(rdr.GetOrdinal("OinvDocNum")),
            InvoiceCreatedAt   = rdr.GetDateTime(rdr.GetOrdinal("InvoiceCreatedAt"))
        };
    }

    /// <summary>
    /// Returns SoLineFragments + FulfillmentRequestLine data joined, ordered by SoLineNum.
    /// </summary>
    public async Task<List<ZfLineContext>> GetLineContextsAsync(
        long orchestrationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                slf.Id             AS FragId,
                slf.SoLineNum      AS SoLineNum,
                slf.ItemCode       AS ItemCode,
                slf.WhsCode        AS WhsCode,
                slf.SoLineQty      AS SoLineQty,
                slf.AllocatedQty   AS AllocatedQty,
                slf.DeliveredQty   AS DeliveredQty,
                frl.LineSeq        AS LineSeq,
                frl.RequestedQty   AS RequestedQty,
                frl.UnitPrice      AS UnitPrice,
                frl.Description    AS Description,
                frl.U_ItemName     AS U_ItemName
            FROM dbo.SoLineFragment slf
            JOIN dbo.FulfillmentOrchestration fo ON fo.Id = slf.OrchestrationId
            JOIN dbo.FulfillmentRequestLine frl
                ON frl.RequestId = fo.RequestId AND frl.RequestLineId = slf.RequestLineId
            WHERE slf.OrchestrationId = @orchId
            ORDER BY slf.SoLineNum;
            """;

        await using var conn = new SqlConnection(_molasCs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", orchestrationId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ZfLineContext>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new ZfLineContext
            {
                FragId       = rdr.GetInt64(0),
                SoLineNum    = rdr.GetInt32(1),
                ItemCode     = rdr.GetString(2),
                WhsCode      = rdr.GetString(3),
                SoLineQty    = rdr.GetDecimal(4),
                AllocatedQty = rdr.GetDecimal(5),
                DeliveredQty = rdr.GetDecimal(6),
                LineSeq      = rdr.GetInt32(7),
                RequestedQty = rdr.GetDecimal(8),
                UnitPrice    = rdr.GetDecimal(9),
                Description  = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                U_ItemName   = rdr.IsDBNull(11) ? null : rdr.GetString(11)
            });
        }
        return list;
    }

    /// <summary>
    /// Returns pick + bin detail for each SoLineFragment in this orchestration.
    /// Joins PickListRecord → PickListFragmentRecord → DeliveryFragmentRecord → DeliveryFragmentBinRecord
    /// and PickerAssignment for picker identity.
    /// </summary>
    public async Task<List<ZfPickDetail>> GetPickDetailsAsync(
        long orchestrationId, long deliveryRecordId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                plfr.SoLineFragmentId  AS FragId,
                plr.PickListAbsEntry   AS OpklAbsEntry,
                plfr.PickedQty         AS PickedQty,
                dfr.DlnLineNum         AS DlnLineNum,
                dfr.DeliveredQty       AS DeliveredQty,
                dfb.BinAbsEntry        AS BinAbsEntry,
                dfb.BinCode            AS BinCode,
                dfb.Quantity           AS BinQty,
                pa.SapUserId           AS PickerUserId,
                pa.UserId              AS PickerUserCode,
                pa.UserName            AS PickerName
            FROM dbo.PickListFragmentRecord plfr
            JOIN dbo.PickListRecord plr ON plr.Id = plfr.PickListRecordId
            LEFT JOIN dbo.DeliveryFragmentRecord dfr
                ON dfr.FragmentId = plfr.SoLineFragmentId
               AND dfr.DeliveryRecordId = @drid
            LEFT JOIN dbo.DeliveryFragmentBinRecord dfb ON dfb.DeliveryFragmentRecordId = dfr.Id
            LEFT JOIN dbo.PickerAssignment pa
                ON pa.WhsCode = plfr.WhsCode AND pa.IsActive = 1 AND pa.IsDefault = 1
            WHERE plr.OrchestrationId = @orchId
            ORDER BY plfr.SoLineFragmentId, dfb.BinAbsEntry;
            """;

        await using var conn = new SqlConnection(_molasCs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@orchId", orchestrationId);
        cmd.Parameters.AddWithValue("@drid",   deliveryRecordId);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ZfPickDetail>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new ZfPickDetail
            {
                FragId         = rdr.GetInt64(0),
                OpklAbsEntry   = rdr.GetInt32(1),
                PickedQty      = rdr.IsDBNull(2) ? 0m : rdr.GetDecimal(2),
                DlnLineNum     = rdr.IsDBNull(3) ? (int?)null : rdr.GetInt32(3),
                DeliveredQty   = rdr.IsDBNull(4) ? (decimal?)null : rdr.GetDecimal(4),
                BinAbsEntry    = rdr.IsDBNull(5) ? (int?)null : rdr.GetInt32(5),
                BinCode        = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                BinQty         = rdr.IsDBNull(7) ? (decimal?)null : rdr.GetDecimal(7),
                PickerUserId   = rdr.IsDBNull(8) ? (int?)null : rdr.GetInt32(8),
                PickerUserCode = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                PickerName     = rdr.IsDBNull(10) ? null : rdr.GetString(10)
            });
        }
        return list;
    }

    // ── Neon connection helper ─────────────────────────────────────────────────

    private async Task<NpgsqlConnection> GetNeonConnectionAsync(CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_neon.Database.GetDbConnection();
        if (conn.State == ConnectionState.Broken) await conn.CloseAsync();
        if (conn.State != ConnectionState.Open)   await conn.OpenAsync(ct);
        return conn;
    }

    // ── Neon row mapper ────────────────────────────────────────────────────────

    private static ZfReportRecord ReadReport(NpgsqlDataReader r) => new()
    {
        Id                = r.GetInt64(0),
        ReportId          = r.GetGuid(1),
        RequestId         = r.GetGuid(2),
        OrchestrationId   = r.GetInt64(3),
        ReportType        = r.GetString(4),
        Status            = r.GetString(5),
        SalesOrderDocEntry= r.GetInt32(6),
        SalesOrderDocNum  = r.GetInt32(7),
        DeliveryDocEntry  = r.GetInt32(8),
        DeliveryDocNum    = r.GetInt32(9),
        InvoiceDocEntry   = r.IsDBNull(10) ? null : r.GetInt32(10),
        InvoiceDocNum     = r.IsDBNull(11) ? null : r.GetInt32(11),
        CardCode          = r.GetString(12),
        DeliveryLocation  = r.GetString(13),
        ZoneRef           = r.GetString(14),
        U_ReplitId        = r.GetString(15),
        FileName          = r.IsDBNull(16) ? null : r.GetString(16),
        MimeType          = r.IsDBNull(17) ? null : r.GetString(17),
        FileSize          = r.IsDBNull(18) ? null : r.GetInt64(18),
        StorageProvider   = r.IsDBNull(19) ? null : r.GetString(19),
        StorageKey        = r.IsDBNull(20) ? null : r.GetString(20),
        Sha256            = r.IsDBNull(21) ? null : r.GetString(21),
        SnapshotJson      = r.IsDBNull(22) ? null : r.GetString(22),
        GeneratedAtUtc    = r.IsDBNull(23) ? null : r.GetDateTime(23),
        UpdatedAtUtc      = r.GetDateTime(24),
        ErrorMessage      = r.IsDBNull(25) ? null : r.GetString(25)
    };
}

// ── Supporting context records (internal to ZF report system) ────────────────

public sealed class ZfOrchestrationContext
{
    public long      OrchestrationId   { get; set; }
    public Guid      RequestId         { get; set; }
    public string    U_ReplitId        { get; set; } = "";
    public int       SoDocEntry        { get; set; }
    public int       SoDocNum          { get; set; }
    public string    DeliveryLocation  { get; set; } = "";
    public DateTime  OrchCreatedAt     { get; set; }
    public string    CardCode          { get; set; } = "";
    public DateTime  SoDocDate         { get; set; }
    public DateTime  SoDeliveryDate    { get; set; }
    public int       SlpCode           { get; set; }
    public long      DeliveryRecordId  { get; set; }
    public int       OdlnDocEntry      { get; set; }
    public int       OdlnDocNum        { get; set; }
    public DateTime  DeliveryCreatedAt { get; set; }
    public long      InvoiceRecordId   { get; set; }
    public int?      OinvDocEntry      { get; set; }
    public int?      OinvDocNum        { get; set; }
    public DateTime  InvoiceCreatedAt  { get; set; }
}

public sealed class ZfLineContext
{
    public long      FragId       { get; set; }
    public int       SoLineNum    { get; set; }
    public string    ItemCode     { get; set; } = "";
    public string    WhsCode      { get; set; } = "";
    public decimal   SoLineQty    { get; set; }
    public decimal   AllocatedQty { get; set; }
    public decimal   DeliveredQty { get; set; }
    public int       LineSeq      { get; set; }
    public decimal   RequestedQty { get; set; }
    public decimal   UnitPrice    { get; set; }
    public string?   Description  { get; set; }
    public string?   U_ItemName   { get; set; }
}

public sealed class ZfPickDetail
{
    public long      FragId         { get; set; }
    public int       OpklAbsEntry   { get; set; }
    public decimal   PickedQty      { get; set; }
    public int?      DlnLineNum     { get; set; }
    public decimal?  DeliveredQty   { get; set; }
    public int?      BinAbsEntry    { get; set; }
    public string?   BinCode        { get; set; }
    public decimal?  BinQty         { get; set; }
    public int?      PickerUserId   { get; set; }
    public string?   PickerUserCode { get; set; }
    public string?   PickerName     { get; set; }
}
