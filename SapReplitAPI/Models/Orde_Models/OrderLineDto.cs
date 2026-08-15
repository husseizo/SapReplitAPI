// File: Models/Orde_Models/OrderLineDto.cs
using System.Text.Json.Serialization;

namespace SapReplitAPI.Models.Orde_Models
{
    public class OrderLineDto
    {
        public string ItemCode { get; set; } = string.Empty;

        public int LineNum { get; set; }   // ✅ needed for UPSERT key
        public int Quantity { get; set; }
        public decimal Price { get; set; }

        // Optional SAP descriptive fields (used for GET)
        public string Dscription { get; set; } = string.Empty;
        public string WhsCode { get; set; } = "001";

       

        

        public string U_ItemName { get; set; } = string.Empty;
        public string U_Manufacturer { get; set; } = string.Empty;
    }
}