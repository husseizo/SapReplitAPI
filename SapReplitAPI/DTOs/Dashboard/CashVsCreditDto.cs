namespace SapReplitAPI.DTOs.Dashboard
{
    // For new summarized response
    public class CashVsCreditSummaryDto
    {
        public decimal Credit { get; set; }
        public decimal Cash { get; set; }
        public decimal Total => Credit + Cash;
    }

    // For older per-type list if still needed
    public class CashVsCreditDto
    {
        public string PaymentType { get; set; } = string.Empty;
        public decimal Total { get; set; }
    }
}