using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Jobs;
using SapReplitAPI.Models.SoDelivery;
using SapReplitAPI.Services.SoDelivery;

namespace SapReplitAPI.Controllers;

/// <summary>
/// SO → Delivery nightly automation — manual trigger, run history, backlog visibility, PDF reports.
/// Business logic lives in SoDeliveryService / SoDeliveryDbService / SoDeliveryReportService.
/// </summary>
[ApiController]
[Route("api/so-delivery")]
public class SoDeliveryController : ControllerBase
{
    private readonly SoDeliveryService        _service;
    private readonly SoDeliveryDbService      _db;
    private readonly SoDeliveryReportService  _report;
    private readonly ILogger<SoDeliveryController> _log;

    // Reuse the same timezone instance as SoDeliveryJob — single source of truth.
    // Move to a shared BusinessTimeProvider in a future refactor (Step 9 note).
    private static readonly TimeZoneInfo _businessTz = SoDeliveryJob.BusinessTz;

    private const int MaxPageSize = 100;

    public SoDeliveryController(
        SoDeliveryService       service,
        SoDeliveryDbService     db,
        SoDeliveryReportService report,
        ILogger<SoDeliveryController> log)
    {
        _service = service;
        _db      = db;
        _report  = report;
        _log     = log;
    }

    // ── POST /api/so-delivery/run ─────────────────────────────────────────────

    /// <summary>
    /// Manually triggers an SO → Delivery run.
    ///
    /// Date rules (EAT = East Africa Time, UTC+3):
    ///   - Omit Date → defaults to today's EAT date.
    ///   - Date == today → processes today's SOs.
    ///   - Date &lt; today → 400 Bad Request. Historical dates would backdate the ODLN DocDate
    ///     in SAP. Use a dedicated backlog workflow (not yet implemented).
    ///   - Date &gt; today → 400 Bad Request. Future dates have no open SOs.
    ///
    /// Concurrency: run guard in SoDeliveryService (DB-level) + in-process SemaphoreSlim
    ///   prevent duplicate runs. A blocked guard throws InvalidOperationException → 409.
    ///
    /// Returns 200 even for ABORTED runs — the run record is valid and captured.
    /// Returns 409 when the guard blocks a duplicate or concurrent initialization.
    /// Returns 400 for invalid date inputs.
    /// Returns 500 for unexpected failures (full exception in server logs; not exposed to client).
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunAsync(
        [FromBody] SoDeliveryRunRequest request,
        CancellationToken ct)
    {
        DateTime businessToday = GetBusinessToday();
        DateTime processingDate;

        if (request.Date.HasValue)
        {
            processingDate = request.Date.Value.Date;

            if (processingDate < businessToday)
                return BadRequest(new
                {
                    Error = "Historical processing is not supported via this endpoint.",
                    Detail =
                        $"Requested date {processingDate:yyyy-MM-dd} is before today's business date " +
                        $"({businessToday:yyyy-MM-dd} EAT). Processing a historical date would create " +
                        "ODLNs with a backdated DocDate in SAP. " +
                        "Use a dedicated backlog workflow endpoint (not yet available in v1).",
                });

            if (processingDate > businessToday)
                return BadRequest(new
                {
                    Error = "Future processing dates are not allowed.",
                    Detail =
                        $"Requested date {processingDate:yyyy-MM-dd} is after today's business date " +
                        $"({businessToday:yyyy-MM-dd} EAT). No open Sales Orders can exist for a future date.",
                });
        }
        else
        {
            processingDate = businessToday;
        }

        try
        {
            var run = await _service.ProcessRunAsync(processingDate, request.Force, "Manual");
            return Ok(ToRunResponse(run));
        }
        catch (InvalidOperationException ex)
        {
            // Run guard (DB-level: COMPLETED/RUNNING exists) or in-process semaphore (concurrent init).
            _log.LogWarning("[SoDeliveryController] POST /run blocked — 409: {Reason}", ex.Message);
            return Conflict(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] POST /run unexpected failure");
            return StatusCode(500, new { Error = "Run failed. See server logs for details." });
        }
    }

    // ── GET /api/so-delivery/runs ─────────────────────────────────────────────

    /// <summary>
    /// Paginated run history, newest first. Query params: page (default 1), pageSize (default 20, max 100),
    /// date (yyyy-MM-dd), status (Running|Completed|Aborted). Returns summary rows only.
    /// </summary>
    [HttpGet("runs")]
    public async Task<IActionResult> GetRunsAsync(
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 20,
        [FromQuery] string? date     = null,
        [FromQuery] string? status   = null,
        CancellationToken   ct       = default)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        DateTime? dateFilter = null;
        if (!string.IsNullOrEmpty(date))
        {
            if (!DateTime.TryParse(date, out var parsedDate))
                return BadRequest(new { Error = $"Invalid date format: '{date}'. Use yyyy-MM-dd." });
            dateFilter = parsedDate;
        }

        try
        {
            var (items, total) = await _db.GetRunsPagedAsync(page, pageSize, dateFilter, status);
            var result = new PagedResult<SoDeliveryRunResponse>
            {
                Items      = items.Select(ToRunResponse).ToList(),
                Page       = page,
                PageSize   = pageSize,
                TotalCount = total,
                TotalPages = (int)Math.Ceiling((double)total / pageSize),
            };
            return Ok(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] GET /runs failed");
            return StatusCode(500, new { Error = "Failed to retrieve run history." });
        }
    }

    // ── GET /api/so-delivery/runs/latest ─────────────────────────────────────
    // NOTE: route "latest" must be declared BEFORE "{runId:int}" to avoid ambiguity.

    /// <summary>Returns the most recent run with SO-level logs (no line logs). 404 if no runs exist.</summary>
    [HttpGet("runs/latest")]
    public async Task<IActionResult> GetLatestRunAsync(CancellationToken ct)
    {
        try
        {
            var run = await _db.GetLatestRunAsync();
            if (run is null)
                return NotFound(new { Error = "No runs found." });

            // Load with logs+lines; map logs to summary DTO (lines are loaded but not mapped here).
            var detail = await _db.GetRunByIdAsync(run.Id);
            if (detail is null)
                return NotFound(new { Error = "Run not found after lookup." });

            var response = new SoDeliveryRunWithLogsResponse
            {
                RunId                  = detail.Id,
                ProcessingDate         = detail.ProcessingDate.ToString("yyyy-MM-dd"),
                Status                 = detail.Status,
                TriggeredBy            = detail.TriggeredBy,
                IsForced               = detail.IsForced,
                TotalOrders            = detail.TotalOrders,
                SuccessCount           = detail.SuccessCount,
                FailedCount            = detail.FailedCount,
                SkippedCount           = detail.SkippedCount,
                ExceptionCount         = detail.ExceptionCount,
                TotalDeliveriesCreated = detail.TotalDeliveriesCreated,
                StartTime              = detail.StartTime,
                EndTime                = detail.EndTime,
                ErrorMessage           = detail.ErrorMessage,
                PdfPath                = detail.PdfPath,
                Logs                   = detail.Logs.OrderBy(l => l.Id).Select(ToLogDto).ToList(),
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] GET /runs/latest failed");
            return StatusCode(500, new { Error = "Failed to retrieve latest run." });
        }
    }

    // ── GET /api/so-delivery/runs/{runId} ─────────────────────────────────────

    /// <summary>Full run detail: run summary + all SO logs + line logs. 404 if not found.</summary>
    [HttpGet("runs/{runId:int}")]
    public async Task<IActionResult> GetRunByIdAsync(int runId, CancellationToken ct)
    {
        try
        {
            var run = await _db.GetRunByIdAsync(runId);
            if (run is null)
                return NotFound(new { Error = $"Run {runId} not found." });

            var response = new SoDeliveryRunDetailResponse
            {
                RunId                  = run.Id,
                ProcessingDate         = run.ProcessingDate.ToString("yyyy-MM-dd"),
                Status                 = run.Status,
                TriggeredBy            = run.TriggeredBy,
                IsForced               = run.IsForced,
                TotalOrders            = run.TotalOrders,
                SuccessCount           = run.SuccessCount,
                FailedCount            = run.FailedCount,
                SkippedCount           = run.SkippedCount,
                ExceptionCount         = run.ExceptionCount,
                TotalDeliveriesCreated = run.TotalDeliveriesCreated,
                StartTime              = run.StartTime,
                EndTime                = run.EndTime,
                ErrorMessage           = run.ErrorMessage,
                PdfPath                = run.PdfPath,
                Logs                   = run.Logs.OrderBy(l => l.Id).Select(ToLogDetailDto).ToList(),
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] GET /runs/{RunId} failed", runId);
            return StatusCode(500, new { Error = $"Failed to retrieve run {runId}." });
        }
    }

    // ── GET /api/so-delivery/runs/{runId}/failed ──────────────────────────────

    /// <summary>
    /// FAILED and EXCEPTION logs for a run, with line details.
    /// Returns 404 if run not found; 200 [] if run exists but has no failures.
    /// </summary>
    [HttpGet("runs/{runId:int}/failed")]
    public async Task<IActionResult> GetFailedLogsAsync(int runId, CancellationToken ct)
    {
        try
        {
            var run = await _db.GetRunByIdAsync(runId);
            if (run is null)
                return NotFound(new { Error = $"Run {runId} not found." });

            // GetFailedLogsAsync loads FAILED+EXCEPTION with their Lines
            var logs = await _db.GetFailedLogsAsync(runId);
            return Ok(logs.Select(ToLogDetailDto).ToList());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] GET /runs/{RunId}/failed failed", runId);
            return StatusCode(500, new { Error = $"Failed to retrieve failure logs for run {runId}." });
        }
    }

    // ── GET /api/so-delivery/runs/{runId}/report ──────────────────────────────

    /// <summary>
    /// Downloads the PDF report for a run. Generates it on demand if not yet created or if
    /// the file was deleted (regeneration uses SQLite audit data only — no SAP access).
    /// Returns 404 if run not found; 500 with safe message if PDF generation fails.
    /// </summary>
    [HttpGet("runs/{runId:int}/report")]
    public async Task<IActionResult> GetReportAsync(int runId, CancellationToken ct)
    {
        // Verify run exists before attempting PDF generation
        var run = await _db.GetRunByIdAsync(runId);
        if (run is null)
            return NotFound(new { Error = $"Run {runId} not found." });

        string filename = run.IsForced
            ? $"SalesOrderDeliveryReport_{run.ProcessingDate:yyyy-MM-dd}_Run_{run.Id}_Forced.pdf"
            : $"SalesOrderDeliveryReport_{run.ProcessingDate:yyyy-MM-dd}_Run_{run.Id}.pdf";

        try
        {
            byte[] pdf = await _report.GetOrGeneratePdfAsync(runId);
            return File(pdf, "application/pdf", filename);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[SoDeliveryController] GET /runs/{RunId}/report — PDF generation failed", runId);
            return StatusCode(500, new
            {
                Error = $"Failed to generate PDF for run {runId}. See server logs for details.",
            });
        }
    }

    // ── POST /api/so-delivery/runs/{runId}/backfill-bins ─────────────────────

    /// <summary>
    /// Retroactively fetches bin allocation data from SAP OIBD for all success line logs in
    /// the given run that have no BinAllocationsJson. Updates SQLite in place so a subsequent
    /// GET /runs/{runId}/report returns a PDF with bin locations filled in.
    ///
    /// Safe to call multiple times — lines already populated are skipped.
    /// Returns 404 if run not found; 200 with a summary JSON otherwise.
    /// </summary>
    [HttpPost("runs/{runId:int}/backfill-bins")]
    public async Task<IActionResult> BackfillBinsAsync(int runId, CancellationToken ct)
    {
        var run = await _db.GetRunByIdAsync(runId);
        if (run is null)
            return NotFound(new { Error = $"Run {runId} not found." });

        try
        {
            var result = await _service.BackfillRunBinAllocationsAsync(runId);
            _log.LogInformation(
                "[SoDeliveryController] POST /runs/{RunId}/backfill-bins — {Summary}",
                runId, result.Summary);
            return Ok(new
            {
                result.RunId,
                result.TotalLines,
                result.UpdatedLines,
                result.NoDataLines,
                result.Summary,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[SoDeliveryController] POST /runs/{RunId}/backfill-bins failed", runId);
            return StatusCode(500, new { Error = $"Backfill failed for run {runId}. See server logs." });
        }
    }

    // ── GET /api/so-delivery/backlog ──────────────────────────────────────────

    /// <summary>
    /// Read-only view of Sales Orders from past dates that still have open lines (DocDate &lt; today EAT).
    /// Enriched with last processing attempt from local SQLite. No Delivery creation.
    /// </summary>
    [HttpGet("backlog")]
    public async Task<IActionResult> GetBacklogAsync(CancellationToken ct)
    {
        try
        {
            var businessToday = GetBusinessToday();
            var backlog = await _service.GetBacklogAsync(businessToday);
            return Ok(backlog);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] GET /backlog failed");
            return StatusCode(500, new { Error = "Failed to retrieve backlog. See server logs for details." });
        }
    }

    // ── Pilot / Controlled Test Endpoints ────────────────────────────────────
    // These endpoints are for controlled single-SO pilot testing only.
    // They do NOT affect the nightly 20:00 Quartz schedule.

    /// <summary>
    /// Pre-flight inspection for a specific SO: returns the SO header, all open lines,
    /// and current SAP stock for each line (per ItemCode + Warehouse). No changes made.
    /// Use before the pilot process call to capture the pre-test snapshot.
    /// </summary>
    [HttpGet("pilot/orders/{docEntry:int}/inspect")]
    public IActionResult PilotInspectOrderAsync(int docEntry)
    {
        var businessToday = GetBusinessToday();
        try
        {
            // Date-agnostic lookup — works for backlog SOs (DocDate < today) as well as today's SOs.
            var so = _service.GetSoByDocEntry(docEntry);
            if (so is null)
                return NotFound(new
                {
                    Error = $"DocEntry {docEntry} not found in SAP, or is closed/cancelled.",
                });

            var (lines, stock) = _service.InspectSoForPilot(docEntry);
            var requiredByGroup = lines
                .Where(l => l.OpenQty > 0 && !string.IsNullOrWhiteSpace(l.ItemCode))
                .GroupBy(l => (l.ItemCode.Trim().ToUpperInvariant(), l.WhsCode.Trim().ToUpperInvariant()))
                .ToDictionary(g => g.Key, g => g.Sum(l => l.OpenQty));

            var lineSnapshots = lines.Select(l =>
            {
                var key = (l.ItemCode.Trim().ToUpperInvariant(), l.WhsCode.Trim().ToUpperInvariant());
                decimal onHand  = stock.TryGetValue(key, out var h) ? h : 0m;
                decimal required = requiredByGroup.TryGetValue(key, out var r) ? r : l.OpenQty;
                return new
                {
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.WhsCode,
                    l.Quantity,
                    l.OpenQty,
                    OnHand       = onHand,
                    RequiredQty  = required,
                    StockOk      = onHand >= required,
                    StockStatus  = onHand >= required ? "SUFFICIENT" : $"INSUFFICIENT (short by {required - onHand:F4})",
                };
            }).ToList();

            bool allStockOk = lineSnapshots.All(l => l.StockOk);
            return Ok(new
            {
                DeliveryDate   = businessToday.ToString("yyyy-MM-dd"),
                SoDocDate      = so.DocDate.ToString("yyyy-MM-dd"),
                SoHeader       = so,
                OpenLines      = lineSnapshots,
                AllStockSufficient = allStockOk,
                SelectedSOs        = 1,
                NonTestSOsSelected = 0,
                Guidance           = allStockOk
                    ? $"Stock validated. Call POST /api/so-delivery/pilot/orders/{docEntry}/process with ExpectedCardCode={so.CardCode} to proceed."
                    : "STOCK INSUFFICIENT on one or more lines. No ODLN will be created until stock is resolved.",
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[SoDeliveryController] GET /pilot/orders/{DocEntry}/inspect failed", docEntry);
            return StatusCode(500, new { Error = "Pre-flight inspection failed. See server logs." });
        }
    }

    /// <summary>
    /// Lists today's open Sales Orders from SAP filtered by CardName (case-insensitive substring).
    /// Use this to identify the target SO's DocEntry and CardCode before calling the pilot process endpoint.
    /// Returns all today's open SOs if cardName is omitted.
    /// </summary>
    [HttpGet("pilot/customer-orders")]
    public IActionResult GetPilotCustomerOrders([FromQuery] string cardName = "")
    {
        var businessToday = GetBusinessToday();
        try
        {
            var orders = _service.GetTodayOpenSosByCustomer(businessToday, cardName);
            return Ok(new
            {
                BusinessDate = businessToday.ToString("yyyy-MM-dd"),
                Filter       = string.IsNullOrWhiteSpace(cardName) ? "(all)" : cardName,
                Count        = orders.Count,
                Orders       = orders,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SoDeliveryController] GET /pilot/customer-orders failed");
            return StatusCode(500, new { Error = "Failed to fetch open SOs from SAP. See server logs." });
        }
    }

    /// <summary>
    /// Controlled pilot: processes EXACTLY ONE Sales Order (identified by DocEntry) and creates
    /// a Delivery Note for it only. All other today's SOs are untouched.
    ///
    /// Safety guard: supply ExpectedCardCode (from GET /pilot/customer-orders) to abort the pilot
    /// if the resolved SO belongs to a different customer than expected.
    ///
    /// TriggeredBy = "ManualPilot", TotalOrders = 1.
    /// The same production audit path (SoDeliveryRun/Log/LineLog) is used.
    /// PDF can be generated afterward via GET /api/so-delivery/runs/{runId}/report.
    ///
    /// Does NOT affect the 20:00 nightly Quartz schedule.
    /// </summary>
    [HttpPost("pilot/orders/{docEntry:int}/process")]
    public async Task<IActionResult> PilotProcessOrderAsync(
        int                docEntry,
        [FromBody]         PilotOrderRequest request,
        CancellationToken  ct)
    {
        var businessToday = GetBusinessToday();

        // Pre-flight: date-agnostic SO lookup — supports both today's SOs and backlog SOs.
        OpenSoDto? targetSo;
        try
        {
            targetSo = _service.GetSoByDocEntry(docEntry);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[SoDeliveryController] Pilot pre-flight SAP lookup failed for DocEntry={DocEntry}", docEntry);
            return StatusCode(500, new { Error = "SAP lookup failed during pre-flight check. Cannot safely proceed." });
        }

        if (targetSo is null)
            return NotFound(new
            {
                Error    = $"DocEntry {docEntry} not found in SAP, or is closed/cancelled.",
                Guidance = "Run GET /api/so-delivery/pilot/orders/{docEntry}/inspect to verify the SO state.",
            });

        // Require ExpectedCardCode — mandatory for pilot safety.
        if (string.IsNullOrEmpty(request?.ExpectedCardCode))
            return BadRequest(new
            {
                Error    = "ExpectedCardCode is required for pilot safety. " +
                           "Obtain CardCode from GET /api/so-delivery/pilot/orders/{docEntry}/inspect, " +
                           "then POST with { \"ExpectedCardCode\": \"<CardCode>\" }.",
                SoCardCode = targetSo.CardCode,
                SoCardName = targetSo.CardName,
            });

        // Safety guard: abort if CardCode doesn't match.
        if (!targetSo.CardCode.Equals(request.ExpectedCardCode, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogError(
                "[SoDeliveryController] Pilot ABORTED — DocEntry={DocEntry} belongs to " +
                "CardCode={Actual} ({ActualName}), expected {Expected}. NO ODLN created.",
                docEntry, targetSo.CardCode, targetSo.CardName, request.ExpectedCardCode);
            return BadRequest(new
            {
                Error          = $"SAFETY CHECK FAILED: DocEntry {docEntry} belongs to " +
                                 $"{targetSo.CardCode} ({targetSo.CardName}), not {request.ExpectedCardCode}. " +
                                 "Pilot ABORTED. No delivery was created.",
                ActualCardCode = targetSo.CardCode,
                ActualCardName = targetSo.CardName,
            });
        }

        // Dedicated pilot path — does NOT touch GetOpenSosForDate or the nightly job.
        // deliveryDate = businessToday (ODLN.DocDate = today, not the SO's DocDate).
        try
        {
            var run = await _service.ProcessSingleOrderPilotAsync(
                docEntry,
                expectedCardCode: request.ExpectedCardCode,
                deliveryDate:     businessToday,
                force:            request.Force,
                triggeredBy:      "ManualPilot");

            return Ok(new
            {
                PilotDocEntry  = docEntry,
                PilotCardCode  = targetSo.CardCode,
                PilotCardName  = targetSo.CardName,
                SoDocDate      = targetSo.DocDate.ToString("yyyy-MM-dd"),
                DeliveryDate   = businessToday.ToString("yyyy-MM-dd"),
                Run            = ToRunResponse(run),
            });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(
                "[SoDeliveryController] Pilot /orders/{DocEntry}/process blocked — {Reason}", docEntry, ex.Message);
            return Conflict(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[SoDeliveryController] Pilot /orders/{DocEntry}/process unexpected failure", docEntry);
            return StatusCode(500, new { Error = "Pilot run failed. See server logs for details." });
        }
    }

    // ── Timezone helper ───────────────────────────────────────────────────────

    private static DateTime GetBusinessToday() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _businessTz).Date;

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static SoDeliveryRunResponse ToRunResponse(SoDeliveryRun run) => new()
    {
        RunId                  = run.Id,
        ProcessingDate         = run.ProcessingDate.ToString("yyyy-MM-dd"),
        Status                 = run.Status,
        TriggeredBy            = run.TriggeredBy,
        IsForced               = run.IsForced,
        TotalOrders            = run.TotalOrders,
        SuccessCount           = run.SuccessCount,
        FailedCount            = run.FailedCount,
        SkippedCount           = run.SkippedCount,
        ExceptionCount         = run.ExceptionCount,
        TotalDeliveriesCreated = run.TotalDeliveriesCreated,
        StartTime              = run.StartTime,
        EndTime                = run.EndTime,
        ErrorMessage           = run.ErrorMessage,
        PdfPath                = run.PdfPath,
    };

    private static SoDeliveryLogDto ToLogDto(SoDeliveryLog log) => new()
    {
        Id               = log.Id,
        SoDocEntry       = log.SoDocEntry,
        SoDocNum         = log.SoDocNum,
        CustomerCode     = log.CustomerCode,
        CustomerName     = log.CustomerName,
        Status           = log.Status,
        DeliveryDocEntry = log.DeliveryDocEntry,
        DeliveryDocNum   = log.DeliveryDocNum,
        ErrorMessage     = log.ErrorMessage,
        SapErrorCode     = log.SapErrorCode,
        SapErrorMessage  = log.SapErrorMessage,
        ProcessedAt      = log.ProcessedAt,
        DurationMs       = log.DurationMs,
    };

    private static SoDeliveryLogDetailDto ToLogDetailDto(SoDeliveryLog log) => new()
    {
        Id               = log.Id,
        SoDocEntry       = log.SoDocEntry,
        SoDocNum         = log.SoDocNum,
        CustomerCode     = log.CustomerCode,
        CustomerName     = log.CustomerName,
        Status           = log.Status,
        DeliveryDocEntry = log.DeliveryDocEntry,
        DeliveryDocNum   = log.DeliveryDocNum,
        ErrorMessage     = log.ErrorMessage,
        SapErrorCode     = log.SapErrorCode,
        SapErrorMessage  = log.SapErrorMessage,
        ProcessedAt      = log.ProcessedAt,
        DurationMs       = log.DurationMs,
        Lines            = log.Lines.OrderBy(l => l.LineNum).Select(ToLineLogDto).ToList(),
    };

    private static SoDeliveryLineLogDto ToLineLogDto(SoDeliveryLineLog l) => new()
    {
        LineNum         = l.LineNum,
        ItemCode        = l.ItemCode,
        ItemDescription = l.ItemDescription,
        Quantity        = l.Quantity,
        OpenQuantity    = l.OpenQuantity,
        WarehouseCode   = l.WarehouseCode,
        OnHandBefore    = l.OnHandBefore,
        OnHandAfter     = l.OnHandAfter,
        Status          = l.Status,
        ErrorMessage    = l.ErrorMessage,
    };
}
