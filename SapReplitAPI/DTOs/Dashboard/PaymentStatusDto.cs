namespace SapReplitAPI.DTOs.Dashboard
{
    public class PaymentStatusDto
    {
        public string Status { get; set; } = string.Empty;  // "Paid", "Partial", etc.
        public int Count { get; set; }
    }
}
