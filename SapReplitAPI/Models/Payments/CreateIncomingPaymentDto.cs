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
        /// Physical channel: CashOnHand | MPesaLipa | TigoLipa | CRDB | AALNMB
        ///   — use for both regular invoice payments AND receiving an advance (invoices: []).
        ///   SAP posts: DR physical-channel GL, CR AR (invoice) or CR 202011 (on-account).
        ///
        /// Settlement channel: AdvanceCustomerPayments
        ///   — use to settle invoices from a previously received advance balance.
        ///   SAP posts: DR 202011, CR AR (invoice). Invoices must be non-empty.
        /// </summary>
        public string PaymentChannel { get; set; } = string.Empty;

        /// <summary>
        /// Required for transfer-based physical channels (MPesaLipa, TigoLipa, CRDB, AALNMB).
        /// Not required for CashOnHand or AdvanceCustomerPayments.
        /// </summary>
        public string? TransferReference { get; set; }

        public DateTime PaymentDate { get; set; }

        public string? Remarks { get; set; }

        /// <summary>Sales employee code (SlpCode) to stamp on the payment.</summary>
        public string? SalesEmployeeCode { get; set; }

        /// <summary>
        /// Invoices to apply this payment against.
        /// Leave empty when receiving an advance — TotalAmount is required in that case.
        /// Must be non-empty when PaymentChannel is AdvanceCustomerPayments (settlement).
        /// </summary>
        public List<InvoiceApplicationDto> Invoices { get; set; } = new();

        /// <summary>
        /// Required when Invoices is empty (advance receipt via physical channel).
        /// Ignored when Invoices is non-empty.
        /// </summary>
        public decimal? TotalAmount { get; set; }

        /// <summary>
        /// Caller-supplied unique ID (e.g. the accounts-app audit log ID: "A57").
        /// If a payment with this reference was already posted successfully, the
        /// existing PaymentDocEntry/PaymentDocNum is returned without re-posting to SAP.
        /// Strongly recommended — prevents double-posting on timeout/retry.
        /// </summary>
        public string? ClientReference { get; set; }
    }
}
