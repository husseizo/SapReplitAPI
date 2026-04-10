namespace SapReplitAPI.Models.Payments
{
    /// <summary>
    /// Minimal payload returned by the payment micro-sync query.
    /// Contains only the three columns that change when an invoice is paid.
    /// </summary>
    public record InvoiceStatusPatch(
        int DocEntry,
        string DocStatus,
        decimal PaidToDate,
        decimal BalanceDue
    );
}
