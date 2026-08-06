namespace SapReplitAPI.Models.Invoicing;

public class InvoiceFromDeliveryLog
{
    public int Id { get; set; }
    public int DeliveryDocEntry { get; set; }
    public int DeliveryDocNum { get; set; }
    public string CardCode { get; set; } = "";
    public int? InvoiceDocEntry { get; set; }
    public int? InvoiceDocNum { get; set; }
    // Success | Failed | Skipped | Locked
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }
    // Job | Manual
    public string TriggerSource { get; set; } = "";
    public string ProcessedAt { get; set; } = "";
}
