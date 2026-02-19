namespace SapReplitAPI.DTOs.Dashboard
{
    public class RevenueTrendDto
    {
        public string Period { get; set; } = "";
        public decimal OpenRevenue { get; set; }
        public decimal ClosedRevenue { get; set; }

        // Automatically computes TotalRevenue
        public decimal TotalRevenue => OpenRevenue + ClosedRevenue;
    }
}