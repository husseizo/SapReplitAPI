namespace SapReplitAPI.DTOs.Dashboard
{
    public class MonthToDateSalesDto
    {
        public int SlpCode { get; set; }
        public decimal CashSales { get; set; }
        public decimal CreditSales { get; set; }   // aged Open
        public decimal PendingSales { get; set; }  // not-yet-aged Open
        public decimal TotalSales => CashSales + CreditSales + PendingSales;
    }
}
