using System.Text.Json.Serialization;
using SapReplitAPI.Models.InvoiceLifecycle;

namespace SapReplitAPI.Models.Returns;

public sealed class InvoiceReturnsDto
{
    [JsonPropertyName("doc_entry")] public int DocEntry { get; set; }
    [JsonPropertyName("doc_num")] public int DocNum { get; set; }
    [JsonPropertyName("doc_date")] public DateTime DocDate { get; set; }
    [JsonPropertyName("card_code")] public string CardCode { get; set; } = string.Empty;
    [JsonPropertyName("card_name")] public string CardName { get; set; } = string.Empty;
    [JsonPropertyName("doc_status")] public string DocStatus { get; set; } = string.Empty;
    [JsonPropertyName("canceled")] public string Canceled { get; set; } = string.Empty;
    [JsonPropertyName("doc_total")] public decimal DocTotal { get; set; }
    [JsonPropertyName("paid_to_date")] public decimal PaidToDate { get; set; }
    [JsonPropertyName("balance_due")] public decimal BalanceDue { get; set; }
    [JsonPropertyName("credit_memo_count")] public int CreditMemoCount { get; set; }
    [JsonPropertyName("credit_memo_total")] public decimal CreditMemoTotal { get; set; }
    [JsonPropertyName("outstanding_amount")] public decimal OutstandingAmount { get; set; }
    [JsonPropertyName("lines")] public List<InvoiceReturnsLineDto> Lines { get; set; } = new();

    public void ApplyLifecycle(InvoiceLifecycleStatusResult status)
    {
        CreditMemoCount = status.CreditMemoCount;
        CreditMemoTotal = status.CreditMemoTotal;
        OutstandingAmount = status.OutstandingAmount;
    }
}

public sealed class InvoiceReturnsLineDto
{
    [JsonPropertyName("invoice_doc_entry")] public int InvoiceDocEntry { get; set; }
    [JsonPropertyName("invoice_line_num")] public int InvoiceLineNum { get; set; }
    [JsonPropertyName("item_code")] public string ItemCode { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("invoiced_qty")] public decimal InvoicedQty { get; set; }
    [JsonPropertyName("returned_qty")] public decimal ReturnedQty { get; set; }
    [JsonPropertyName("pending_return_qty")] public decimal PendingReturnQty { get; set; }
    [JsonPropertyName("returnable_qty")] public decimal ReturnableQty => Math.Max(0m, InvoicedQty - ReturnedQty - PendingReturnQty);
    [JsonPropertyName("unit_price")] public decimal UnitPrice { get; set; }
    [JsonPropertyName("whs_code")] public string WhsCode { get; set; } = string.Empty;
}
