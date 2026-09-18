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
        // Quantity returned via credit memos (sum of non-cancelled CM lines referencing this line)
        public decimal ReturnedQty { get; set; }
        // Open quantity still pending in active return requests for this invoice line
        public decimal PendingReturnQty { get; set; }

        // Base-document reference (INV1.BaseType/BaseEntry/BaseLine)
        // -1 = no base, 15 = ODLN, 17 = ORDR; BaseEntry/BaseLine null when BaseType=-1
        public int BaseType { get; set; }
        public int? BaseEntry { get; set; }
        public int? BaseLine { get; set; }
    }
}
