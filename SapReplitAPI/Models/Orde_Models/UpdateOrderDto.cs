namespace SapReplitAPI.Models.Orde_Models
{
    public class UpdateOrderDto
    {
        public int DocEntry { get; set; }
        public List<OrderLineDto> UpdatedLines { get; set; } = new List<OrderLineDto>();
    }
}