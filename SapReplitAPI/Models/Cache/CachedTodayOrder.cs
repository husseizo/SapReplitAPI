namespace SapReplitAPI.Models.Cache
{
    public class CachedTodayOrder
    {
        public int DocEntry { get; set; }
        public int DocNum { get; set; }
        public string CardName { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool Cancelled { get; set; } // ✅ Newly added
        public decimal OrderValue { get; set; }
        public int? SlpCode { get; set; }
        public string SlpName { get; set; } = string.Empty;

        // ✅ Navigation property for related lines
        public ICollection<CachedTodayOrderLine> Lines { get; set; } = new List<CachedTodayOrderLine>();
    }
}