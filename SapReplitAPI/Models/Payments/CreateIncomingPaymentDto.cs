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

        /// <summary>
        /// Caller-supplied unique ID (e.g. the accounts-app audit log ID: "A57").
        /// If a payment with this reference was already posted successfully, the
        /// existing PaymentDocEntry/PaymentDocNum is returned without re-posting to SAP.
        /// Strongly recommended — prevents double-posting on timeout/retry.
        /// </summary>
        public string? ClientReference { get; set; }

        /// <summary>
        /// Required when PaymentChannel is "AdvanceCustomerPayments".
        /// Specifies the physical channel through which the money arrived
        /// (CashOnHand | MPesaLipa | TigoLipa | CRDB | AALNMB).
        /// Used as the receiving GL account (debit); 140200 becomes the counter account (credit).
        /// </summary>
        public string? ReceivingChannel { get; set; }
    }
}
