namespace SapReplitAPI.DTOs.Dashboard
{
    public class InvoiceStatusDto
    {
        public string Status { get; set; } = string.Empty; // "O", "C", etc.
        public int Count { get; set; }
    }
}
