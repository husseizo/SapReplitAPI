namespace SapReplitAPI.Models.Cache
{
    public class CachedInvoicePayment
    {
        public int Id { get; set; }

        public int DocEntry { get; set; }

        public int PaymentDocEntry { get; set; }
        public int PaymentNumber { get; set; }
        public DateTime PaymentDate { get; set; }

        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;

        public decimal AmountApplied { get; set; }
        public decimal BankTransferAmount { get; set; }

        public string BankTransferReference { get; set; } = string.Empty;
        public string DebitAccountCode { get; set; } = string.Empty;
        public string DebitAccountName { get; set; } = string.Empty;

        public string SalesEmployeeCode { get; set; } = string.Empty;
        public string SalesEmployeeName { get; set; } = string.Empty;
        public int InvoiceDocNum { get; internal set; }
    }
}
