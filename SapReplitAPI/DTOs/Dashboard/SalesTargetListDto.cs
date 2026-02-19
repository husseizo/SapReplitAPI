namespace SapReplitAPI.DTOs.Dashboard
{
    public class SalesTargetListDto
    {
        public int Id { get; set; }
        public int SlpCode { get; set; }
        public string SlpName { get; set; } = "";
        public string Quarter { get; set; } = "";
        public int? Year { get; set; }
        public int? Month { get; set; }
        public decimal TargetAmount { get; set; }
    }
}