using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SapReplitAPI.Models.SoDelivery;

namespace SapReplitAPI.Services.SoDelivery;

/// <summary>
/// Generates PDF reports from SQLite audit data only — never queries SAP.
/// Historical reports always reflect what was logged at processing time.
///
/// QuestPDF license: set once at startup in Program.cs (LicenseType.Community).
/// Upgrade to Professional or Enterprise if annual gross revenue exceeds USD $1M.
/// </summary>
public class SoDeliveryReportService
{
    private readonly SoDeliveryDbService          _db;
    private readonly ILogger<SoDeliveryReportService> _log;

    private const string ReportDir = @"C:\CacheDbs\Autohub Inventory reports";

    // ── Design tokens ─────────────────────────────────────────────────────────
    private const string NavyBlue    = "#1F3864";
    private const string MedBlue     = "#2E75B6";
    private const string TableHdrBg  = "#D6E4F0";
    private const string SuccessGreen = "#375623";
    private const string ErrorRed    = "#C00000";
    private const string AbortAmber  = "#833C00";
    private const string VioletDark  = "#4B0082";

    public SoDeliveryReportService(SoDeliveryDbService db, ILogger<SoDeliveryReportService> log)
    {
        _db  = db;
        _log = log;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public string GetReportDirectoryPath()
    {
        Directory.CreateDirectory(ReportDir);
        return ReportDir;
    }

    /// <summary>
    /// Generates a PDF for the given run, saves it, updates Run.PdfPath, and returns the full path.
    /// Writes to a .tmp file first, then atomically renames it — a failed generation never leaves
    /// a corrupt PDF at the final path.
    /// Throws on error without modifying Run.Status — PDF is reporting, not transaction processing.
    /// </summary>
    public async Task<string> GeneratePdfAsync(int runId)
    {
        var run = await _db.GetRunByIdAsync(runId)
            ?? throw new InvalidOperationException($"Run {runId} not found — cannot generate PDF.");

        var dir      = GetReportDirectoryPath();
        var filename = BuildFilename(run);
        var final    = Path.Combine(dir, filename);
        var temp     = final + ".tmp";

        try
        {
            var doc = Document.Create(c => BuildDocument(c, run));
            doc.GeneratePdf(temp);

            // Atomic rename — File.Move with overwrite:true is atomic on the same volume on Windows
            File.Move(temp, final, overwrite: true);

            // Only persist PdfPath once the file is confirmed on disk
            run.PdfPath = final;
            await _db.UpdateRunAsync(run);

            _log.LogInformation(
                "PDF generated — RunId={RunId} File={File} Bytes={Bytes}",
                run.Id, filename, new FileInfo(final).Length);

            return final;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDF generation failed for RunId={RunId} — run status is unchanged", runId);
            try { File.Delete(temp); } catch { /* best-effort cleanup of temp file */ }
            throw; // propagate so controller/caller can surface the error separately
        }
    }

    /// <summary>
    /// Returns the PDF bytes for a run.
    /// If PdfPath is set and the file exists, serves from disk.
    /// If the file is missing (deleted/moved), regenerates from SQLite — no SAP access.
    /// </summary>
    public async Task<byte[]> GetOrGeneratePdfAsync(int runId)
    {
        var run = await _db.GetRunByIdAsync(runId)
            ?? throw new InvalidOperationException($"Run {runId} not found.");

        string path;
        if (!string.IsNullOrEmpty(run.PdfPath) && File.Exists(run.PdfPath))
            path = run.PdfPath;
        else
            path = await GeneratePdfAsync(runId);

        return await File.ReadAllBytesAsync(path);
    }

    // ── Filename ──────────────────────────────────────────────────────────────

    private static string BuildFilename(SoDeliveryRun run)
    {
        // RunId is always part of the filename so multiple runs on the same date
        // (including forced re-runs) never overwrite each other.
        var date = run.ProcessingDate.ToString("yyyy-MM-dd");
        return run.IsForced
            ? $"SalesOrderDeliveryReport_{date}_Run_{run.Id}_Forced.pdf"
            : $"SalesOrderDeliveryReport_{date}_Run_{run.Id}.pdf";
    }

    // ── Document composition ──────────────────────────────────────────────────

    private static void BuildDocument(IDocumentContainer container, SoDeliveryRun run)
    {
        var allLogs      = run.Logs?.OrderBy(l => l.SoDocNum).ToList() ?? new List<SoDeliveryLog>();
        var successLogs  = allLogs.Where(l => l.Status == SoLogStatus.Success).ToList();
        var failedLogs   = allLogs.Where(l => l.Status == SoLogStatus.Failed).ToList();
        var exceptionLogs = allLogs.Where(l => l.Status == SoLogStatus.Exception).ToList();
        var skippedLogs  = allLogs.Where(l => l.Status == SoLogStatus.Skipped
                                           || l.Status == SoLogStatus.SkippedAlreadyDone).ToList();

        container.Page(page =>
        {
            // A4 Landscape — 297 × 210 mm — wider tables fit without clipping
            page.Size(297, 210, Unit.Millimetre);
            page.MarginHorizontal(15, Unit.Millimetre);
            page.MarginVertical(12, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(8));

            page.Header().Column(hdr =>
            {
                hdr.Item().Background(NavyBlue).PaddingHorizontal(10).PaddingVertical(7)
                    .Row(row =>
                    {
                        row.RelativeItem()
                            .Text("AUTOHUB INVENTORY MANAGEMENT")
                            .FontSize(13).Bold().FontColor(Colors.White);

                        row.RelativeItem()
                            .Text(t =>
                            {
                                t.AlignRight();
                                t.Span("Nightly Sales Order → Delivery Report")
                                    .FontSize(9).FontColor(Colors.Grey.Lighten3);
                            });
                    });

                hdr.Item().Height(3).Background(MedBlue);
            });

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Spacing(10);

                // ── Run summary ───────────────────────────────────────────────
                col.Item().Element(x => ComposeRunSummary(x, run));

                // ── Aborted banner ────────────────────────────────────────────
                if (run.Status == RunStatus.Aborted)
                    col.Item().Element(x => ComposeAbortedBanner(x, run));

                // ── Empty run ─────────────────────────────────────────────────
                if (run.TotalOrders == 0)
                {
                    col.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(12)
                        .Text("No open Sales Orders were found for the processing date.")
                        .FontSize(9).Italic().FontColor(Colors.Grey.Darken2);
                    return;
                }

                // ── Sections — only render if there are entries ───────────────
                if (successLogs.Count > 0)
                    col.Item().Element(x => ComposeSuccessSection(x, successLogs));

                if (failedLogs.Count > 0)
                    col.Item().Element(x => ComposeFailedSection(x, failedLogs));

                if (exceptionLogs.Count > 0)
                    col.Item().Element(x => ComposeExceptionSection(x, exceptionLogs));

                if (skippedLogs.Count > 0)
                    col.Item().Element(x => ComposeSkippedSection(x, skippedLogs));

                if (successLogs.Count > 0)
                    col.Item().Element(x => ComposeLineAuditSection(x, successLogs));
            });

            page.Footer().BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4)
                .Row(row =>
                {
                    row.RelativeItem()
                        .Text($"Autohub Inventory Management  ·  Run #{run.Id}" +
                              $"  ·  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                        .FontSize(7).FontColor(Colors.Grey.Darken2);

                    row.ConstantItem(60)
                        .Text(t =>
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

    // ── Run summary box ───────────────────────────────────────────────────────

    private static void ComposeRunSummary(IContainer root, SoDeliveryRun run)
    {
        var duration = run.EndTime.HasValue
            ? (run.EndTime.Value - run.StartTime).TotalSeconds
            : (double?)null;

        int processedCount = run.SuccessCount + run.FailedCount + run.SkippedCount + run.ExceptionCount;

        root.Border(1).BorderColor(MedBlue).Column(col =>
        {
            col.Item().Background(MedBlue).Padding(5)
                .Text("Run Summary").FontSize(9).Bold().FontColor(Colors.White);

            col.Item().Padding(8).Row(row =>
            {
                // Left: identity + timing
                row.RelativeItem().Column(left =>
                {
                    SummaryRow(left, "Run ID",          $"#{run.Id}");
                    SummaryRow(left, "Processing Date", run.ProcessingDate.ToString("dddd, dd MMMM yyyy"));
                    SummaryRow(left, "Triggered By",    run.TriggeredBy);
                    SummaryRow(left, "Forced Run",      run.IsForced ? "YES" : "No");
                    SummaryRow(left, "Start Time",      run.StartTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
                    SummaryRow(left, "End Time",        run.EndTime.HasValue
                        ? run.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "—");
                    SummaryRow(left, "Duration",        duration.HasValue ? $"{duration:F1}s" : "—");
                });

                // Right: counters
                row.RelativeItem().Column(right =>
                {
                    var statusColor = run.Status == RunStatus.Aborted    ? ErrorRed
                                    : run.Status == RunStatus.Completed  ? SuccessGreen
                                                                         : MedBlue;
                    SummaryRow(right, "Status",                   run.Status, statusColor);
                    SummaryRow(right, "Total Orders Found",        run.TotalOrders.ToString());
                    SummaryRow(right, "Successfully Converted",    run.SuccessCount.ToString());
                    SummaryRow(right, "Failed",                    run.FailedCount.ToString());
                    SummaryRow(right, "Skipped",                   run.SkippedCount.ToString());
                    SummaryRow(right, "Exceptions",                run.ExceptionCount.ToString());
                    SummaryRow(right, "Delivery Notes Created",    run.TotalDeliveriesCreated.ToString());
                    if (run.Status == RunStatus.Aborted)
                        SummaryRow(right, "Persisted Results (partial)", processedCount.ToString(), AbortAmber);
                });
            });
        });
    }

    private static void SummaryRow(ColumnDescriptor col, string label, string value, string? valueColor = null)
    {
        col.Item().PaddingBottom(3).Row(row =>
        {
            row.ConstantItem(160).Text(label + ":").FontSize(8).FontColor(Colors.Grey.Darken2);
            var span = row.RelativeItem().Text(value).FontSize(8).Bold();
            if (valueColor != null) span.FontColor(valueColor);
        });
    }

    // ── Aborted banner ────────────────────────────────────────────────────────

    private static void ComposeAbortedBanner(IContainer root, SoDeliveryRun run)
    {
        root.Background("#FFF0F0").Border(2).BorderColor(ErrorRed).Padding(10).Column(col =>
        {
            col.Item()
                .Text("⚠   RUN ABORTED — PROCESSING INCOMPLETE")
                .FontSize(11).Bold().FontColor(ErrorRed);

            if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
                col.Item().PaddingTop(5)
                    .Text(run.ErrorMessage).FontSize(8).FontColor(AbortAmber);
        });
    }

    // ── Successful conversions ────────────────────────────────────────────────

    private static void ComposeSuccessSection(IContainer root, List<SoDeliveryLog> logs)
    {
        root.Column(col =>
        {
            SectionTitle(col, $"Successful Conversions ({logs.Count})", SuccessGreen);

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(52);   // SO DocNum
                    c.RelativeColumn(2.5f); // Customer
                    c.ConstantColumn(38);   // Lines
                    c.ConstantColumn(62);   // ODLN DocNum
                    c.ConstantColumn(100);  // Processed At
                    c.ConstantColumn(52);   // Duration
                });

                table.Header(h =>
                {
                    Th(h.Cell(), "SO DocNum");
                    Th(h.Cell(), "Customer");
                    Th(h.Cell(), "Lines");
                    Th(h.Cell(), "ODLN DocNum");
                    Th(h.Cell(), "Processed At (UTC)");
                    Th(h.Cell(), "Duration");
                });

                bool alt = false;
                foreach (var log in logs)
                {
                    string bg = alt ? "#F0FBF0" : Colors.White; alt = !alt;
                    Td(table, $"SO-{log.SoDocNum}", bg);
                    Td(table, $"{log.CustomerCode}  {log.CustomerName}", bg);
                    Td(table, log.Lines.Count.ToString(), bg);
                    Td(table, log.DeliveryDocNum.HasValue ? $"ODLN-{log.DeliveryDocNum}" : "—", bg);
                    Td(table, log.ProcessedAt.ToString("yyyy-MM-dd HH:mm:ss"), bg);
                    Td(table, $"{log.DurationMs:N0} ms", bg);
                }
            });
        });
    }

    // ── Failed orders ─────────────────────────────────────────────────────────

    private static void ComposeFailedSection(IContainer root, List<SoDeliveryLog> logs)
    {
        root.Column(col =>
        {
            SectionTitle(col, $"Failed Orders — Insufficient Stock ({logs.Count})", ErrorRed);

            foreach (var log in logs)
            {
                col.Item().PaddingTop(5)
                    .Border(1).BorderColor(Colors.Grey.Lighten2)
                    .Column(inner =>
                    {
                        // Order header bar
                        inner.Item().Background("#FFF0F0").Padding(5).Row(row =>
                        {
                            row.RelativeItem()
                                .Text($"SO-{log.SoDocNum}   {log.CustomerCode}  {log.CustomerName}")
                                .FontSize(8).Bold().FontColor(ErrorRed);

                            row.ConstantItem(190)
                                .Text($"Processed: {log.ProcessedAt:yyyy-MM-dd HH:mm:ss} UTC  " +
                                      $"({log.DurationMs:N0} ms)")
                                .FontSize(7).FontColor(Colors.Grey.Darken2);
                        });

                        if (!string.IsNullOrWhiteSpace(log.ErrorMessage))
                            inner.Item().PaddingHorizontal(7).PaddingBottom(4)
                                .Text(log.ErrorMessage).FontSize(7).Italic().FontColor(AbortAmber);

                        // Line detail table
                        inner.Item().PaddingHorizontal(6).PaddingBottom(5).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(32);   // Line
                                c.ConstantColumn(72);   // Item Code
                                c.RelativeColumn(2f);   // Description
                                c.ConstantColumn(48);   // Open Qty
                                c.ConstantColumn(45);   // Warehouse
                                c.ConstantColumn(50);   // On Hand
                                c.ConstantColumn(48);   // Required
                                c.ConstantColumn(50);   // Shortage
                                c.ConstantColumn(55);   // Status
                            });

                            table.Header(h =>
                            {
                                Th(h.Cell(), "Line"); Th(h.Cell(), "Item Code"); Th(h.Cell(), "Description");
                                Th(h.Cell(), "Open Qty"); Th(h.Cell(), "Warehouse");
                                Th(h.Cell(), "On Hand"); Th(h.Cell(), "Required"); Th(h.Cell(), "Shortage"); Th(h.Cell(), "Status");
                            });

                            bool alt = false;
                            foreach (var line in log.Lines.OrderBy(l => l.LineNum))
                            {
                                string bg  = alt ? "#F7F7F7" : Colors.White; alt = !alt;
                                decimal? shortage = line.OnHandBefore < line.OpenQuantity
                                    ? line.OpenQuantity - line.OnHandBefore : null;

                                Td(table, line.LineNum.ToString(), bg);
                                Td(table, line.ItemCode, bg);
                                Td(table, line.ItemDescription, bg);
                                Td(table, line.OpenQuantity.ToString("N2"), bg);
                                Td(table, line.WarehouseCode, bg);
                                Td(table, line.OnHandBefore.ToString("N2"), bg);
                                Td(table, line.OpenQuantity.ToString("N2"), bg);
                                Td(table, shortage.HasValue ? shortage.Value.ToString("N2") : "—",
                                    bg, shortage.HasValue ? ErrorRed : null);
                                Td(table, line.Status, bg,
                                    line.Status == LineLogStatus.Insufficient ? ErrorRed : null);
                            }
                        });

                        inner.Item().PaddingHorizontal(7).PaddingBottom(4)
                            .Text("No warehouse substitution attempted.")
                            .FontSize(7).Italic().FontColor(Colors.Grey.Darken2);
                    });
            }
        });
    }

    // ── Exceptions ────────────────────────────────────────────────────────────

    private static void ComposeExceptionSection(IContainer root, List<SoDeliveryLog> logs)
    {
        root.Column(col =>
        {
            SectionTitle(col, $"Exception Orders ({logs.Count})", VioletDark);

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(52);   // SO DocNum
                    c.RelativeColumn(2f);   // Customer
                    c.RelativeColumn(3f);   // Error Message
                    c.ConstantColumn(58);   // SAP Code
                    c.RelativeColumn(2f);   // SAP Error
                });

                table.Header(h =>
                {
                    Th(h.Cell(), "SO DocNum"); Th(h.Cell(), "Customer"); Th(h.Cell(), "Error Message");
                    Th(h.Cell(), "SAP Code"); Th(h.Cell(), "SAP Error");
                });

                bool alt = false;
                foreach (var log in logs)
                {
                    string bg = alt ? "#FDF5FF" : Colors.White; alt = !alt;
                    Td(table, $"SO-{log.SoDocNum}", bg);
                    Td(table, $"{log.CustomerCode}  {log.CustomerName}", bg);
                    Td(table, log.ErrorMessage ?? "—", bg, ErrorRed);
                    Td(table, log.SapErrorCode ?? "—", bg);
                    Td(table, log.SapErrorMessage ?? "—", bg);
                }
            });
        });
    }

    // ── Skipped orders ────────────────────────────────────────────────────────

    private static void ComposeSkippedSection(IContainer root, List<SoDeliveryLog> logs)
    {
        root.Column(col =>
        {
            SectionTitle(col, $"Skipped Orders ({logs.Count})", "#595959");

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(52);   // SO DocNum
                    c.RelativeColumn(2f);   // Customer
                    c.RelativeColumn(2f);   // Reason
                });

                table.Header(h =>
                {
                    Th(h.Cell(), "SO DocNum"); Th(h.Cell(), "Customer"); Th(h.Cell(), "Reason");
                });

                bool alt = false;
                foreach (var log in logs)
                {
                    string bg = alt ? "#F5F5F5" : Colors.White; alt = !alt;
                    Td(table, $"SO-{log.SoDocNum}", bg);
                    Td(table, $"{log.CustomerCode}  {log.CustomerName}", bg);
                    Td(table, log.Status == SoLogStatus.SkippedAlreadyDone
                        ? "Already delivered — SKIPPED_ALREADY_PROCESSED"
                        : log.ErrorMessage ?? "No open lines", bg);
                }
            });
        });
    }

    // ── Successful line-level inventory audit ─────────────────────────────────

    private static void ComposeLineAuditSection(IContainer root, List<SoDeliveryLog> successLogs)
    {
        root.Column(col =>
        {
            SectionTitle(col, "Successful Delivery — Line-Level Inventory Audit", NavyBlue);

            col.Item().PaddingBottom(4)
                .Text("Stock levels recorded immediately before and after each delivery was posted to SAP. " +
                      "Use this section for next-day inventory reconciliation.")
                .FontSize(7).Italic().FontColor(Colors.Grey.Darken2);

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(48);   // SO DocNum
                    c.ConstantColumn(60);   // ODLN DocNum
                    c.ConstantColumn(65);   // Item Code
                    c.RelativeColumn(2f);   // Description
                    c.ConstantColumn(40);   // Qty
                    c.ConstantColumn(42);   // Warehouse
                    c.ConstantColumn(52);   // On Hand Before
                    c.ConstantColumn(52);   // On Hand After
                    c.RelativeColumn(1.5f); // Bin(s) Used
                });

                table.Header(h =>
                {
                    Th(h.Cell(), "SO DocNum"); Th(h.Cell(), "ODLN DocNum"); Th(h.Cell(), "Item Code");
                    Th(h.Cell(), "Description"); Th(h.Cell(), "Qty");
                    Th(h.Cell(), "Whs"); Th(h.Cell(), "On Hand Before"); Th(h.Cell(), "On Hand After");
                    Th(h.Cell(), "Bin(s) Used");
                });

                bool alt = false;
                foreach (var log in successLogs)
                {
                    foreach (var line in log.Lines
                        .Where(l => l.Status == LineLogStatus.Ok)
                        .OrderBy(l => l.LineNum))
                    {
                        string bg = alt ? "#F0FBF0" : Colors.White; alt = !alt;
                        Td(table, $"SO-{log.SoDocNum}", bg);
                        Td(table, log.DeliveryDocNum.HasValue ? $"ODLN-{log.DeliveryDocNum}" : "—", bg);
                        Td(table, line.ItemCode, bg);
                        Td(table, line.ItemDescription, bg);
                        Td(table, line.OpenQuantity.ToString("N2"), bg);
                        Td(table, line.WarehouseCode, bg);
                        Td(table, line.OnHandBefore.ToString("N2"), bg);
                        Td(table, line.OnHandAfter.HasValue
                            ? line.OnHandAfter.Value.ToString("N2") : "—", bg);
                        Td(table, FormatBinAllocations(line.BinAllocationsJson), bg);
                    }
                }
            });
        });
    }

    private static string FormatBinAllocations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "—";
        try
        {
            var allocs = System.Text.Json.JsonSerializer.Deserialize<List<BinAllocEntry>>(json);
            if (allocs == null || allocs.Count == 0) return "—";
            return string.Join(", ", allocs.Select(a => $"{a.BinCode}: {a.Qty:N2}"));
        }
        catch
        {
            return json; // raw fallback if JSON is malformed
        }
    }

    private sealed record BinAllocEntry(string BinCode, decimal Qty);

    // ── Shared rendering helpers ──────────────────────────────────────────────

    private static void SectionTitle(ColumnDescriptor col, string title, string color)
    {
        col.Item()
            .BorderBottom(2).BorderColor(color)
            .PaddingBottom(3)
            .Text(title).FontSize(10).Bold().FontColor(color);
    }

    // Accepts the IContainer returned by h.Cell() — the header descriptor type is
    // internal to QuestPDF and not exposed as a named public type in 2024.3.7.
    private static void Th(IContainer cell, string text)
    {
        cell
            .Background(TableHdrBg)
            .BorderBottom(1).BorderColor(MedBlue)
            .Padding(4)
            .Text(text).FontSize(7.5f).Bold();
    }

    private static void Td(TableDescriptor table, string text, string bg, string? color = null)
    {
        var cell = table.Cell()
            .Background(bg)
            .BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
            .Padding(4);

        var span = cell.Text(text).FontSize(7.5f);
        if (color != null) span.FontColor(color);
    }
}
