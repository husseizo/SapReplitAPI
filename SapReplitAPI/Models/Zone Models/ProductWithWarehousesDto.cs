using System.Text.Json.Serialization;

public class ProductWithWarehousesDto
{
    public string ItemCode { get; set; } = string.Empty;
    [JsonIgnore]
    public string? ItemName { get; set; }

    [JsonIgnore]
    public string? U_Article_No { get; set; }

    [JsonIgnore]
    public string? U_MdlTEST { get; set; }

    [JsonIgnore]
    public string? U_Item_Name { get; set; }

    [JsonIgnore]
    public decimal Price { get; set; }

    public List<WarehouseStockDto> Warehouses { get; set; } = new List<WarehouseStockDto>();
    public decimal OnHand { get; set; }

    // Ignored in JSON response
   
    public decimal TotalOnHand { get; set; }

    [JsonIgnore]
    public bool IsMismatch { get; set; }



    [JsonIgnore]
    public decimal SumOfWarehouses { get; set; }
}