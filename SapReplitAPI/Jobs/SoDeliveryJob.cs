using Quartz;
using SapReplitAPI.Models.SoDelivery;
using SapReplitAPI.Services.SoDelivery;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Nightly Quartz job that converts open Sales Orders to SAP Delivery Notes.
/// Thin wrapper only — all business logic lives in SoDeliveryService.
///
/// Schedule: 20:00 EAT daily (cron "0 0 20 * * ?" registered in Program.cs Step 9
///           with InTimeZone(SoDeliveryJob.BusinessTz) to pin the fire time to EAT).
///
/// Timezone strategy:
///   Tanzania (Dar es Salaam) uses East Africa Time (EAT) = UTC+3, no DST.
///   Windows TZ ID  : "E. Africa Standard Time"
///   IANA TZ ID     : "Africa/Dar_es_Salaam"
///   This project targets win-x64 exclusively (SapReplitAPI.csproj RuntimeIdentifier=win-x64).
///   On .NET 6+ on Windows, FindSystemTimeZoneById accepts both IDs via ICU; the Windows
///   ID is used here for reliability on the target host. BusinessTz is exposed as a public
///   static so Program.cs (Step 9) can reference the same object when registering the
///   Quartz trigger with InTimeZone(), avoiding any drift between job and scheduler.
/// </summary>
[DisallowConcurrentExecution]
public class SoDeliveryJob : IJob
{
    /// <summary>
    /// East Africa Time (UTC+3). Public so Program.cs can pass it to the Quartz
    /// trigger via WithCronSchedule("0 0 20 * * ?", x => x.InTimeZone(SoDeliveryJob.BusinessTz)).
    /// </summary>
    public static readonly TimeZoneInfo BusinessTz =
        TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");

    private readonly SoDeliveryService         _service;
    private readonly ILogger<SoDeliveryJob>    _log;

    public SoDeliveryJob(SoDeliveryService service, ILogger<SoDeliveryJob> log)
    {
        _service = service;
        _log     = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Compute the business local date ONCE at job entry.
        // processingDate is fixed for the entire run — SoDeliveryService must not
        // use DateTime.Today internally (and it does not — enforced by Step 5).
        DateTime businessLocalNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BusinessTz);
        DateTime processingDate   = businessLocalNow.Date;

        _log.LogInformation(
            "🌙 [SoDeliveryJob] Nightly SO delivery run started — " +
            "ProcessingDate={Date} BusinessLocalTime={LocalTime} UTC={Utc}",
            processingDate.ToString("yyyy-MM-dd"),
            businessLocalNow.ToString("HH:mm:ss"),
            DateTime.UtcNow.ToString("HH:mm:ss"));

        SoDeliveryRun? run = null;
        try
        {
            run = await _service.ProcessRunAsync(
                processingDate,
                force:       false,
                triggeredBy: "Job");
        }
        catch (InvalidOperationException ioe)
        {
            // The run guard in SoDeliveryService blocked a duplicate run
            // (COMPLETED or RUNNING run already exists for today's date).
            // This is normal scheduler behavior, not an error — log as Information.
            _log.LogInformation(
                "⏭️ [SoDeliveryJob] Run skipped for {Date} — guard: {Reason}",
                processingDate.ToString("yyyy-MM-dd"), ioe.Message);
            return;
        }
        catch (Exception ex)
        {
            // ProcessRunAsync threw (e.g. SAP GetOpenSosForDate failed and re-threw after
            // marking the run ABORTED). The run record is already saved in the DB.
            // Do NOT create a second run. Quartz will re-trigger per its next scheduled firing.
            _log.LogError(ex,
                "❌ [SoDeliveryJob] ProcessRunAsync threw for {Date}: {Error}",
                processingDate.ToString("yyyy-MM-dd"), ex.Message);
            return;
        }

        // ── Log Final Result ─────────────────────────────────────────────────
        // SoDeliveryService owns run lifecycle. Job only observes and logs the outcome.
        long durationMs = run.EndTime.HasValue
            ? (long)(run.EndTime.Value - run.StartTime).TotalMilliseconds
            : -1L;

        if (run.Status == RunStatus.Completed)
        {
            _log.LogInformation(
                "✅ [SoDeliveryJob] Run {RunId} COMPLETED — " +
                "ProcessingDate={Date} TotalOrders={Total} " +
                "Success={Ok} Failed={Fail} Skipped={Skip} Exception={Ex} " +
                "Deliveries={Dlv} Duration={Ms}ms",
                run.Id,
                processingDate.ToString("yyyy-MM-dd"),
                run.TotalOrders,
                run.SuccessCount,
                run.FailedCount,
                run.SkippedCount,
                run.ExceptionCount,
                run.TotalDeliveriesCreated,
                durationMs);
        }
        else if (run.Status == RunStatus.Aborted)
        {
            _log.LogError(
                "❌ [SoDeliveryJob] Run {RunId} ABORTED — " +
                "ProcessingDate={Date} TotalOrders={Total} " +
                "Success={Ok} Failed={Fail} Skipped={Skip} Exception={Ex} " +
                "Deliveries={Dlv} Duration={Ms}ms ErrorMessage={Error}",
                run.Id,
                processingDate.ToString("yyyy-MM-dd"),
                run.TotalOrders,
                run.SuccessCount,
                run.FailedCount,
                run.SkippedCount,
                run.ExceptionCount,
                run.TotalDeliveriesCreated,
                durationMs,
                run.ErrorMessage);
        }
        else
        {
            _log.LogWarning(
                "⚠️ [SoDeliveryJob] Run {RunId} ended with unexpected Status={Status} for {Date}",
                run.Id, run.Status, processingDate.ToString("yyyy-MM-dd"));
        }
    }
}
