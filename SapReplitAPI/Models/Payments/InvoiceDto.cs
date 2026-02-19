namespace SapReplitAPI.Models.Payments
{
    public class InvoiceDto
    {
        private static readonly List<InvoiceLineDto> invoiceLineDtos = new();

        public int DocEntry { get; set; }
        public int DocNum { get; set; }
        public DateTime DocDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public decimal DocTotal { get; set; }
        public List<InvoiceLineDto> Lines { get; set; } = invoiceLineDtos;
        public int SalesEmployeeCode { get; internal set; } 
        public string SalesEmployeeName { get; internal set; } = string.Empty;
        public decimal PaidToDate { get; internal set; } 
        public decimal BalanceDue { get; internal set; }
        public int DaysOverdue { get; set; }
        public int GroupNum { get; internal set; }
        public string? Canceled { get; set; } = string.Empty;
    }

    public class 
        InvoiceLineDto
    {
        public string ItemCode { get; set; } = string.Empty;
        
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal LineTotal { get; set; }
        
        public string Dscription { get; internal set; } = string.Empty;
        public decimal LineNum { get; internal set; }
        public string U_Item_Name { get; internal set; } = string.Empty;
       
        
        public string U_Manufacturer { get; internal set; } = string.Empty;
        public string? U_MdlTEST { get; internal set; }
        public string? U_ItemName { get; internal set; }
    }
}