namespace SapReplitAPI.Models.Cache
{
    public class CachedInvoice
    {
        public int DocEntry { get; set; } // ✅ Primary key

        public int DocNum { get; set; }
        public int InvoiceDocNum { get; set; } // ✅ NEW FIELD
        public DateTime DocDate { get; set; }
        public string DocStatus { get; set; } = string.Empty;
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public decimal DocTotal { get; set; }
        public decimal PaidToDate { get; set; }
        public decimal BalanceDue { get; set; }
        public int SalesEmployeeCode { get; set; }
        public string SalesEmployeeName { get; set; } = string.Empty;
        public int DaysOverdue { get; set; }

        public int GroupNum { get; set; } = 0; // e.g. 1 for Cash, 3 for Credit, etc.

        public List<CachedInvoiceLine> Lines { get; set; } = new();
        public string Canceled { get; internal set; } = string.Empty; // Indicates if the invoice is canceled
        public string DocStatusDisplay { get; set; } = string.Empty;

        // Zone Fulfillment UDFs (nullable — null for non-ZF invoices)
        public string? ZoneRef          { get; set; }
        public string? U_ReplitId       { get; set; }
        public string? DeliveryLocation { get; set; }
    }
}