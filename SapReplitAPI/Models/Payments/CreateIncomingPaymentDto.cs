namespace SapReplitAPI.Models.Payments
{
    public class InvoiceApplicationDto
    {
        public int DocEntry { get; set; }
        public decimal AmountApplied { get; set; }
    }

    public class CreateIncomingPaymentDto
    {
        public string CardCode { get; set; } = string.Empty;

        /// <summary>
        /// CashOnHand | MPesaLipa | TigoLipa | CRDB | AALNMB | AdvanceCustomerPayments
        /// </summary>
        public string PaymentChannel { get; set; } = string.Empty;

        /// <summary>
        /// Required for all transfer channels (M-Pesa ref, Tigo ref, bank ref, etc.)
        /// </summary>
        public string? TransferReference { get; set; }

        public DateTime PaymentDate { get; set; }

        public string? Remarks { get; set; }

        /// <summary>
        /// Sales employee code (SlpCode) to stamp on the payment.
        /// </summary>
        public string? SalesEmployeeCode { get; set; }

        /// <summary>
        /// Invoices to apply this payment against.
        /// Leave empty for advance/on-account payments — TotalAmount is required in that case.
        /// </summary>
        public List<InvoiceApplicationDto> Invoices { get; set; } = new();

        /// <summary>
        /// Required when Invoices is empty (advance payment scenario).
        /// Ignored when Invoices is non-empty (sum of AmountApplied is used instead).
        /// </summary>
        public decimal? TotalAmount { get; set; }
    }
}
