namespace SapReplitAPI.Models.InvoiceLifecycle;

public class InvoiceLifecycleEvidence
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public DateTime DocDate { get; set; }
    public string DocStatus { get; set; } = string.Empty;
    public string CanceledFlag { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public decimal DocTotal { get; set; }
    public decimal PaidToDate { get; set; }
    public decimal BalanceDue { get; set; }
    public int ReceiptNum { get; set; }
    public string Comments { get; set; } = string.Empty;
    public string ReinvoicedFrom { get; set; } = string.Empty;

    public decimal AppliedPaymentSum { get; set; }
    public int LinkedPaymentCount { get; set; }
    public DateTime? LatestPaymentDate { get; set; }

    public int CreditMemoCount { get; set; }
    public decimal CreditMemoTotal { get; set; }

    public bool HasReplacementByReinvoiceReference { get; set; }
    public bool HasReplacementByLineage { get; set; }

    public HashSet<string> UpstreamKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> ReplacementInvoiceDocNums { get; } = new();

    public bool HasLinkedPayment => LinkedPaymentCount > 0 || ReceiptNum > 0;
    public bool HasCreditMemo => CreditMemoCount > 0 || CreditMemoTotal > 0;
    public bool HasReplacement => HasReplacementByReinvoiceReference || HasReplacementByLineage;
}
