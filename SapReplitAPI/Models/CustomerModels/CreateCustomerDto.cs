using System.Text.Json.Serialization;

namespace SapReplitAPI.Models.CustomerModels
{
    public class CreateCustomerDto
    {
        public string CardName { get; set; } = string.Empty;         // Required
        public string Phone { get; set; } = string.Empty;            // Optional
        public string CustomerType { get; set; } = string.Empty;     // Optional
        public string Region { get; set; } = string.Empty;           // Preferred region field
        public string City { get; set; } = string.Empty;             // Fallback: ODOO sends city as the region
        public string SalesPersonName { get; set; } = string.Empty;  // Required (dropdown on frontend)
        public string Address { get; set; } = string.Empty;          // Legacy address field
        public string Address1 { get; set; } = string.Empty;         // ODOO address field

        // Vehicle Identification Numbers (UDFs U_VIN1 / U_VIN2 / U_VIN3) — all optional
        public string? VIN1 { get; set; }
        public string? VIN2 { get; set; }
        public string? VIN3 { get; set; }

        [JsonPropertyName("salesPersonCode")]
        public int SlpCode { get; set; } = 0;
    }
}