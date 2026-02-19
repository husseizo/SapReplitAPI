namespace SapReplitAPI.Models.CachedProducts
{
    public class CachedCustomer
    {
        public int Id { get; set; }  // Primary key
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public string Region { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string CustomerType { get; set; } = string.Empty;
        public string SalesPersonName { get; set; } = string.Empty;
        public int? SalesPersonCode { get; set; }  // ← NEW FIELD
        public decimal TotalSpent { get; set; }
        public string AddressesJson { get; set; } = string.Empty;  // JSON string of addresses


        // 🔹 NEW
        public string VIN1 { get; set; } = string.Empty;
        public string VIN2 { get; set; } = string.Empty;
        public string VIN3 { get; set; } = string.Empty;
    }
}
