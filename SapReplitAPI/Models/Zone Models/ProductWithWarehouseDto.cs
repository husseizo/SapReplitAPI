using System.Text.Json.Serialization;

public class ProductWithWarehouseDto
{
    public string ItemCode { get; set; } = string.Empty;

    
    public string? ItemName { get; set; }

   
    public string? U_Article_No { get; set; }

  
    public string? U_MdlTEST { get; set; }

    
    public string? U_Item_Name { get; set; }

  
    public decimal Price { get; set; }
    public decimal Price05 { get; set; }


    public List<WarehouseStockDto> Warehouses { get; set; } = new List<WarehouseStockDto>();
    public decimal OnHand { get; set; }

    // Ignored in JSON response
    
    public decimal TotalOnHand { get; set; }


}