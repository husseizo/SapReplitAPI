namespace SapReplitAPI.Models.Orde_Models
{
    public class CreateOrderDto
    {
        public string CardCode { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }
        public DateTime? DeliveryDate { get; set; } = null;  // maps to ORDR.DocDueDate
        public int? Series { get; set; } = null;             // maps to ORDR.Series
        public string DocCur { get; set; } = "TZS";          // maps to ORDR.DocCur
        public string VatRegNum { get; set; } = "01";        // maps to ORDR.VatRegNum
        public string DocStatus { get; set; } = "O";         // maps to ORDR.DocStatus
        public int? SlpCode { get; set; } // ✅ New: Sales Employee
        public List<OrderLineDto> Lines { get; set; } = new List<OrderLineDto>();
    }
}