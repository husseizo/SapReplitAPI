public class InvoiceInsightDto
{
    public string SalesName { get; set; } = string.Empty;
    public DateTime? PostingDate { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public string ReinvoicedFrom { get; set; } = string.Empty;
    public string InvoiceStatus { get; set; } = string.Empty;
    public DateTime? PaidDate { get; set; }
    public string Customer { get; set; } = string.Empty;
    public decimal CashSales { get; set; }
    public decimal CreditSales { get; set; }
    public decimal ReturnedSales { get; set; }
    public string PaymentsStatus { get; set; } = string.Empty;
    public string CancellationStatus { get; set; } = string.Empty;
}