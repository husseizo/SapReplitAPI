namespace SapReplitAPI.Models.InvoiceLifecycle;

public class InvoiceLifecycleStatusResult
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public InvoiceLifecycleStatus Status { get; set; }
    public string DocStatusDisplay { get; set; } = string.Empty;
    public string InvoiceStatus { get; set; } = string.Empty;
    public string PaymentsStatus { get; set; } = string.Empty;
    public string CancellationStatus { get; set; } = string.Empty;
    public decimal EffectivePaidAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public decimal AppliedPaymentSum { get; set; }
    public int LinkedPaymentCount { get; set; }
    public int CreditMemoCount { get; set; }
    public decimal CreditMemoTotal { get; set; }
    public bool HasReplacement { get; set; }
    public string ReplacementInvoiceNumbers { get; set; } = string.Empty;
    public DateTime? LatestPaymentDate { get; set; }
}
