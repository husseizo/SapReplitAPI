using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Models.ZoneFulfillment;
using System.Security.Cryptography;
using System.Text.Json;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Orchestrates ZF final fulfillment report lifecycle:
///   1. CaptureSnapshotAsync — triggered by 13/A InvoiceEventHandler; persists immutable snapshot to Neon.
///   2. GeneratePdfBytesAsync — on-demand PDF from SnapshotJson; never touches SAP.
///   3. Query helpers for report list + metadata endpoints.
/// No PDF is generated during 13/A. No SAP documents are created or modified.
/// </summary>
public sealed class ZoneFulfillmentReportService
{
    private readonly ZoneFulfillmentReportRepository    _repo;
    private readonly ZoneFulfillmentReportCacheService  _cache;
    private readonly ILogger<ZoneFulfillmentReportService> _log;

    // ── Design tokens ─────────────────────────────────────────────────────────
    private const string NavyBlue   = "#1F3864";
    private const string MedBlue    = "#2E75B6";
    private const string TableHdrBg = "#D6E4F0";
    private const string SuccessGreen = "#375623";
    private const string LightGrey  = "#F5F5F5";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ZoneFulfillmentReportService(
        ZoneFulfillmentReportRepository repo,
        ZoneFulfillmentReportCacheService cache,
        ILogger<ZoneFulfillmentReportService> log)
    {
        _repo  = repo;
        _cache = cache;
        _log   = log;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    public Task EnsureTablesAsync(CancellationToken ct = default)
        => _repo.EnsureTablesAsync(ct);

    // ── Snapshot capture (called from InvoiceEventHandler 13/A) ──────────────

    /// <summary>
    /// Builds and persists an immutable ZF fulfillment snapshot in Neon.
    /// Must never throw: any failure is caught + logged by the caller.
    /// Must never call GeneratePdfBytesAsync.
    /// </summary>
    public async Task CaptureSnapshotAsync(
        int oinvDocEntry, InvoiceDto dto, CancellationToken ct)
    {
        _log.LogInformation(
            "[ZF-REPORT] SnapshotCapture start: OinvDocEntry={De} DocNum={Dn}",
            oinvDocEntry, dto.DocNum);

        // 1) Resolve orchestration context from MolasIntegration
        var ctx = await _repo.FindOrchestrationContextByOinvAsync(oinvDocEntry, ct);
        if (ctx is null)
        {
            _log.LogWarning(
                "[ZF-REPORT] OrchestrationContextNotFound: OinvDocEntry={De} — no InvoiceRecord/DeliveryRecord/Orchestration chain found. Snapshot skipped.",
                oinvDocEntry);
            return;
        }

        // 2) Idempotency: check if SnapshotReady or Generated already exists
        var existing = await _repo.FindByUniqueKeyAsync(
            ctx.RequestId, "FinalFulfillment", ctx.OdlnDocEntry, ct);

        if (existing is { Status: ZfReportStatus.SnapshotReady or ZfReportStatus.Generated })
        {
            _log.LogInformation(
                "[ZF-REPORT] ExistingReportFound: ReportId={Rid} Status={St} RequestId={Req} — skip duplicate snapshot.",
                existing.ReportId, existing.Status, ctx.RequestId);
            return;
        }

        // 3) If Pending or null → insert / reuse pending row
        Guid reportId;
        if (existing is null)
        {
            var newRecord = new ZfReportRecord
            {
                ReportId          = Guid.NewGuid(),
                RequestId         = ctx.RequestId,
                OrchestrationId   = ctx.OrchestrationId,
                ReportType        = "FinalFulfillment",
                Status            = ZfReportStatus.Pending,
                SalesOrderDocEntry= ctx.SoDocEntry,
                SalesOrderDocNum  = ctx.SoDocNum,
                DeliveryDocEntry  = ctx.OdlnDocEntry,
                DeliveryDocNum    = ctx.OdlnDocNum,
                InvoiceDocEntry   = ctx.OinvDocEntry,
                InvoiceDocNum     = ctx.OinvDocNum,
                CardCode          = ctx.CardCode,
                DeliveryLocation  = ctx.DeliveryLocation,
                ZoneRef           = "ZoneFulfillment",
                U_ReplitId        = ctx.U_ReplitId
            };
            var insertedId = await _repo.InsertPendingAsync(newRecord, ct);
            if (insertedId == 0)
            {
                // ON CONFLICT DO NOTHING fired — another invocation beat us; re-read
                existing = await _repo.FindByUniqueKeyAsync(
                    ctx.RequestId, "FinalFulfillment", ctx.OdlnDocEntry, ct);
                if (existing is { Status: ZfReportStatus.SnapshotReady or ZfReportStatus.Generated })
                {
                    _log.LogInformation("[ZF-REPORT] ConcurrentSnapshotWon: ReportId={Rid}", existing.ReportId);
                    return;
                }
                reportId = existing?.ReportId ?? newRecord.ReportId;
            }
            else
            {
                reportId = newRecord.ReportId;
            }
        }
        else
        {
            // Failed → retry; Pending → resume
            reportId = existing.ReportId;
        }

        _log.LogInformation("[ZF-REPORT] BuildingSnapshot: ReportId={Rid} RequestId={Req}", reportId, ctx.RequestId);

        // 4) Gather line + pick data from MolasIntegration
        var lineContexts = await _repo.GetLineContextsAsync(ctx.OrchestrationId, ct);
        var pickDetails  = await _repo.GetPickDetailsAsync(ctx.OrchestrationId, ctx.DeliveryRecordId, ct);

        // Group pick details by FragId for quick lookup (take first per fragment for primary pick info)
        var pickByFrag = pickDetails
            .GroupBy(p => p.FragId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build snapshot lines
        var snapshotLines = new List<ZfSnapshotLine>(lineContexts.Count);
        var reportLines   = new List<ZfReportLine>(lineContexts.Count);
        int seq = 1;

        foreach (var lc in lineContexts)
        {
            // Find matching invoice line by ItemCode (best effort — InvoiceLineDto has no BaseLine)
            var invLine = dto.Lines?.FirstOrDefault(l =>
                string.Equals(l.ItemCode, lc.ItemCode, StringComparison.OrdinalIgnoreCase));

            pickByFrag.TryGetValue(lc.FragId, out var pd);

            var snapLine = new ZfSnapshotLine
            {
                LineSeq            = seq,
                ItemCode           = lc.ItemCode,
                Description        = invLine?.Dscription ?? lc.Description ?? lc.U_ItemName,
                RequestedQty       = lc.RequestedQty,
                PickedQty          = pd?.PickedQty ?? lc.AllocatedQty,
                DeliveredQty       = pd?.DeliveredQty ?? lc.DeliveredQty,
                UnitPrice          = invLine?.Price ?? lc.UnitPrice,
                LineTotal          = invLine?.LineTotal ?? (lc.UnitPrice * lc.AllocatedQty),
                WhsCode            = lc.WhsCode,
                OpklAbsEntry       = pd?.OpklAbsEntry,
                PickerUserId       = pd?.PickerUserId,
                PickerUserCode     = pd?.PickerUserCode,
                PickerName         = pd?.PickerName,
                BinAbsEntry        = pd?.BinAbsEntry,
                BinCode            = pd?.BinCode,
                BinQty             = pd?.BinQty,
                SalesOrderBaseLine = lc.SoLineNum,
                DeliveryLineNum    = pd?.DlnLineNum,
                InvoiceLineNum     = invLine is null ? null : (int?)invLine.LineNum,
                InvoiceBaseType    = invLine is null ? null : 15,     // ODLN
                InvoiceBaseEntry   = invLine is null ? null : ctx.OdlnDocEntry,
                InvoiceBaseLine    = invLine is null ? null : lc.SoLineNum
            };
            snapshotLines.Add(snapLine);

            reportLines.Add(new ZfReportLine
            {
                ReportId           = reportId,
                LineSeq            = seq,
                ItemCode           = lc.ItemCode,
                Description        = snapLine.Description,
                RequestedQty       = snapLine.RequestedQty,
                PickedQty          = snapLine.PickedQty,
                DeliveredQty       = snapLine.DeliveredQty,
                UnitPrice          = snapLine.UnitPrice,
                LineTotal          = snapLine.LineTotal,
                WhsCode            = snapLine.WhsCode,
                OpklAbsEntry       = snapLine.OpklAbsEntry,
                PickerUserId       = snapLine.PickerUserId,
                PickerUserCode     = snapLine.PickerUserCode,
                PickerName         = snapLine.PickerName,
                BinAbsEntry        = snapLine.BinAbsEntry,
                BinCode            = snapLine.BinCode,
                BinQty             = snapLine.BinQty,
                SalesOrderBaseLine = snapLine.SalesOrderBaseLine,
                DeliveryLineNum    = snapLine.DeliveryLineNum,
                InvoiceLineNum     = snapLine.InvoiceLineNum,
                InvoiceBaseType    = snapLine.InvoiceBaseType,
                InvoiceBaseEntry   = snapLine.InvoiceBaseEntry,
                InvoiceBaseLine    = snapLine.InvoiceBaseLine
            });
            seq++;
        }

        // 5) Build snapshot object
        var snapshot = new ZfReportSnapshot
        {
            ReportVersion  = "1",
            RequestId      = ctx.RequestId.ToString(),
            OrchestrationId= ctx.OrchestrationId.ToString(),
            U_ReplitId     = ctx.U_ReplitId,
            DeliveryLocation= ctx.DeliveryLocation,
            ZoneRef        = "ZoneFulfillment",
            CardCode       = dto.CardCode ?? ctx.CardCode,
            CardName       = dto.CardName,
            SlpCode        = dto.SalesEmployeeCode,
            SlpName        = dto.SalesEmployeeName,
            SoDocEntry     = ctx.SoDocEntry,
            SoDocNum       = ctx.SoDocNum,
            SoDocDate      = ctx.SoDocDate.ToString("yyyy-MM-dd"),
            SoDeliveryDate = ctx.SoDeliveryDate.ToString("yyyy-MM-dd"),
            OdlnDocEntry   = ctx.OdlnDocEntry,
            OdlnDocNum     = ctx.OdlnDocNum,
            OdlnDocDate    = ctx.DeliveryCreatedAt.ToString("yyyy-MM-dd"),
            OinvDocEntry   = ctx.OinvDocEntry,
            OinvDocNum     = ctx.OinvDocNum,
            OinvDocTotal   = dto.DocTotal,
            OinvCurrency   = null,
            OinvDocDate    = dto.DocDate.ToString("yyyy-MM-dd"),
            Lines          = snapshotLines,
            Timeline       = new ZfAutomationTimeline
            {
                OrchCreatedAt      = ctx.OrchCreatedAt.ToString("o"),
                DeliveryCreatedAt  = ctx.DeliveryCreatedAt.ToString("o"),
                InvoiceCreatedAt   = ctx.InvoiceCreatedAt.ToString("o"),
                SnapshotCapturedAt = DateTime.UtcNow.ToString("o")
            }
        };

        // 6) Serialize + persist snapshot JSON + report lines to Neon (idempotent)
        var json = JsonSerializer.Serialize(snapshot, _jsonOpts);
        await _repo.SaveSnapshotAsync(reportId, json, ZfReportStatus.SnapshotReady, ct);
        await _repo.InsertLinesAsync(reportId, reportLines, ct);

        _log.LogInformation(
            "[ZF-REPORT] NeonSnapshotSaved: ReportId={Rid} RequestId={Req} Lines={Lc} OinvDocEntry={De}",
            reportId, ctx.RequestId, snapshotLines.Count, oinvDocEntry);

        // 7) Mirror to SQLite local cache — failure must never block 13/A
        try
        {
            var neonRecord = await _repo.FindByReportIdAsync(reportId, ct);
            if (neonRecord is not null)
            {
                var cached = await _cache.UpsertAsync(neonRecord, reportLines, ct);
                if (cached)
                    _log.LogInformation(
                        "[ZF-REPORT] LocalCacheSaved: ReportId={Rid} RequestId={Req}", reportId, ctx.RequestId);
            }
        }
        catch (Exception cacheEx)
        {
            _log.LogError(cacheEx,
                "[ZF-REPORT] LocalCacheFailed: ReportId={Rid} — Neon snapshot is durable, pipeline continues.",
                reportId);
        }

        _log.LogInformation(
            "[ZF-REPORT] SnapshotReady: ReportId={Rid} RequestId={Req} Lines={Lc} OinvDocEntry={De}",
            reportId, ctx.RequestId, snapshotLines.Count, oinvDocEntry);
    }

    // ── On-demand PDF generation ──────────────────────────────────────────────

    /// <summary>
    /// Generates PDF bytes from SnapshotJson. Never touches SAP.
    /// Updates report Status → Generated with SHA-256 and file size.
    /// Returns null if the report or snapshot is not found.
    /// </summary>
    public async Task<(byte[] Pdf, ZfReportRecord Report)?> GeneratePdfBytesAsync(
        Guid reportId, CancellationToken ct)
    {
        // Prefer local cache; fall through to Neon if missing/stale
        ZfReportRecord? report;
        string source = "neon";
        try
        {
            var cached = await _cache.GetReportAsync(reportId, ct);
            if (cached is not null)
            {
                report = cached.Value.Record;
                source = cached.Value.Source;
            }
            else
            {
                report = await _repo.FindByReportIdAsync(reportId, ct);
            }
        }
        catch
        {
            report = await _repo.FindByReportIdAsync(reportId, ct);
        }

        if (report is null)
        {
            _log.LogWarning("[ZF-REPORT] GeneratePdf: ReportId={Rid} not found.", reportId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(report.SnapshotJson) || report.SnapshotJson == "{}")
        {
            _log.LogWarning("[ZF-REPORT] GeneratePdf: ReportId={Rid} has no snapshot data (source={Src}).", reportId, source);
            return null;
        }

        ZfReportSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ZfReportSnapshot>(report.SnapshotJson, _jsonOpts)!;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-REPORT] GeneratePdf: SnapshotJsonDeserializeFailed ReportId={Rid}", reportId);
            return null;
        }

        // Generate PDF in-memory
        var pdfBytes = Document.Create(c => BuildDocument(c, report, snapshot)).GeneratePdf();

        // Compute SHA-256
        var sha256Bytes = SHA256.HashData(pdfBytes);
        var sha256Hex   = Convert.ToHexString(sha256Bytes).ToLowerInvariant();

        var fileName = BuildFileName(report, snapshot);

        await _repo.SetGeneratedAsync(reportId, sha256Hex, pdfBytes.Length, fileName, ct);

        // Re-read from Neon to get authoritative updated record
        var updated = await _repo.FindByReportIdAsync(reportId, ct) ?? report;

        // Refresh local cache with Generated status + SHA256 (best-effort)
        try
        {
            var lines = await _repo.FindLinesByReportIdAsync(reportId, ct);
            await _cache.UpsertAsync(updated, lines, ct);
        }
        catch (Exception cacheEx)
        {
            _log.LogWarning(cacheEx,
                "[ZF-REPORT] LocalCacheFailed (post-PDF): ReportId={Rid} — PDF generated, Neon updated, local cache will refresh on next read.",
                reportId);
        }

        _log.LogInformation(
            "[ZF-REPORT] PdfGenerated: ReportId={Rid} Bytes={B} Sha256={Sha} Source={Src}",
            reportId, pdfBytes.Length, sha256Hex, source);

        return (pdfBytes, updated);
    }

    // ── Query helpers for controller ──────────────────────────────────────────

    public Task<List<ZfReportRecord>> GetReportsForRequestAsync(Guid requestId, CancellationToken ct)
        => _cache.GetReportsForRequestAsync(requestId, ct);

    public async Task<ZfReportRecord?> GetReportAsync(Guid reportId, CancellationToken ct)
    {
        var result = await _cache.GetReportAsync(reportId, ct);
        return result?.Record;
    }

    /// <summary>
    /// Explicitly rehydrates SQLite from Neon for a given report.
    /// Used by cache-loss recovery / reconciliation flows.
    /// </summary>
    public Task HydrateLocalCacheAsync(Guid reportId, CancellationToken ct)
        => _cache.HydrateFromNeonAsync(reportId, ct);

    /// <summary>
    /// Removes local SQLite cache entry for a report (for cache-loss regression testing only).
    /// Never touches Neon.
    /// </summary>
    public Task DeleteLocalCacheAsync(Guid reportId, CancellationToken ct)
        => _cache.DeleteLocalAsync(reportId, ct);

    // ── PDF filename convention ───────────────────────────────────────────────

    private static string BuildFileName(ZfReportRecord report, ZfReportSnapshot snap)
    {
        var date   = snap.OinvDocDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var reqShort = report.RequestId.ToString("N")[..8];
        return $"ZF_FinalFulfillment_{date}_{reqShort}_ODLN{report.DeliveryDocEntry}_OINV{report.InvoiceDocEntry}.pdf";
    }

    // ── QuestPDF document builder ─────────────────────────────────────────────

    private static void BuildDocument(
        IDocumentContainer container,
        ZfReportRecord report,
        ZfReportSnapshot snap)
    {
        container.Page(page =>
        {
            page.Size(297, 210, Unit.Millimetre);   // A4 Landscape
            page.MarginHorizontal(15, Unit.Millimetre);
            page.MarginVertical(12, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(8));

            // ── Header ───────────────────────────────────────────────────────
            page.Header().Column(hdr =>
            {
                hdr.Item().Background(NavyBlue).PaddingHorizontal(10).PaddingVertical(7)
                    .Row(row =>
                    {
                        row.RelativeItem()
                            .Text("AUTOHUB INVENTORY MANAGEMENT")
                            .FontSize(13).Bold().FontColor(Colors.White);
                        row.RelativeItem().Text(t =>
                        {
                            t.AlignRight();
                            t.Span("Zone Fulfillment — Final Fulfillment Report")
                                .FontSize(9).FontColor(Colors.Grey.Lighten3);
                        });
                    });
                hdr.Item().Height(3).Background(MedBlue);
            });

            // ── Content ───────────────────────────────────────────────────────
            page.Content().PaddingTop(8).Column(col =>
            {
                col.Spacing(10);

                // §1 Order identity + document chain
                col.Item().Element(c => ComposeIdentitySection(c, report, snap));

                // §2 Financial summary
                col.Item().Element(c => ComposeFinancialSection(c, report, snap));

                // §3 Line items table
                if (snap.Lines.Count > 0)
                    col.Item().Element(c => ComposeLineTable(c, snap));

                // §4 Timeline
                if (snap.Timeline is not null)
                    col.Item().Element(c => ComposeTimeline(c, snap.Timeline));
            });

            // ── Footer ────────────────────────────────────────────────────────
            page.Footer().BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4)
                .Row(row =>
                {
                    row.RelativeItem()
                        .Text($"Autohub Inventory Management  ·  ZF Report {report.ReportId.ToString("N")[..8]}  ·  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                        .FontSize(7).FontColor(Colors.Grey.Darken2);
                    row.ConstantItem(60).Text(t =>
                    {
                        t.AlignRight();
                        t.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken2));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
        });
    }

    // ── §1 Identity + document chain ─────────────────────────────────────────

    private static void ComposeIdentitySection(
        IContainer root, ZfReportRecord report, ZfReportSnapshot snap)
    {
        root.Border(1).BorderColor(MedBlue).Column(col =>
        {
            col.Item().Background(MedBlue).Padding(5)
                .Text("Order Identity & Document Chain").FontSize(9).Bold().FontColor(Colors.White);

            col.Item().Padding(8).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    SummaryRow(left, "RequestId",         snap.RequestId);
                    SummaryRow(left, "U_ReplitId",        snap.U_ReplitId);
                    SummaryRow(left, "Card Code",         snap.CardCode);
                    SummaryRow(left, "Card Name",         snap.CardName ?? "—");
                    SummaryRow(left, "Delivery Location", snap.DeliveryLocation);
                    SummaryRow(left, "Sales Employee",    snap.SlpName ?? $"Code {snap.SlpCode}");
                });
                row.RelativeItem().Column(right =>
                {
                    SummaryRow(right, "ORDR DocEntry",   snap.SoDocEntry.ToString());
                    SummaryRow(right, "ORDR DocNum",     snap.SoDocNum.ToString());
                    SummaryRow(right, "ORDR Date",       snap.SoDocDate ?? "—");
                    SummaryRow(right, "Delivery Date",   snap.SoDeliveryDate ?? "—");
                    SummaryRow(right, "ODLN DocEntry",   snap.OdlnDocEntry.ToString());
                    SummaryRow(right, "ODLN DocNum",     snap.OdlnDocNum.ToString());
                    SummaryRow(right, "OINV DocEntry",   snap.OinvDocEntry?.ToString() ?? "—");
                    SummaryRow(right, "OINV DocNum",     snap.OinvDocNum?.ToString()   ?? "—");
                    SummaryRow(right, "OINV Date",       snap.OinvDocDate ?? "—");
                });
            });
        });
    }

    // ── §2 Financial summary ─────────────────────────────────────────────────

    private static void ComposeFinancialSection(
        IContainer root, ZfReportRecord report, ZfReportSnapshot snap)
    {
        root.Border(1).BorderColor(Colors.Grey.Lighten2).Column(col =>
        {
            col.Item().Background(TableHdrBg).Padding(5)
                .Text("Financial Summary").FontSize(9).Bold().FontColor(NavyBlue);

            col.Item().Padding(8).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    SummaryRow(left, "Invoice Total",  $"{snap.OinvDocTotal:N2}");
                    SummaryRow(left, "Currency",       snap.OinvCurrency ?? "SAR");
                    SummaryRow(left, "Line Count",     snap.Lines.Count.ToString());
                });
                row.RelativeItem().Column(right =>
                {
                    SummaryRow(right, "Report Status", report.Status, SuccessGreen);
                    SummaryRow(right, "Report ID",     report.ReportId.ToString("D")[..18] + "…");
                    SummaryRow(right, "Report Type",   report.ReportType);
                });
            });
        });
    }

    // ── §3 Line items table ───────────────────────────────────────────────────

    private static void ComposeLineTable(IContainer root, ZfReportSnapshot snap)
    {
        root.Border(1).BorderColor(Colors.Grey.Lighten2).Column(col =>
        {
            col.Item().Background(TableHdrBg).Padding(5)
                .Text("Line Items").FontSize(9).Bold().FontColor(NavyBlue);

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(22);  // #
                    c.RelativeColumn(2);   // ItemCode
                    c.RelativeColumn(3);   // Description
                    c.ConstantColumn(45);  // WHS
                    c.ConstantColumn(40);  // Req Qty
                    c.ConstantColumn(40);  // Picked
                    c.ConstantColumn(40);  // Delivered
                    c.ConstantColumn(55);  // Unit Price
                    c.ConstantColumn(55);  // Line Total
                    c.RelativeColumn(2);   // Bin
                    c.RelativeColumn(2);   // Picker
                });

                // Header row
                static IContainer HeaderCell(IContainer c) =>
                    c.Background(NavyBlue).Padding(4);

                table.Header(h =>
                {
                    foreach (var hdr in new[] { "#", "Item Code", "Description", "WHS",
                        "Req Qty", "Picked", "Delivered", "Unit Price", "Total", "Bin", "Picker" })
                    {
                        h.Cell().Element(HeaderCell)
                            .Text(hdr).FontSize(7).Bold().FontColor(Colors.White);
                    }
                });

                // Data rows
                bool alt = false;
                foreach (var l in snap.Lines)
                {
                    var bg = alt ? LightGrey : "#FFFFFF";
                    alt = !alt;

                    static IContainer DataCell(IContainer c, string bg) =>
                        c.Background(bg).BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(4);

                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.LineSeq.ToString()).FontSize(7);
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.ItemCode).FontSize(7).Bold();
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.Description ?? "—").FontSize(7);
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.WhsCode ?? "—").FontSize(7);
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.RequestedQty.ToString("N2")).FontSize(7).AlignRight();
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.PickedQty.ToString("N2")).FontSize(7).AlignRight();
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.DeliveredQty.ToString("N2")).FontSize(7).AlignRight();
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.UnitPrice.ToString("N2")).FontSize(7).AlignRight();
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.LineTotal.ToString("N2")).FontSize(7).Bold().AlignRight();
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.BinCode ?? "—").FontSize(7);
                    table.Cell().Element(c => DataCell(c, bg))
                        .Text(l.PickerName ?? "—").FontSize(7);
                }
            });
        });
    }

    // ── §4 Timeline ───────────────────────────────────────────────────────────

    private static void ComposeTimeline(IContainer root, ZfAutomationTimeline tl)
    {
        root.Border(1).BorderColor(Colors.Grey.Lighten2).Column(col =>
        {
            col.Item().Background(TableHdrBg).Padding(5)
                .Text("Automation Timeline").FontSize(9).Bold().FontColor(NavyBlue);
            col.Item().Padding(8).Column(inner =>
            {
                if (tl.OrchCreatedAt    is not null) SummaryRow(inner, "Orchestration Created", tl.OrchCreatedAt);
                if (tl.DeliveryCreatedAt is not null) SummaryRow(inner, "Delivery Created",      tl.DeliveryCreatedAt);
                if (tl.InvoiceCreatedAt  is not null) SummaryRow(inner, "Invoice Created",       tl.InvoiceCreatedAt);
                if (tl.SnapshotCapturedAt is not null) SummaryRow(inner, "Snapshot Captured",   tl.SnapshotCapturedAt);
            });
        });
    }

    // ── Shared layout helpers ─────────────────────────────────────────────────

    private static void SummaryRow(
        ColumnDescriptor col, string label, string value, string? valueColor = null)
    {
        col.Item().PaddingBottom(3).Row(row =>
        {
            row.ConstantItem(160).Text(label + ":").FontSize(8).FontColor(Colors.Grey.Darken2);
            var span = row.RelativeItem().Text(value).FontSize(8).Bold();
            if (valueColor is not null) span.FontColor(valueColor);
        });
    }
}
