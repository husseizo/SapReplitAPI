namespace SapReplitAPI.Models.Cache
{
    public class CachedOrder
    {
        public int DocEntry { get; set; }
        public int DocNum { get; set; }
        public DateTime DocDate { get; set; }
        public string CardName { get; set; } = string.Empty;
        public decimal OrderValue { get; set; } = 0;
        public string Status { get; set; } = string.Empty;
        public int SlpCode { get; set; }
        public string SlpName { get; set; } = string.Empty;
        public string CancellationStatus { get; internal set; } = string.Empty;
    }
}
