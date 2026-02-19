// File: Models/OrderLineModel.cs
namespace SapReplitAPI.Models.Orde_Models
{
    public class OrderLineModel
    {
        public string ItemCode { get; set; } = string.Empty;
        public string Dscription { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string WhsCode { get; set; } = "001";

       
        public string U_ItemName { get; set; } = string.Empty;
        public string U_Manufacturer { get; set; } = string.Empty;
       
    }
}