namespace SapReplitAPI.Models.InvoiceLifecycle;

public enum InvoiceLifecycleStatus
{
    Unknown = 0,
    OpenInvoice = 1,
    ClosedInvoice = 2,
    PaidInvoice = 3,
    PartiallyPaidInvoice = 4,
    CanceledInvoice = 5,
    CancellationDocument = 6,
    ReplacedOrReinvoiced = 7
}
