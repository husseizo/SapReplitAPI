public class InvoiceQueryParams
{
    public string? CardCodeOrName { get; set; }
    public string? Status { get; set; } // Open / Closed
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}