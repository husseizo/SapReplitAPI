namespace SapReplitAPI.Models.Pending;

/// <summary>
/// An order captured locally while SAP is offline (or immediately after creation for tracking).
/// Status flow: Draft → Pending → Synced | Failed
/// </summary>
public class PendingOrder
{
    public int Id { get; set; }

    /// <summary>Idempotency key — set at creation, sent to SAP as U_ReplitId UDF.</summary>
    public string ReplitId { get; set; } = "";

    public string CardCode { get; set; } = "";
    public DateTime DocDate { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public int? SlpCode { get; set; }
    public string DocCurrency { get; set; } = "TZS";

    /// <summary>Draft | Pending | Synced | Failed | Cancelled</summary>
    public string Status { get; set; } = "Pending";

    public int? SapDocEntry { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SyncedAt { get; set; }

    public List<PendingOrderLine> Lines { get; set; } = new();
}
