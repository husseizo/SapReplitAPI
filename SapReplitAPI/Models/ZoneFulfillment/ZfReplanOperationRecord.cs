namespace SapReplitAPI.Models.ZoneFulfillment;

/// <summary>
/// Durable recovery record for an AMBER SO-edit replan.
/// Created before the first SAP mutation; advanced through each step.
/// Delivery automation checks this before creating ODLN.
/// </summary>
public sealed class ZfReplanOperationRecord
{
    public long      Id                { get; set; }
    public Guid      OperationId       { get; set; }
    public int       SoDocEntry        { get; set; }
    public Guid      RequestId         { get; set; }
    public string    ChangedBy         { get; set; } = "";
    public string    CurrentStep       { get; set; } = ReplanStep.Prepared;
    /// <summary>Last step that completed successfully; null = nothing yet.</summary>
    public string?   LastGoodStep      { get; set; }
    public string?   LastError         { get; set; }
    /// <summary>Serialized UpdateOrderDto — needed to replay UpdateOrder on resume.</summary>
    public string    DtoJson           { get; set; } = "{}";
    /// <summary>JSON array of int AbsEntries that were retired in this replan.</summary>
    public string    OldAbsEntriesJson { get; set; } = "[]";
    /// <summary>JSON array of new int AbsEntries created after SO update.</summary>
    public string?   NewAbsEntriesJson { get; set; }
    public DateTime  StartedAtUtc      { get; set; }
    public DateTime? CompletedAtUtc    { get; set; }
}

/// <summary>Step constants for ZfReplanOperationRecord.CurrentStep / LastGoodStep.</summary>
public static class ReplanStep
{
    public const string Prepared                    = "Prepared";
    public const string OldPickListsRetired         = "OldPickListsRetired";
    public const string SalesOrderUpdated           = "SalesOrderUpdated";
    /// <summary>SAP GUI already updated the SO; no UpdateOrder call was made.</summary>
    public const string ExternalSalesOrderAccepted  = "ExternalSalesOrderAccepted";
    public const string FragmentsSynchronized       = "FragmentsSynchronized";
    public const string ReplacementPickListsCreated = "ReplacementPickListsCreated";
    public const string Completed                   = "Completed";
    public const string RecoveryRequired            = "RecoveryRequired";
    public const string FailedBeforeMutation        = "FailedBeforeMutation";

    private static readonly string[] _ordered =
    [
        Prepared,
        OldPickListsRetired,
        SalesOrderUpdated,
        ExternalSalesOrderAccepted,  // ordinal 3 — SAP GUI path; always > SalesOrderUpdated(2)
        FragmentsSynchronized,
        ReplacementPickListsCreated,
        Completed
    ];

    /// <summary>Returns the ordinal of a forward-progress step, or -1 if unknown/null.</summary>
    public static int Ordinal(string? step)
    {
        if (step is null) return -1;
        var idx = Array.IndexOf(_ordered, step);
        return idx;
    }
}
