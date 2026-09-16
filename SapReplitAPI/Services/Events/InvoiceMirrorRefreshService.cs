using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Shared per-invoice refresh: SAP read → lifecycle → map → SQLite targeted write → Neon targeted write.
/// Used by InvoiceEventHandler (event fast-path) and InvoiceDriftDetectionJob (drift repair).
/// Never touches SyncMetadata watermarks. Never does ZF, OINM, inventory, or delivery side effects.
/// </summary>
public sealed class InvoiceMirrorRefreshService : IInvoiceMirrorRefresher
{
    private readonly SapService _sap;
    private readonly InvoiceCacheService _cache;
    private readonly NeonEventWriteService _neon;
    private readonly ILogger<InvoiceMirrorRefreshService> _logger;

    public InvoiceMirrorRefreshService(
        SapService sap,
        InvoiceCacheService cache,
        NeonEventWriteService neon,
        ILogger<InvoiceMirrorRefreshService> logger)
    {
        _sap    = sap;
        _cache  = cache;
        _neon   = neon;
        _logger = logger;
    }

    public async Task<RefreshResult> RefreshAsync(int docEntry, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var dto = await _sap.GetInvoiceByDocEntryAsync(docEntry, ct);
            if (dto is null)
                return Fail(docEntry, $"Invoice DocEntry={docEntry} not found in SAP", sw);

            double sapReadMs = sw.Elapsed.TotalMilliseconds;

            var lifecycle = _sap.GetInvoiceLifecycleStatusResults(new[] { docEntry });
            var header    = MapToHeader(dto, lifecycle);
            var lines     = MapToLines(dto);

            await _cache.UpsertSingleInvoiceAsync(header, lines, ct);
            double sqliteMs = sw.Elapsed.TotalMilliseconds - sapReadMs;

            await _neon.UpsertInvoiceAsync(header, lines, ct);
            double neonMs = sw.Elapsed.TotalMilliseconds - sapReadMs - sqliteMs;

            sw.Stop();
            return new RefreshResult(
                Ok: true, Error: null,
                DocEntry: docEntry, DocNum: dto.DocNum,
                LineCount: lines.Count,
                SapReadMs: sapReadMs, SqliteMs: sqliteMs, NeonMs: neonMs,
                TotalMs: sw.Elapsed.TotalMilliseconds,
                Dto: dto, Header: header, Lines: lines);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "[InvoiceMirror] RefreshAsync failed DocEntry={DocEntry} after {Elapsed:F1}ms",
                docEntry, sw.Elapsed.TotalMilliseconds);
            return Fail(docEntry, $"{ex.GetType().Name}: {ex.Message}", sw);
        }
    }

    private static RefreshResult Fail(int docEntry, string error, System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        return new RefreshResult(
            Ok: false, Error: error,
            DocEntry: docEntry, DocNum: 0,
            LineCount: 0,
            SapReadMs: 0, SqliteMs: 0, NeonMs: 0,
            TotalMs: sw.Elapsed.TotalMilliseconds,
            Dto: null, Header: null, Lines: null);
    }

    internal static CachedInvoice MapToHeader(
        InvoiceDto dto,
        IReadOnlyDictionary<int, InvoiceLifecycleStatusResult> lifecycle)
    {
        string docStatus = dto.Status ?? "";
        string canceled  = dto.Canceled ?? "";

        string docStatusDisplay = lifecycle.TryGetValue(dto.DocEntry, out var lc)
            ? lc.DocStatusDisplay
            : ComputeLegacyStatusDisplay(docStatus, canceled);

        return new CachedInvoice
        {
            DocEntry          = dto.DocEntry,
            DocNum            = dto.DocNum,
            InvoiceDocNum     = dto.DocNum,
            DocDate           = dto.DocDate,
            DocStatus         = docStatus,
            Canceled          = canceled,
            DocStatusDisplay  = docStatusDisplay,
            CardCode          = dto.CardCode ?? "",
            CardName          = dto.CardName ?? "",
            DocTotal          = dto.DocTotal,
            PaidToDate        = dto.PaidToDate,
            BalanceDue        = dto.BalanceDue,
            DaysOverdue       = dto.DaysOverdue,
            SalesEmployeeCode = dto.SalesEmployeeCode,
            SalesEmployeeName = dto.SalesEmployeeName ?? "",
            GroupNum          = dto.GroupNum,
            ZoneRef           = dto.ZoneRef,
            U_ReplitId        = dto.U_ReplitId,
            DeliveryLocation  = dto.DeliveryLocation
        };
    }

    internal static List<CachedInvoiceLine> MapToLines(InvoiceDto dto)
    {
        var lines = new List<CachedInvoiceLine>(dto.Lines?.Count ?? 0);
        foreach (var l in dto.Lines ?? Enumerable.Empty<InvoiceLineDto>())
        {
            lines.Add(new CachedInvoiceLine
            {
                DocEntry       = dto.DocEntry,
                LineNum        = (int)l.LineNum,
                ItemCode       = l.ItemCode ?? "",
                Dscription     = l.Dscription ?? "",
                Quantity       = l.Quantity,
                Price          = l.Price,
                LineTotal      = l.LineTotal,
                U_Item_Name    = l.U_Item_Name ?? "",
                U_MdlTEST      = l.U_MdlTEST ?? "",
                U_MDLTsT       = l.U_MDLTsT ?? "",
                U_ItemName     = l.U_ItemName ?? "",
                U_Manufacturer = l.U_Manufacturer ?? ""
            });
        }
        return lines;
    }

    internal static string ComputeLegacyStatusDisplay(string docStatus, string canceled)
    {
        if (docStatus == "O") return "Open";
        if (docStatus == "C")
        {
            if (string.Equals(canceled, "Canceled",      StringComparison.OrdinalIgnoreCase)) return "Cancelled";
            if (string.Equals(canceled, "Cancellation",  StringComparison.OrdinalIgnoreCase)) return "Cancellation";
            return "Closed";
        }
        return string.IsNullOrWhiteSpace(docStatus) ? "Unknown" : docStatus;
    }
}

public sealed record RefreshResult(
    bool Ok,
    string? Error,
    int DocEntry,
    int DocNum,
    int LineCount,
    double SapReadMs,
    double SqliteMs,
    double NeonMs,
    double TotalMs,
    InvoiceDto? Dto,
    CachedInvoice? Header,
    IReadOnlyList<CachedInvoiceLine>? Lines);
