using System.Text.Json.Serialization;

namespace SapReplitAPI.Models.CustomerModels
{
    public class CustomerDto
    {
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public string City { get; set; } = string.Empty;
        public string SalesPersonName { get; set; } = string.Empty;
        public int? SalesPersonCode { get; set; }
        public List<CustomerAddressDto> Addresses { get; set; } = new();
        public decimal TotalSpent { get; set; }
        
        public string Region { get; internal set; } = string.Empty;
        public string Phone { get; internal set; } = string.Empty;
        public string CustomerType { get; internal set; } = string.Empty;

        // 🔹 NEW VIN fields
        public string VIN1 { get; set; } = string.Empty;
        public string VIN2 { get; set; } = string.Empty;
        public string VIN3 { get; set; } = string.Empty;
    }
}