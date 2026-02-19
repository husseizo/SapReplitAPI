using System;

namespace SapReplitAPI.Models.Cache
{
    public class CachedOpenOrderLine
    {
        public int Id { get; set; } // PK
        public int DocEntry { get; set; } // FK to header
        public int LineNum { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string Dscription { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal LineTotal { get; set; }
        public string WhsCode { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }

        public CachedOpenOrder Header { get; set; }
        
    }
}