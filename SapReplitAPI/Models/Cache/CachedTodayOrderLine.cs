using System.ComponentModel.DataAnnotations.Schema;

namespace SapReplitAPI.Models.Cache
{
    public class CachedTodayOrderLine
    {
        public int Id { get; set; }

        // ✅ Foreign key to CachedTodayOrder.DocEntry
        public int DocEntry { get; set; }

        public string ItemCode { get; set; } = string.Empty;
        public string Dscription { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }
        public string WhsCode { get; set; } = string.Empty;
        public string U_ItemName { get; set; } = string.Empty;
        public string U_Manufacturer { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }

        
        public CachedTodayOrder Header { get; set; }
    }
}