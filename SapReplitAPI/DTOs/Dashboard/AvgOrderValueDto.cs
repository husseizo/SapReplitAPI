namespace SapReplitAPI.DTOs.Dashboard
{
    public class AvgOrderValueDto
    {
        public string SlpCode { get; set; } = string.Empty; // was int
        public decimal AverageOrderValue { get; set; }
    }
}
