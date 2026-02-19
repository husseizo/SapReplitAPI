namespace SapReplitAPI.DTOs.Dashboard
{
    public class OpenDocsBreakdownDto
    {
        public int SlpCode { get; set; }
        public decimal AgedOpen { get; set; }     // aged per your rules
        public decimal PendingOpen { get; set; }  // not yet aged
        public decimal OpenTotal => AgedOpen + PendingOpen;
    }
}