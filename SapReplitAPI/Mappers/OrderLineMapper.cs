// File: Mappers/OrderLineMapper.cs
using SapReplitAPI.Models;
using SapReplitAPI.Models.Orde_Models;

namespace SapReplitAPI.Mappers
{
    public static class OrderLineMapper
    {
        public static OrderLineDto ToDto(OrderLineModel model) => new()
        {
            ItemCode = model.ItemCode,
            Quantity = model.Quantity,
            Price = model.Price,
            Dscription = model.Dscription,
            WhsCode = model.WhsCode,
            U_ItemName = model.U_ItemName,
            U_Manufacturer = model.U_Manufacturer,
           
        };

        public static OrderLineModel ToModel(OrderLineDto dto) => new()
        {
            ItemCode = dto.ItemCode,
            Quantity = dto.Quantity,
            Price = dto.Price,
            Dscription = dto.Dscription,
            WhsCode = dto.WhsCode,
            U_ItemName = dto.U_ItemName,
            U_Manufacturer = dto.U_Manufacturer,
            
        };
    }
}