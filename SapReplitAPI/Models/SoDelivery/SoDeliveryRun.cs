namespace SapReplitAPI.Models.SoDelivery;

/// <summary>
/// One record per nightly (or manual) SO → Delivery processing run.
/// Multiple runs per ProcessingDate are allowed (e.g. forced manual re-runs).
/// RunStatus constants: RunStatus.Running / Completed / Aborted
/// </summary>
public class SoDeliveryRun
{
    public int      Id                     { get; set; }

    /// <summary>Business date being processed (date part only, time ignored).</summary>
    public DateTime ProcessingDate         { get; set; }

    public DateTime StartTime              { get; set; }
    public DateTime? EndTime              { get; set; }

    /// <summary>Running | Completed | Aborted — see RunStatus constants.</summary>
    public string   Status                 { get; set; } = RunStatus.Running;

    /// <summary>"Job" for automatic nightly trigger, "Manual" for API-triggered runs.</summary>
    public string   TriggeredBy            { get; set; } = "Job";

    /// <summary>True when run was started with force=true despite an existing completed run.</summary>
    public bool     IsForced               { get; set; }

    public int      TotalOrders            { get; set; }
    public int      SuccessCount           { get; set; }
    public int      FailedCount            { get; set; }
    public int      SkippedCount           { get; set; }
    public int      ExceptionCount         { get; set; }
    public int      TotalDeliveriesCreated { get; set; }

    /// <summary>Full local path to generated PDF report. Null until PDF is created.</summary>
    public string?  PdfPath                { get; set; }

    /// <summary>Populated on Aborted runs to describe why the run did not complete.</summary>
    public string?  ErrorMessage           { get; set; }

    public ICollection<SoDeliveryLog> Logs { get; set; } = new List<SoDeliveryLog>();
}
