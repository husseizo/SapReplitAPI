namespace SapReplitAPI.Models;

public class GlAccountStatement
{
    public int Id { get; set; }

    // Journal entry identity
    public int TransId { get; set; }
    public string Account { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public DateTime RefDate { get; set; }

    // Amounts
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    // Description / references from OJDT/JDT1
    public string LineMemo { get; set; } = string.Empty;
    public string TransType { get; set; } = string.Empty;
    public string Ref1 { get; set; } = string.Empty;
    public string Ref2 { get; set; } = string.Empty;

    // Link to incoming payment (populated when TransType = "46")
    public int? PaymentDocEntry { get; set; }
    public int? PaymentDocNum { get; set; }

    // Link to the first invoice applied on the payment (may be null for advance payments)
    public int? InvoiceDocEntry { get; set; }
    public int? InvoiceDocNum { get; set; }

    // Business partner (from ORCT when available)
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
}
