using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.SoDelivery;

namespace SapReplitAPI.Services.SoDelivery;

public class SoDeliveryService
{
    private readonly SapService          _sap;
    private readonly SoDeliveryDbService _db;
    private readonly ILogger<SoDeliveryService> _log;

    // Static — survives DI scope boundaries (scoped or singleton).
    // Guards concurrent ProcessRunAsync callers (Quartz + HTTP, or two HTTP requests)
    // against the TOCTOU race between the DB guard check and CreateRunAsync.
    private static readonly SemaphoreSlim _runLock = new(1, 1);

    public SoDeliveryService(
        SapService sap,
        SoDeliveryDbService db,
        ILogger<SoDeliveryService> log)
    {
        _sap = sap;
        _db  = db;
        _log = log;
    }

    // ── Pilot / Lookup Methods (read-only SAP queries) ────────────────────────

    /// <summary>
    /// Returns today's open SOs filtered by a case-insensitive CardName substring.
    /// If cardNameFilter is empty, returns all open SOs. Pilot/lookup use only.
    /// SAP COM call — synchronous, same thread safety constraints as the nightly run.
    /// </summary>
    public List<OpenSoDto> GetTodayOpenSosByCustomer(DateTime processingDate, string cardNameFilter)
    {
        var allSos = _sap.GetOpenSosForDate(processingDate);
        if (string.IsNullOrWhiteSpace(cardNameFilter)) return allSos;
        return allSos
            .Where(so => so.CardName.Contains(cardNameFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns the specific open SO for today by DocEntry, or null if not found.
    /// Used by pilot endpoint for pre-flight customer validation before processing.
    /// </summary>
    public OpenSoDto? GetTodayOpenSoByDocEntry(DateTime processingDate, int docEntry)
    {
        var allSos = _sap.GetOpenSosForDate(processingDate);
        return allSos.FirstOrDefault(so => so.DocEntry == docEntry);
    }

    /// <summary>
    /// Pre-flight inspection for a pilot run: returns open lines and current SAP stock.
    /// No changes made to SAP or SQLite. Use to capture the pre-test snapshot before processing.
    /// </summary>
    public (List<OpenSoLineDto> Lines, Dictionary<(string, string), decimal> Stock) InspectSoForPilot(int docEntry)
    {
        var lines = _sap.GetOpenSoLines(docEntry);
        var stock = lines.Count > 0 ? _sap.GetStockForSoLines(lines) : new Dictionary<(string, string), decimal>();
        return (lines, stock);
    }

    /// <summary>
    /// Returns an open SO by DocEntry regardless of DocDate. For backlog pilot use only.
    /// Returns null if not found, closed, or cancelled. Does not query other SOs.
    /// </summary>
    public OpenSoDto? GetSoByDocEntry(int docEntry)
        => _sap.GetOpenSoByDocEntry(docEntry);

    // ── Backlog Pilot — Dedicated Single-Order Path ───────────────────────────
    // Completely separate from ProcessRunAsync / ProcessRunCoreAsync.
    // The nightly 20:00 job path is NOT modified by anything below.

    /// <summary>
    /// Processes exactly one Sales Order (by DocEntry) without requiring DocDate == deliveryDate.
    /// For backlog pilot use only. Uses the same per-SO logic, audit, and semaphore as the
    /// nightly run — but fetches the SO date-agnostically via GetOpenSoByDocEntry.
    ///
    /// deliveryDate: the business date to stamp on the ODLN (must be today — do NOT pass SO.DocDate).
    /// expectedCardCode: safety guard — pilot is aborted if the resolved SO has a different CardCode.
    /// </summary>
    public async Task<SoDeliveryRun> ProcessSingleOrderPilotAsync(
        int      docEntry,
        string   expectedCardCode,
        DateTime deliveryDate,
        string   triggeredBy = "ManualPilot")
    {
        deliveryDate = deliveryDate.Date;

        if (!await _runLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException(
                "A run is currently being processed in this server instance. " +
                "Only one run can execute at a time. Wait for it to complete, then retry.");

        try
        {
            return await ProcessSingleOrderPilotCoreAsync(docEntry, expectedCardCode, deliveryDate, triggeredBy);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<SoDeliveryRun> ProcessSingleOrderPilotCoreAsync(
        int      docEntry,
        string   expectedCardCode,
        DateTime deliveryDate,
        string   triggeredBy)
    {
        // ── Run Guard (for deliveryDate, not SO DocDate) ──────────────────────
        var (hasCompleted, hasRunning) = await _db.GetRunGuardStatusAsync(deliveryDate);
        if (hasCompleted)
        {
            _log.LogInformation(
                "[SoDelivery Pilot] Run guard — COMPLETED run exists for {Date}. Use force or a different date.",
                deliveryDate.ToString("yyyy-MM-dd"));
            throw new InvalidOperationException(
                $"A completed run already exists for {deliveryDate:yyyy-MM-dd}. " +
                "Pilot cannot run against a date that already has a completed run.");
        }
        if (hasRunning)
        {
            _log.LogWarning(
                "[SoDelivery Pilot] Run guard — RUNNING run exists for {Date}.",
                deliveryDate.ToString("yyyy-MM-dd"));
            throw new InvalidOperationException(
                $"A run is already in progress for {deliveryDate:yyyy-MM-dd}. Pass force=true if it is stuck.");
        }

        // ── Create Run Record (TotalOrders = 1) ───────────────────────────────
        var run = await _db.CreateRunAsync(deliveryDate, triggeredBy, isForced: false);
        run.TotalOrders = 1;
        await SafeUpdateRunAsync(run);

        _log.LogInformation(
            "▶️ [SoDelivery Pilot] Run {RunId} started — DocEntry={DocEntry} DeliveryDate={Date} TriggeredBy={By}",
            run.Id, docEntry, deliveryDate.ToString("yyyy-MM-dd"), triggeredBy);

        // ── Fetch SO from SAP (date-agnostic) ────────────────────────────────
        OpenSoDto? so;
        try
        {
            so = _sap.GetOpenSoByDocEntry(docEntry);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "❌ [SoDelivery Pilot] Run {RunId} — GetOpenSoByDocEntry({DocEntry}) failed, marking ABORTED",
                run.Id, docEntry);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage = $"SAP GetOpenSoByDocEntry failed: {ex.Message}";
            await SafeUpdateRunAsync(run);
            throw;
        }

        // ── Safety Gate 1: SO must exist and be open ──────────────────────────
        if (so is null)
        {
            string reason = $"DocEntry {docEntry} not found in SAP, or is closed/cancelled. Pilot ABORTED — no ODLN created.";
            _log.LogError("❌ [SoDelivery Pilot] Run {RunId} — {Reason}", run.Id, reason);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage = reason;
            await SafeUpdateRunAsync(run);
            throw new InvalidOperationException(reason);
        }

        // ── Safety Gate 2: validate CardCode ─────────────────────────────────
        if (!so.CardCode.Equals(expectedCardCode, StringComparison.OrdinalIgnoreCase))
        {
            string reason =
                $"SAFETY ABORT: DocEntry {docEntry} belongs to CardCode={so.CardCode} ({so.CardName}), " +
                $"expected CardCode={expectedCardCode}. No ODLN created.";
            _log.LogError("❌ [SoDelivery Pilot] Run {RunId} — {Reason}", run.Id, reason);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage = reason;
            await SafeUpdateRunAsync(run);
            throw new InvalidOperationException(reason);
        }

        _log.LogInformation(
            "[SoDelivery Pilot] Run {RunId} — SO validated: DocEntry={DocEntry} CardCode={CardCode} " +
            "CardName={CardName} SoDocDate={SoDate} DeliveryDate={DlvDate}",
            run.Id, so.DocEntry, so.CardCode, so.CardName,
            so.DocDate.ToString("yyyy-MM-dd"), deliveryDate.ToString("yyyy-MM-dd"));

        // ── Process This Single SO (reuses existing per-SO logic) ─────────────
        // deliveryDate (businessToday) is passed as processingDate so CreateDeliveryFromSo
        // stamps ODLN.DocDate = deliveryDate, NOT so.DocDate.
        try
        {
            await ProcessSingleSoAsync(run, so, deliveryDate);
        }
        catch (CriticalAuditFailureException caf)
        {
            _log.LogCritical(
                "💥 [SoDelivery Pilot] Run {RunId} — CRITICAL_AUDIT_FAILURE for SO {SoEntry}: " +
                "ODLN DocEntry={DlvEntry} DocNum={DlvNum} created in SAP. Local audit failed: {Error}. " +
                "Run ABORTED. Manual reconciliation required.",
                run.Id, caf.SoDocEntry, caf.DlvDocEntry, caf.DlvDocNum, caf.Message);

            await DerivePartialCountersAsync(run);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage =
                $"CRITICAL_AUDIT_FAILURE: ODLN created in SAP (DocEntry={caf.DlvDocEntry}, DocNum={caf.DlvDocNum}) " +
                $"for SO DocEntry={caf.SoDocEntry}, but local audit persistence failed. Manual reconciliation required.";
            await SafeUpdateRunAsync(run);
            return run;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "❌ [SoDelivery Pilot] Run {RunId} — orchestration failure: {Error}", run.Id, ex.Message);
            await DerivePartialCountersAsync(run);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage = $"Orchestration failure: {ex.Message}";
            await SafeUpdateRunAsync(run);
            throw;
        }

        // ── Finalize ──────────────────────────────────────────────────────────
        var logs = await _db.GetLogsForRunAsync(run.Id);
        run.SuccessCount           = logs.Count(l => l.Status == SoLogStatus.Success);
        run.FailedCount            = logs.Count(l => l.Status == SoLogStatus.Failed);
        run.SkippedCount           = logs.Count(l =>
                                         l.Status == SoLogStatus.Skipped ||
                                         l.Status == SoLogStatus.SkippedAlreadyDone);
        run.ExceptionCount         = logs.Count(l => l.Status == SoLogStatus.Exception);
        run.TotalDeliveriesCreated = logs.Count(l => l.DeliveryDocEntry.HasValue);
        run.Status  = RunStatus.Completed;
        run.EndTime = DateTime.UtcNow;
        await _db.UpdateRunAsync(run);

        _log.LogInformation(
            "✅ [SoDelivery Pilot] Run {RunId} COMPLETED — DocEntry={DocEntry} Success={Ok} " +
            "Failed={Fail} Exception={Ex} Deliveries={Dlv} Duration={Ms}ms",
            run.Id, docEntry, run.SuccessCount, run.FailedCount,
            run.ExceptionCount, run.TotalDeliveriesCreated,
            (long)(run.EndTime!.Value - run.StartTime).TotalMilliseconds);

        return run;
    }

    // ── Main Entry Point ──────────────────────────────────────────────────────

    /// <summary>
    /// Processes open SOs for the given date. If docEntryFilter is non-null, only those
    /// DocEntries are processed and TotalOrders reflects the filtered count (not the full SAP queue).
    /// Use docEntryFilter for controlled pilot runs; leave null for production nightly runs.
    /// </summary>
    public async Task<SoDeliveryRun> ProcessRunAsync(
        DateTime           processingDate,
        bool               force,
        string             triggeredBy,
        IReadOnlySet<int>? docEntryFilter = null)
    {
        processingDate = processingDate.Date;

        // Non-blocking acquire — fail immediately if another caller is inside ProcessRunAsync.
        // Covers the TOCTOU window between the DB guard check and CreateRunAsync.
        // After CreateRunAsync commits status=RUNNING, the DB guard handles all later callers.
        if (!await _runLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException(
                "A run is currently being processed in this server instance. " +
                "Only one run can execute at a time. " +
                "Wait for the active run to complete, then retry.");

        try
        {
            return await ProcessRunCoreAsync(processingDate, force, triggeredBy, docEntryFilter);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<SoDeliveryRun> ProcessRunCoreAsync(
        DateTime           processingDate,
        bool               force,
        string             triggeredBy,
        IReadOnlySet<int>? docEntryFilter = null)
    {
        // ── Run Guard ────────────────────────────────────────────────────────
        // Check ALL runs for this date (not just the latest). A COMPLETED run from earlier
        // in the day must not be unblocked by a later ABORTED re-run being the "latest".
        //
        // Rules:
        //   force=false + ANY COMPLETED run exists → BLOCK
        //   force=false + ANY RUNNING  run exists  → BLOCK (concurrent or orphaned)
        //   force=false + only ABORTED runs exist  → ALLOW (retry after investigation)
        //   force=true                             → bypass guard (per-SO safeguards still active)
        var (hasCompleted, hasRunning) = await _db.GetRunGuardStatusAsync(processingDate);

        if (!force)
        {
            if (hasCompleted)
            {
                _log.LogInformation(
                    "[SoDelivery] Run guard — a COMPLETED run exists for {Date}. Use force=true to override.",
                    processingDate.ToString("yyyy-MM-dd"));
                throw new InvalidOperationException(
                    $"A completed run already exists for {processingDate:yyyy-MM-dd}. " +
                    "Pass force=true to override.");
            }
            if (hasRunning)
            {
                _log.LogWarning(
                    "[SoDelivery] Run guard — a RUNNING run exists for {Date}. " +
                    "Use force=true if the previous run is stuck.",
                    processingDate.ToString("yyyy-MM-dd"));
                throw new InvalidOperationException(
                    $"A run is already in progress for {processingDate:yyyy-MM-dd}. " +
                    "Pass force=true if it is stuck.");
            }
            // Only ABORTED (or no) runs exist for this date → allow retry
        }

        // ── Create Run Record BEFORE Any SAP Contact ─────────────────────────
        // Even if all subsequent steps fail we have an audit trail.
        var run = await _db.CreateRunAsync(processingDate, triggeredBy, isForced: force);
        _log.LogInformation(
            "▶️ [SoDelivery] Run {RunId} started — ProcessingDate={Date} TriggeredBy={By} IsForced={F}",
            run.Id, processingDate.ToString("yyyy-MM-dd"), triggeredBy, force);

        // ── Fetch Today's Open SOs from SAP ──────────────────────────────────
        List<OpenSoDto> openSos;
        try
        {
            openSos = _sap.GetOpenSosForDate(processingDate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "❌ [SoDelivery] Run {RunId} — SAP GetOpenSosForDate failed, marking ABORTED: {Error}",
                run.Id, ex.Message);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage = $"SAP GetOpenSosForDate failed: {ex.Message}";
            await SafeUpdateRunAsync(run);
            throw;
        }

        // Pilot filter: restrict processing to specific DocEntries only.
        // Applied BEFORE counting so TotalOrders reflects the filtered queue (e.g. 1 for a pilot run).
        if (docEntryFilter is { Count: > 0 })
        {
            int totalFromSap = openSos.Count;
            openSos = openSos.Where(so => docEntryFilter.Contains(so.DocEntry)).ToList();
            _log.LogInformation(
                "[SoDelivery] Run {RunId} — docEntry filter active: {FilterCount} target(s), " +
                "{MatchCount} matched from {AllCount} total open SOs in SAP.",
                run.Id, docEntryFilter.Count, openSos.Count, totalFromSap);
        }

        // Persist the queue count immediately so ABORTED runs also show TotalOrders.
        // Do NOT overwrite TotalOrders at the end with logs.Count — that would hide partial-abort.
        run.TotalOrders = openSos.Count;
        await SafeUpdateRunAsync(run);

        _log.LogInformation(
            "[SoDelivery] Run {RunId} — {Count} open SO(s) found for {Date}",
            run.Id, openSos.Count, processingDate.ToString("yyyy-MM-dd"));

        // ── Sequential Processing — One SO at a Time ─────────────────────────
        // No Parallel.ForEach / Task.WhenAll — SAP DI-API is not thread-safe and
        // sequential execution lets each SO observe the stock state left by the previous.
        try
        {
            foreach (var so in openSos)
                await ProcessSingleSoAsync(run, so, processingDate);
        }
        catch (CriticalAuditFailureException caf)
        {
            // ODLN was committed to SAP but local audit persistence failed.
            // After this, the local DB and SAP are inconsistent — we cannot trust
            // counters or reports for the remainder of the run.
            // Mark ABORTED immediately and stop processing further SOs.
            _log.LogCritical(
                "💥 [SoDelivery] Run {RunId} — CRITICAL_AUDIT_FAILURE for SO {SoEntry}: " +
                "ODLN DocEntry={DlvEntry} DocNum={DlvNum} created in SAP. " +
                "Local audit persistence failed: {Error}. " +
                "Run ABORTED. Do NOT retry without manual SAP/DB reconciliation.",
                run.Id, caf.SoDocEntry, caf.DlvDocEntry, caf.DlvDocNum, caf.Message);

            // Derive partial counters from whatever was persisted before the failure
            await DerivePartialCountersAsync(run);

            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage =
                $"CRITICAL_AUDIT_FAILURE: ODLN was successfully created in SAP " +
                $"(DocEntry={caf.DlvDocEntry}, DocNum={caf.DlvDocNum}) for SO DocEntry={caf.SoDocEntry} " +
                $"DocNum={caf.SoDocNum}, but local audit persistence failed. " +
                (caf.SoLogId.HasValue
                    ? $"SUCCESS log saved (Id={caf.SoLogId}), line logs missing."
                    : "SUCCESS log NOT saved — manual reconciliation essential.") +
                " Do NOT re-run without operator investigation.";
            await SafeUpdateRunAsync(run);

            // Return ABORTED run — do NOT rethrow. The ODLN exists in SAP; it is correct.
            // Rethrowing would hide the saved run record from the controller/job.
            return run;
        }
        catch (Exception ex)
        {
            // Unexpected exception escaping per-SO isolation — orchestration-level bug.
            _log.LogError(ex,
                "❌ [SoDelivery] Run {RunId} — orchestration failure: {Error}",
                run.Id, ex.Message);
            await DerivePartialCountersAsync(run);
            run.Status       = RunStatus.Aborted;
            run.EndTime      = DateTime.UtcNow;
            run.ErrorMessage = $"Orchestration failure: {ex.Message}";
            await SafeUpdateRunAsync(run);
            throw;
        }

        // ── Finalize: Derive Counters from Persisted Logs ────────────────────
        // Counts come from what was actually saved — cannot drift from branch logic.
        // TotalOrders is NOT overwritten here — it was set to openSos.Count (actual SAP queue)
        // before processing began, so ABORTED runs don't lose that information.
        var logs = await _db.GetLogsForRunAsync(run.Id);
        run.SuccessCount           = logs.Count(l => l.Status == SoLogStatus.Success);
        run.FailedCount            = logs.Count(l => l.Status == SoLogStatus.Failed);
        run.SkippedCount           = logs.Count(l =>
                                         l.Status == SoLogStatus.Skipped ||
                                         l.Status == SoLogStatus.SkippedAlreadyDone);
        run.ExceptionCount         = logs.Count(l => l.Status == SoLogStatus.Exception);
        run.TotalDeliveriesCreated = logs.Count(l => l.DeliveryDocEntry.HasValue);

        // COMPLETED = job finished its queue, even if individual SOs failed/exceptioned.
        // ABORTED = orchestration could not continue (SAP failure, critical audit failure).
        run.Status  = RunStatus.Completed;
        run.EndTime = DateTime.UtcNow;
        await _db.UpdateRunAsync(run);

        _log.LogInformation(
            "✅ [SoDelivery] Run {RunId} COMPLETED — Total={Total} Success={Ok} Failed={Fail} " +
            "Skipped={Skip} Exception={Ex} Deliveries={Dlv} Duration={Ms}ms",
            run.Id, run.TotalOrders, run.SuccessCount, run.FailedCount,
            run.SkippedCount, run.ExceptionCount, run.TotalDeliveriesCreated,
            (long)(run.EndTime!.Value - run.StartTime).TotalMilliseconds);

        return run;
    }

    // ── Backlog Visibility ────────────────────────────────────────────────────

    public async Task<List<BacklogSoDto>> GetBacklogAsync(DateTime processingDate)
    {
        processingDate = processingDate.Date;

        List<BacklogSoDto> backlog;
        try
        {
            backlog = _sap.GetBacklogSos(processingDate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [SoDelivery] GetBacklog — SAP fetch failed: {Error}", ex.Message);
            throw;
        }

        if (backlog.Count == 0) return backlog;

        foreach (var item in backlog)
            item.DaysOpen = (int)(processingDate - item.DocDate.Date).TotalDays;

        // Batch-enrich from local SQLite — no N+1
        var enrichments = await _db.GetBacklogEnrichmentsAsync(backlog.Select(b => b.DocEntry));
        foreach (var item in backlog)
        {
            if (!enrichments.TryGetValue(item.DocEntry, out var e)) continue;
            item.LastAttemptDate   = e.LastAttemptDate;
            item.LastAttemptStatus = e.LastAttemptStatus;
            item.LastFailureReason = e.LastAttemptStatus == SoLogStatus.Success
                ? null : e.LastFailureReason;
        }

        return backlog;
    }

    // ── Per-SO Orchestration ──────────────────────────────────────────────────

    private async Task ProcessSingleSoAsync(
        SoDeliveryRun run, OpenSoDto so, DateTime processingDate)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation(
            "📋 [SoDelivery] Run {RunId} — SO {DocEntry}/{DocNum} ({CardCode}) starting",
            run.Id, so.DocEntry, so.DocNum, so.CardCode);

        SoDeliveryLog? soLog          = null;
        bool           deliveryCreated = false;
        int?           dlvDocEntry     = null;
        int?           dlvDocNum       = null;

        try
        {
            // ── A: Fetch Current Open Lines from SAP ─────────────────────────
            List<OpenSoLineDto> openLines;
            try
            {
                openLines = _sap.GetOpenSoLines(so.DocEntry);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "⚠️ [SoDelivery] SO {DocEntry} — GetOpenSoLines failed: {Error}",
                    so.DocEntry, ex.Message);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                    errorMessage: $"SAP GetOpenSoLines failed: {ex.Message}"));
                return;
            }

            // ── B: No Open Lines → SKIPPED ───────────────────────────────────
            if (openLines.Count == 0)
            {
                _log.LogDebug("[SoDelivery] SO {DocEntry} — no open lines returned from SAP.", so.DocEntry);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Skipped, sw,
                    errorMessage: "SAP returned no open lines (LineStatus='O' AND OpenQty>0)."));
                return;
            }

            // ── C: Structural Validity Guard (All-or-Nothing) ────────────────
            // Any line with blank ItemCode/WhsCode or zero OpenQty would be silently dropped
            // by Step 3 (CreateDeliveryFromSo), creating a partial delivery. We catch this here.
            var invalidLines = openLines
                .Where(l => string.IsNullOrWhiteSpace(l.ItemCode) ||
                             string.IsNullOrWhiteSpace(l.WhsCode)  ||
                             l.OpenQty <= 0)
                .ToList();

            if (invalidLines.Count > 0)
            {
                var details = string.Join(", ", invalidLines.Select(l =>
                    $"LineNum={l.LineNum} ItemCode='{l.ItemCode}' WhsCode='{l.WhsCode}' OpenQty={l.OpenQty:F4}"));
                string reason = $"SO has {invalidLines.Count} structurally invalid line(s): {details}. " +
                    "Cannot create a safe Delivery without violating all-or-nothing.";
                _log.LogWarning("⚠️ [SoDelivery] SO {DocEntry} — structural validation failed: {Reason}",
                    so.DocEntry, reason);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                    errorMessage: reason));
                return;
            }

            // ── D: Local SUCCESS History (checked AFTER confirming SAP state) ─
            // SAP is the final source of truth. Local history is checked only after SAP has
            // confirmed open lines still exist. A mismatch requires manual investigation.
            bool hasLocalSuccess = await _db.HasSuccessLogAsync(so.DocEntry);
            if (hasLocalSuccess)
            {
                string reason =
                    "LOCAL_SUCCESS_BUT_SAP_STILL_OPEN — a previous run logged SUCCESS for this SO " +
                    $"but SAP still reports {openLines.Count} open line(s) (OpenQty>0). " +
                    "Manual SAP/DB reconciliation required before this SO can be safely retried.";
                _log.LogWarning(
                    "⚠️ [SoDelivery] SO {DocEntry} — inconsistency: {Reason}", so.DocEntry, reason);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                    errorMessage: reason));
                return;
            }

            // ── E: Initial Stock Validation ──────────────────────────────────
            Dictionary<(string, string), decimal> stockBefore;
            try
            {
                stockBefore = _sap.GetStockForSoLines(openLines);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "⚠️ [SoDelivery] SO {DocEntry} — GetStockForSoLines failed: {Error}",
                    so.DocEntry, ex.Message);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                    errorMessage: $"GetStockForSoLines failed: {ex.Message}"));
                return;
            }

            var requiredByGroup    = AggregateRequired(openLines);
            var insufficientGroups = FindInsufficientGroups(requiredByGroup, stockBefore);

            if (insufficientGroups.Count > 0)
            {
                string reason = string.Join("; ", insufficientGroups.Select(g =>
                    $"{g.ItemCode}/{g.WhsCode}: required={g.Required:F4} onHand={g.OnHand:F4} shortage={g.Shortage:F4}"));
                _log.LogWarning(
                    "⚠️ [SoDelivery] SO {DocEntry} — initial stock FAILED: {Reason}", so.DocEntry, reason);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Failed, sw,
                    errorMessage: reason));
                await _db.AddLineLogsAsync(BuildLineLogs(soLog.Id, openLines, stockBefore, insufficientGroups));
                return;
            }

            // ── F: Create Delivery via SAP ───────────────────────────────────
            // NOTE — batch/serial/bin-managed items: CreateDeliveryFromSo does not supply
            // batch numbers, serial numbers, or bin allocations. delivery.Add() will fail for
            // such items and the result will be SapError with SAP's error code/message. The SO
            // is logged as EXCEPTION. See Step 5 verification report for the planned pre-check
            // path (Step 5b, not yet in scope).
            //
            // Step 3 performs a final live OITW revalidation immediately before delivery.Add()
            // to minimise the race window vs. the initial check above.
            CreateDeliveryResult result;
            try
            {
                result = _sap.CreateDeliveryFromSo(so, openLines, processingDate);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "❌ [SoDelivery] SO {DocEntry} — unexpected exception in CreateDeliveryFromSo: {Error}",
                    so.DocEntry, ex.Message);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                    errorMessage: $"CreateDeliveryFromSo threw unexpectedly: {ex.Message}"));
                return;
            }

            // ── G: Handle Delivery Result ────────────────────────────────────
            if (result.Success)
            {
                deliveryCreated = true;
                dlvDocEntry     = result.DeliveryDocEntry;
                dlvDocNum       = result.DeliveryDocNum;

                _log.LogInformation(
                    "✅ [SoDelivery] SO {DocEntry}/{DocNum} ({CardCode}) → ODLN DocEntry={DlvEntry} DocNum={DlvNum}",
                    so.DocEntry, so.DocNum, so.CardCode, dlvDocEntry, dlvDocNum);

                // ⚠️  CRITICAL POINT: if CreateSoLogAsync or AddLineLogsAsync throws from here
                // forward, deliveryCreated=true — the outer catch escalates to CriticalAuditFailure,
                // which aborts the entire run. Do NOT swallow exceptions after this line.
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Success, sw,
                    deliveryDocEntry: dlvDocEntry, deliveryDocNum: dlvDocNum));

                var stockAfter = FetchStockAfterDelivery(openLines);
                // If AddLineLogsAsync throws: soLog is saved (SUCCESS log preserved), but line
                // logs are missing → still treated as CriticalAuditFailure → run ABORTED.
                await _db.AddLineLogsAsync(BuildLineLogs(
                    soLog.Id, openLines, stockBefore,
                    insufficientGroups: null,
                    stockAfter: stockAfter,
                    binAllocsByLineNum: result.LineBinAllocations));
            }
            else if (result.FailureType == DeliveryFailureType.InsufficientStock)
            {
                // Race-window stock change detected by Step 3's final revalidation.
                // Map to FAILED (not EXCEPTION) — cause is stock, not a system error.
                string reason = $"Stock changed before Delivery creation (race window): {result.SapErrorMessage}";
                _log.LogWarning("⚠️ [SoDelivery] SO {DocEntry} — FAILED (race window): {Msg}",
                    so.DocEntry, result.SapErrorMessage);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Failed, sw,
                    errorMessage: reason));
                await _db.AddLineLogsAsync(BuildLineLogs(
                    soLog.Id, openLines, stockBefore, insufficientGroups: null,
                    raceWindowFailReason: reason));
            }
            else if (result.FailureType == DeliveryFailureType.SapError)
            {
                // SAP DI-API rejected delivery.Add() — includes managed-item errors, locked docs, etc.
                _log.LogError(
                    "❌ [SoDelivery] SO {DocEntry} — SAP DI-API error [{Code}]: {Msg}",
                    so.DocEntry, result.SapErrorCode, result.SapErrorMessage);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                    errorMessage:    result.SapErrorMessage,
                    sapErrorCode:    result.SapErrorCode,
                    sapErrorMessage: result.SapErrorMessage));
                await _db.AddLineLogsAsync(BuildLineLogs(
                    soLog.Id, openLines, stockBefore, insufficientGroups: null));
            }
            else if (result.FailureType == DeliveryFailureType.NoOpenLines)
            {
                _log.LogDebug("[SoDelivery] SO {DocEntry} — NoOpenLines from Step 3 defensive path.", so.DocEntry);
                soLog = await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Skipped, sw,
                    errorMessage: result.SapErrorMessage ?? "No deliverable lines."));
            }
        }
        catch (Exception ex) when (deliveryCreated)
        {
            // ODLN was committed to SAP, but a subsequent write failed:
            //   - soLog == null: CreateSoLogAsync failed → ODLN has NO local record
            //   - soLog != null: SUCCESS log saved, but AddLineLogsAsync failed → partial record
            //
            // Either way: the local DB and SAP are now inconsistent.
            // Throw CriticalAuditFailureException so ProcessRunAsync can ABORT the entire run.
            // DO NOT continue processing further SOs after this.
            string detail = soLog == null
                ? $"SUCCESS audit log FAILED to save — ODLN has NO local record. Reconciliation essential."
                : $"SUCCESS log saved (SoDeliveryLog.Id={soLog.Id}), but line audit logs failed.";

            _log.LogCritical(
                "💥 [SoDelivery] SO {DocEntry} — ODLN CREATED IN SAP (DocEntry={DlvEntry}, DocNum={DlvNum}): " +
                "{Detail} Error={Error}",
                so.DocEntry, dlvDocEntry, dlvDocNum, detail, ex.Message);

            throw new CriticalAuditFailureException(
                so.DocEntry, so.DocNum, soLog?.Id, dlvDocEntry, dlvDocNum,
                $"ODLN created in SAP but audit persistence failed ({detail}): {ex.Message}",
                innerException: ex);
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx) && !deliveryCreated)
        {
            // (RunId, SoDocEntry) unique constraint fired BEFORE any delivery was created.
            // This SO entered the queue twice — orchestration bug. Safe to continue.
            _log.LogError(dbEx,
                "⚠️ [SoDelivery] SO {DocEntry} — DUPLICATE LOG CONFLICT in Run {RunId}. " +
                "SO appeared twice in the processing queue. No Delivery was created.",
                so.DocEntry, run.Id);
        }
        catch (Exception ex)
        {
            // Generic per-SO isolation: one bad order must not abort the entire nightly run.
            //
            // SAP connection-loss note: subsequent SOs will also fail with COMException here,
            // each producing an individual EXCEPTION log. There is no automatic abort on
            // consecutive SAP failures in this version — all SOs receive individual audit records.
            _log.LogError(ex,
                "❌ [SoDelivery] SO {DocEntry} — unexpected exception: {Error}", so.DocEntry, ex.Message);
            if (soLog == null)
            {
                try
                {
                    await _db.CreateSoLogAsync(MakeSoLog(run.Id, so, SoLogStatus.Exception, sw,
                        errorMessage: $"Unexpected exception: {ex.Message}"));
                }
                catch (Exception logEx)
                {
                    _log.LogCritical(logEx,
                        "💥 [SoDelivery] SO {DocEntry} — ALSO FAILED to write EXCEPTION audit log: {Error}",
                        so.DocEntry, logEx.Message);
                }
            }
        }
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    private Dictionary<(string, string), decimal>? FetchStockAfterDelivery(List<OpenSoLineDto> lines)
    {
        try   { return _sap.GetStockForSoLines(lines); }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "⚠️ [SoDelivery] Post-delivery stock read failed — OnHandAfter will be null: {Error}",
                ex.Message);
            return null;
        }
    }

    private async Task SafeUpdateRunAsync(SoDeliveryRun run)
    {
        try { await _db.UpdateRunAsync(run); }
        catch (Exception ex)
        {
            _log.LogCritical(ex,
                "💥 [SoDelivery] Run {RunId} — failed to persist run state (Status={Status}): {Error}",
                run.Id, run.Status, ex.Message);
        }
    }

    private async Task DerivePartialCountersAsync(SoDeliveryRun run)
    {
        try
        {
            var logs = await _db.GetLogsForRunAsync(run.Id);
            run.SuccessCount           = logs.Count(l => l.Status == SoLogStatus.Success);
            run.FailedCount            = logs.Count(l => l.Status == SoLogStatus.Failed);
            run.SkippedCount           = logs.Count(l =>
                                             l.Status == SoLogStatus.Skipped ||
                                             l.Status == SoLogStatus.SkippedAlreadyDone);
            run.ExceptionCount         = logs.Count(l => l.Status == SoLogStatus.Exception);
            run.TotalDeliveriesCreated = logs.Count(l => l.DeliveryDocEntry.HasValue);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[SoDelivery] Run {RunId} — could not derive partial counters for ABORTED run: {Error}",
                run.Id, ex.Message);
        }
    }

    private static SoDeliveryLog MakeSoLog(
        int        runId,
        OpenSoDto  so,
        string     status,
        Stopwatch  sw,
        int?       deliveryDocEntry  = null,
        int?       deliveryDocNum    = null,
        string?    errorMessage      = null,
        string?    sapErrorCode      = null,
        string?    sapErrorMessage   = null)
    => new SoDeliveryLog
    {
        RunId            = runId,
        SoDocEntry       = so.DocEntry,
        SoDocNum         = so.DocNum,
        CustomerCode     = so.CardCode,
        CustomerName     = so.CardName,
        Status           = status,
        DeliveryDocEntry = deliveryDocEntry,
        DeliveryDocNum   = deliveryDocNum,
        ErrorMessage     = errorMessage,
        SapErrorCode     = sapErrorCode,
        SapErrorMessage  = sapErrorMessage,
        ProcessedAt      = DateTime.UtcNow,
        DurationMs       = sw.ElapsedMilliseconds,
    };

    private static Dictionary<(string, string), decimal> AggregateRequired(
        List<OpenSoLineDto> lines) =>
        lines
            .Where(l => l.OpenQty > 0
                     && !string.IsNullOrWhiteSpace(l.ItemCode)
                     && !string.IsNullOrWhiteSpace(l.WhsCode))
            .GroupBy(l => (l.ItemCode.Trim().ToUpperInvariant(), l.WhsCode.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.OpenQty));

    private static List<StockShortage> FindInsufficientGroups(
        Dictionary<(string, string), decimal> required,
        Dictionary<(string, string), decimal> stock)
    {
        var result = new List<StockShortage>();
        foreach (var ((itemCode, whsCode), totalRequired) in required)
        {
            decimal onHand = stock.TryGetValue((itemCode, whsCode), out var qty) ? qty : 0m;
            if (onHand < totalRequired)
                result.Add(new StockShortage(itemCode, whsCode, totalRequired, onHand));
        }
        return result;
    }

    private static List<SoDeliveryLineLog> BuildLineLogs(
        int                                       logId,
        List<OpenSoLineDto>                       lines,
        Dictionary<(string, string), decimal>     stockBefore,
        List<StockShortage>?                      insufficientGroups,
        Dictionary<(string, string), decimal>?    stockAfter          = null,
        string?                                   raceWindowFailReason = null,
        Dictionary<int, List<LineBinAlloc>>?      binAllocsByLineNum   = null)
    {
        var shortageByKey = new Dictionary<(string, string), StockShortage>();
        if (insufficientGroups != null)
            foreach (var s in insufficientGroups)
                shortageByKey[(s.ItemCode, s.WhsCode)] = s;

        return lines.Select(l =>
        {
            var key       = (l.ItemCode.Trim().ToUpperInvariant(), l.WhsCode.Trim().ToUpperInvariant());
            decimal before = stockBefore.TryGetValue(key, out var b) ? b : 0m;
            decimal? after = stockAfter?.TryGetValue(key, out var a) == true ? a : null;

            string  status   = LineLogStatus.Ok;
            string? errorMsg = null;

            if (raceWindowFailReason != null)
            {
                status   = LineLogStatus.Skipped;
                errorMsg = raceWindowFailReason;
            }
            else if (shortageByKey.TryGetValue(key, out var shortage))
            {
                status   = LineLogStatus.Insufficient;
                errorMsg = $"Insufficient stock — required={shortage.Required:F4} " +
                           $"onHand={shortage.OnHand:F4} " +
                           $"shortage={shortage.Shortage:F4} [{shortage.WhsCode}]";
            }

            string? binJson = null;
            if (binAllocsByLineNum != null && binAllocsByLineNum.TryGetValue(l.LineNum, out var allocs) && allocs.Count > 0)
                binJson = JsonSerializer.Serialize(allocs.Select(a => new { a.BinCode, a.Qty }));

            return new SoDeliveryLineLog
            {
                LogId              = logId,
                LineNum            = l.LineNum,
                ItemCode           = l.ItemCode,
                ItemDescription    = l.ItemDescription,
                Quantity           = l.Quantity,
                OpenQuantity       = l.OpenQty,
                WarehouseCode      = l.WhsCode,
                OnHandBefore       = before,
                OnHandAfter        = after,
                Status             = status,
                ErrorMessage       = errorMsg,
                BinAllocationsJson = binJson,
            };
        }).ToList();
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains(
            "UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;

    // ── Nested Types ──────────────────────────────────────────────────────────

    private sealed record StockShortage(
        string  ItemCode,
        string  WhsCode,
        decimal Required,
        decimal OnHand)
    {
        public decimal Shortage => Required - OnHand;
    }

    /// <summary>
    /// Thrown when an ODLN was successfully committed to SAP but local audit persistence
    /// subsequently failed. Propagates out of ProcessSingleSoAsync so ProcessRunAsync
    /// can ABORT the entire run — we cannot trust counters or the report after this point.
    /// </summary>
    private sealed class CriticalAuditFailureException : Exception
    {
        public int  SoDocEntry  { get; }
        public int  SoDocNum    { get; }
        public int? SoLogId     { get; }   // null if SUCCESS log itself failed to save
        public int? DlvDocEntry { get; }
        public int? DlvDocNum   { get; }

        public CriticalAuditFailureException(
            int       soDocEntry,
            int       soDocNum,
            int?      soLogId,
            int?      dlvDocEntry,
            int?      dlvDocNum,
            string    message,
            Exception innerException)
            : base(message, innerException)
        {
            SoDocEntry  = soDocEntry;
            SoDocNum    = soDocNum;
            SoLogId     = soLogId;
            DlvDocEntry = dlvDocEntry;
            DlvDocNum   = dlvDocNum;
        }
    }
}
