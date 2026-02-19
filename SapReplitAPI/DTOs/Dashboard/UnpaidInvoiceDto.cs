// File: DTOs/Dashboard/UnpaidInvoiceDto.cs
namespace SapReplitAPI.DTOs.Dashboard
{
    public class UnpaidInvoiceDto
    {
        public int DocNum { get; set; }
        public string CardName { get; set; } = string.Empty; // Also used as 'Customer'
        public DateTime DocDate { get; set; }

        public decimal DocTotal { get; set; }               // Also used as 'Total'
        public decimal PaidToDate { get; set; }             // Also used as 'Paid'
        public decimal Balance => DocTotal - PaidToDate;    // Used as 'Balance'

        public int SlpCode { get; set; }                    // Used explicitly
        public string SlpName { get; set; } = string.Empty; // Optional
    }
}