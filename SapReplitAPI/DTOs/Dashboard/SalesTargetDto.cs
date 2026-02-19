namespace SapReplitAPI.DTOs.Dashboard
{
    public class SalesTargetDto
    {
        public int SlpCode { get; set; }

        public string Quarter { get; set; } = string.Empty;

        public int? Year { get; set; }
        public int? Month { get; set; }

        public decimal TargetAmount { get; set; }
    }
}