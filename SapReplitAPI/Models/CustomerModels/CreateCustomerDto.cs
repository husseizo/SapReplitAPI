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
        public string Address { get; set; } = string.Empty;

        // Vehicle Identification Numbers (UDFs U_VIN1 / U_VIN2 / U_VIN3)
        public string VIN1 { get; set; } = string.Empty;
        public string VIN2 { get; set; } = string.Empty;
        public string VIN3 { get; set; } = string.Empty;

        [JsonPropertyName("salesPersonCode")]
        public int SlpCode { get; set; } = 0;
    }
}