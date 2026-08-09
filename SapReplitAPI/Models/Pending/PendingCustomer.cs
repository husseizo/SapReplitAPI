namespace SapReplitAPI.Models.Pending;

/// <summary>
/// A customer captured locally while SAP is offline.
/// Status flow: Pending → Synced | Failed
/// </summary>
public class PendingCustomer
{
    public int Id { get; set; }

    // Customer fields (mirror of CreateCustomerDto)
    public string CardName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string CustomerType { get; set; } = "";
    public string Region { get; set; } = "";
    public string SalesPersonName { get; set; } = "";
    public int SlpCode { get; set; }
    public string? VIN1 { get; set; }
    public string? VIN2 { get; set; }
    public string? VIN3 { get; set; }

    // Assigned by SAP once synced
    public string? SapCardCode { get; set; }

    // Pending | Synced | Failed
    public string Status { get; set; } = "Pending";

    public int RetryCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SyncedAt { get; set; }
}
