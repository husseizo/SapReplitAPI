namespace SapReplitAPI.Models.Cache
{
    public class SalesTarget
    {
        public int Id { get; set; }

        public int SlpCode { get; set; }
        public string SlpName { get; set; } = "";

        public int SalesEmployeeCode { get; set; }
        public string SalesEmployeeName { get; set; } = "";

        public string Quarter { get; set; } = "";

        // ✅ New: Monthly tracking
        public int? Year { get; set; }
        public int? Month { get; set; }

        public decimal TargetAmount { get; set; }
    }
}