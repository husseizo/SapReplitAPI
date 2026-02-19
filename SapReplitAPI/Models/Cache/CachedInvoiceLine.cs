using System.Text.Json.Serialization;

namespace SapReplitAPI.Models.Cache
{
    public class CachedInvoiceLine
    {
        public int Id { get; set; }
        public int DocEntry { get; set; }
        public int LineNum { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string Dscription { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal LineTotal { get; set; }
        public string U_Item_Name { get; set; } = string.Empty;

        [JsonIgnore]
        public string U_ItemName { get; set; } = string.Empty;

        [JsonIgnore]
        public string U_MDLTsT { get; set; } = string.Empty;

        
        public string U_Manufacturer { get; internal set; } = string.Empty;
        
        public string? U_MdlTEST { get; internal set; } = string.Empty;
    }
}
