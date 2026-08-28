using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Payments;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=13 (OINV), TransactionType=A.
/// Reads the invoice from SAP, updates SQLite and Neon in targeted transactions.
/// Never advances SyncMetadata["Invoice"] or NeonMirror:Invoices — those are
/// owned by InvoiceDeltaSyncJob and NeonSyncJob exclusively.
/// </summary>
public sealed class InvoiceEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InvoiceCacheService _cache;
    private readonly NeonEventWriteService _neon;
    private readonly ILogger<InvoiceEventHandler> _logger;

    public InvoiceEventHandler(
        SapService sap,
        InvoiceCacheService cache,
        NeonEventWriteService neon,
        ILogger<InvoiceEventHandler> logger)
    {
        _sap    = sap;
        _cache  = cache;
        _neon   = neon;
        _logger = logger;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "13" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 1) Validate DocEntry
            if (ev.DocEntry is not { } docEntry)
            {
                _logger.LogWarning("[InvoiceHandler] EventId={EventId} has null DocEntry — skipping.", ev.EventId);
                return (false, "DocEntry is null for 13/A event");
            }

            // 2) Read invoice from SAP
            var dto = await _sap.GetInvoiceByDocEntryAsync(docEntry, ct);
            if (dto is null)
            {
                _logger.LogWarning("[InvoiceHandler] DocEntry={DocEntry} not found in OINV — EventId={EventId}.",
                    docEntry, ev.EventId);
                return (false, $"Invoice DocEntry={docEntry} not found in SAP");
            }

            // 3) Physical inventory observability probe
            bool hasInventoryMovement = await _sap.CheckOinmAsync(13, docEntry, ct);
            if (hasInventoryMovement)
            {
                _logger.LogInformation(
                    "[InvoiceHandler] InvoicePhysicalInventoryDetected: DocEntry={DocEntry} DocNum={DocNum} EventId={EventId}. " +
                    "Invoice directly drove a stock posting in OINM.",
                    docEntry, dto.DocNum, ev.EventId);
            }

            // 4) Lifecycle status for DocStatusDisplay
            var lifecycle = _sap.GetInvoiceLifecycleStatusResults(new[] { docEntry });

            // 5) Map to CachedInvoice + CachedInvoiceLines
            var header = MapToHeader(dto, lifecycle);
            var lines  = MapToLines(dto);

            // 6) SQLite: header UPSERT + lines replace (one tx)
            await _cache.UpsertSingleInvoiceAsync(header, lines, ct);

            // 7) Neon: header UPSERT + lines replace (one tx)
            await _neon.UpsertInvoiceAsync(header, lines, ct);

            // 8) Cancellation-pair refresh: SAP fires only ONE 13/A event — for the cancellation
            //    document (CANCELED='C'). The original invoice's state change (to CANCELED='Y') is
            //    NOT emitted as a separate event. The link comes from INV1.BaseEntry (SAP's
            //    authoritative document chain — confirmed from live MOLAS_Live_2021 data).
            if (dto.Canceled == "Cancellation")
            {
                var originalDocEntry = await _sap.GetOriginalInvoiceDocEntryAsync(docEntry, ct);
                if (originalDocEntry.HasValue)
                {
                    _logger.LogInformation(
                        "[InvoiceHandler] InvoiceCancellationPairDetected: CancellationDocEntry={CancellationDocEntry} " +
                        "OriginalDocEntry={OriginalDocEntry} EventId={EventId}",
                        docEntry, originalDocEntry.Value, ev.EventId);

                    var originalDto = await _sap.GetInvoiceByDocEntryAsync(originalDocEntry.Value, ct);
                    if (originalDto is not null)
                    {
                        var origLifecycle = _sap.GetInvoiceLifecycleStatusResults(new[] { originalDocEntry.Value });
                        var origHeader    = MapToHeader(originalDto, origLifecycle);
                        var origLines     = MapToLines(originalDto);

                        await _cache.UpsertSingleInvoiceAsync(origHeader, origLines, ct);
                        await _neon.UpsertInvoiceAsync(origHeader, origLines, ct);

                        _logger.LogInformation(
                            "[InvoiceHandler] InvoiceCancellationPairRefreshed: CancellationDocEntry={CancellationDocEntry} " +
                            "OriginalDocEntry={OriginalDocEntry} ElapsedMs={ElapsedMs:F1} EventId={EventId}",
                            docEntry, originalDocEntry.Value, sw.Elapsed.TotalMilliseconds, ev.EventId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[InvoiceHandler] CancellationPair: OriginalDocEntry={OriginalDocEntry} not found in SAP — " +
                            "original not refreshed. EventId={EventId}",
                            originalDocEntry.Value, ev.EventId);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[InvoiceHandler] CancellationDoc DocEntry={DocEntry} has no INV1.BaseEntry (BaseType=13) — " +
                        "original not identified. EventId={EventId}",
                        docEntry, ev.EventId);
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "[InvoiceHandler] Done: DocEntry={DocEntry} DocNum={DocNum} Status={Status} " +
                "DocStatusDisplay={Display} Lines={LineCount} {Elapsed:F1}ms EventId={EventId}",
                docEntry, dto.DocNum, header.DocStatus, header.DocStatusDisplay,
                lines.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[InvoiceHandler] Failed for DocEntry={DocEntry} EventId={EventId} after {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CachedInvoice MapToHeader(
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
            GroupNum          = dto.GroupNum
        };
    }

    private static List<CachedInvoiceLine> MapToLines(InvoiceDto dto)
    {
        var lines = new List<CachedInvoiceLine>(dto.Lines?.Count ?? 0);
        foreach (var l in dto.Lines ?? Enumerable.Empty<InvoiceLineDto>())
        {
            lines.Add(new CachedInvoiceLine
            {
                DocEntry   = dto.DocEntry,
                LineNum    = (int)l.LineNum,
                ItemCode   = l.ItemCode ?? "",
                Dscription = l.Dscription ?? "",
                Quantity   = l.Quantity,
                Price      = l.Price,
                LineTotal  = l.LineTotal,
                U_Item_Name = l.U_Item_Name ?? "",
                U_MdlTEST  = l.U_MdlTEST ?? ""
            });
        }
        return lines;
    }

    private static string ComputeLegacyStatusDisplay(string docStatus, string canceled)
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
