#pragma warning disable CA1707

using System;

namespace SapReplitAPI.Models.Cache
{
    public class CachedOrderLine
    {
        public int Id { get; set; }
        public int DocEntry { get; set; }
        public DateTime DocDate { get; set; }
        public int LineNum { get; set; }   // ✅ needed for UPSERT key

        public string ItemCode { get; set; } = string.Empty;
        public string Dscription { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string WhsCode { get; set; } = string.Empty;

        public string U_ItemName { get; set; } = string.Empty;
        public string U_Manufacturer { get; set; } = string.Empty;
       
    }
}