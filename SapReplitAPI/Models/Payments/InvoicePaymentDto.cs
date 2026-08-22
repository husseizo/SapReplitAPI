namespace SapReplitAPI.Models.Payments
{
    public class InvoicePaymentDto
    {
        public int PaymentDocEntry { get; set; }
        public int PaymentNumber { get; set; }
        public int InvoiceDocNum { get; set; } // ✅ Add this line
        public DateTime PaymentDate { get; set; }
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public decimal AmountApplied { get; set; }
        public decimal BankTransferAmount { get; set; }
        public string BankTransferReference { get; set; } = string.Empty;
        public string DebitAccountCode { get; set; } = string.Empty;
        public string DebitAccountName { get; set; } = string.Empty;
        public string SalesEmployeeCode { get; internal set; } = string.Empty;
        public string SalesEmployeeName { get; internal set; } = string.Empty;
        public int DocEntry { get; internal set; }
        public string ClientReference { get; set; } = string.Empty;
        public bool Canceled { get; set; }
        public string CounterRef { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }
}