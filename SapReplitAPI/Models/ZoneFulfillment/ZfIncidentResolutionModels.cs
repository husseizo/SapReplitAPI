namespace SapReplitAPI.Models.ZoneFulfillment;

// ── Resolution type constants ────────────────────────────────────────────────

/// <summary>
/// Allowed resolution values for a ZF diagnostic incident.
/// ACKNOWLEDGED_EXTERNAL_SAP_EDIT: operator acknowledges the RDR1 deletion was an external
///   SAP change; marks the incident RESOLVED and preserves history.
/// RESTORE_REQUIRED: operator records that the SAP line must be manually restored; marks ACKNOWLEDGED.
/// CANCEL_FRAGMENT_REQUIRED: operator records that the fragment must be cancelled; marks ACKNOWLEDGED.
/// FALSE_POSITIVE: operator determines this incident was incorrectly raised; marks RESOLVED.
/// DEFERRED: operator defers action to a later time; marks DEFERRED.
/// </summary>
public static class ZfIncidentResolution
{
    public const string AcknowledgedExternalSapEdit = "ACKNOWLEDGED_EXTERNAL_SAP_EDIT";
    public const string RestoreRequired             = "RESTORE_REQUIRED";
    public const string CancelFragmentRequired      = "CANCEL_FRAGMENT_REQUIRED";
    public const string FalsePositive               = "FALSE_POSITIVE";
    public const string Deferred                    = "DEFERRED";

    public static readonly IReadOnlyList<string> AllValues =
    [
        AcknowledgedExternalSapEdit,
        RestoreRequired,
        CancelFragmentRequired,
        FalsePositive,
        Deferred,
    ];

    /// <summary>
    /// Maps a resolution value to the resulting incident status.
    /// ACKNOWLEDGED_EXTERNAL_SAP_EDIT → RESOLVED (external edit is acknowledged as truth).
    /// FALSE_POSITIVE → RESOLVED (no real problem).
    /// RESTORE_REQUIRED / CANCEL_FRAGMENT_REQUIRED → ACKNOWLEDGED (intent recorded, action pending).
    /// DEFERRED → DEFERRED.
    /// </summary>
    public static string ToIncidentStatus(string resolution) => resolution switch
    {
        AcknowledgedExternalSapEdit => ZfIncidentStatusValue.Resolved,
        FalsePositive               => ZfIncidentStatusValue.Resolved,
        RestoreRequired             => ZfIncidentStatusValue.Acknowledged,
        CancelFragmentRequired      => ZfIncidentStatusValue.Acknowledged,
        Deferred                    => ZfIncidentStatusValue.Deferred,
        _                           => ZfIncidentStatusValue.Active,
    };
}

// ── Incident status values ───────────────────────────────────────────────────

public static class ZfIncidentStatusValue
{
    public const string Active       = "ACTIVE";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Resolved     = "RESOLVED";
    public const string Deferred     = "DEFERRED";
}

// ── Request / response models ────────────────────────────────────────────────

/// <summary>Request body for POST .../diagnostic-incidents/{incidentKey}/resolve.</summary>
public sealed class ZfIncidentResolutionRequest
{
    /// <summary>
    /// Client-generated idempotency key (GUID).
    /// Generate once at the final confirmation step and reuse on any network retry.
    /// Generate a new GUID only for a genuinely new operator decision.
    /// When supplied, replayed submissions return the original stored result without
    /// inserting a duplicate row.  Omitting it falls back to append-only behaviour.
    /// </summary>
    public Guid?   ResolutionRequestId { get; set; }

    /// <summary>One of the ZfIncidentResolution constants.</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Operator name or ID (required, non-empty).</summary>
    public string Operator   { get; set; } = string.Empty;

    /// <summary>Free-text reason (required, non-empty).</summary>
    public string Reason     { get; set; } = string.Empty;
}

/// <summary>
/// Immutable history record stored in dbo.ZfIncidentResolutions on MolasIntegration (SQL Server).
/// Resolution records are append-only; never updated or deleted.
/// </summary>
public sealed class ZfIncidentResolutionRecord
{
    public long      Id            { get; set; }

    /// <summary>Client-generated idempotency GUID. NULL for legacy rows.</summary>
    public Guid?     ResolutionRequestId { get; set; }

    /// <summary>Deterministic key: "{SoDocNum}_{FragmentId}_{IncidentCode}"</summary>
    public string    IncidentKey   { get; set; } = string.Empty;

    /// <summary>User-visible SO number (DocNum in SAP).</summary>
    public int       SoDocNum      { get; set; }

    /// <summary>Internal SAP DocEntry for the Sales Order (ORDR.DocEntry).</summary>
    public int?      SoDocEntry    { get; set; }

    /// <summary>ZF orchestration ID at time of resolution.</summary>
    public long?     OrchestrationId { get; set; }

    public long?     FragmentId    { get; set; }
    public string    IncidentCode  { get; set; } = string.Empty;

    /// <summary>One of the ZfIncidentResolution constants.</summary>
    public string    Resolution    { get; set; } = string.Empty;

    /// <summary>Resulting status after this resolution (ZfIncidentStatusValue).</summary>
    public string    Status        { get; set; } = string.Empty;

    public string    Operator      { get; set; } = string.Empty;
    public string    Reason        { get; set; } = string.Empty;
    public DateTime  ResolvedAtUtc { get; set; }

    /// <summary>JSON-serialized original incident evidence captured at resolution time.</summary>
    public string?   EvidenceJson  { get; set; }
}
