using System;
using System.Collections.Generic;

namespace SapReplitAPI.Models.Cache
{
    public class CachedOpenOrder
    {
        public int DocEntry { get; set; } // Primary Key
        public int DocNum { get; set; }
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }
        public decimal OrderTotal { get; set; }
        public int SlpCode { get; set; }
        public string SlpName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // e.g., "Open", "Cancelled"

        public List<CachedOpenOrderLine> Lines { get; set; } = new();
      
    }
}