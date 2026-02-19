using System.Text.Json.Serialization;

namespace SapReplitAPI.Models.CustomerModels
{
    public class CreateCustomerDto
    {
        public string CardName { get; set; } = string.Empty;         // Required
        public string Phone { get; set; } = string.Empty;            // Optional
        public string CustomerType { get; set; } = string.Empty;     // Optional
        public string Region { get; set; } = string.Empty;           // Required (dropdown on frontend)
        public string SalesPersonName { get; set; } = string.Empty;  // Required (dropdown on frontend)
        public string Address { get; set; } = string.Empty; // 🆕 Added

        [JsonPropertyName("salesPersonCode")]
        public int SlpCode { get; set; } = 0; // optional, still present if needed
    }
}