namespace SapReplitAPI.Models.Orde_Models
{
    public class OrderDto
    {
        public int DocEntry { get; set; }
        public string CardCode { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }
        public List<OrderLineDto> Lines { get; set; } = new List<OrderLineDto>();
        public string CardNme { get; internal set; } = string.Empty;   
        public String DocStatus { get; internal set; } = string.Empty;
        public decimal DocTotal { get; internal set; }
        public string SlpName { get; set; } = string.Empty;
        public int SlpCode { get; set; }
        public int DocNum { get; internal set; }
        public DateTime DocDueDate { get; internal set; }
    }
}