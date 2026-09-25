using System.Text.Json.Serialization;

public class ProductWithWarehouseDto
{
    public string ItemCode { get; set; } = string.Empty;

    
    public string? ItemName { get; set; }

   
    public string? U_Article_No { get; set; }

  
    public string? U_MdlTEST { get; set; }

    
    public string? U_Item_Name { get; set; }

  
    public decimal Price01 { get; set; }  // PL1
    public decimal Price02 { get; set; }  // PL2
    public decimal Price { get; set; }    // PL3 (legacy)
    public decimal Price04 { get; set; }  // PL4
    public decimal Price05 { get; set; }  // PL5

    // True when SAP returned an actual ITM1 row for this price list (genuine value,
    // possibly 0). False means no ITM1 row was found — the 0 default above is a
    // placeholder, not a real price, and callers must not use it to overwrite an
    // existing cached price. Default true so DTO producers that don't set these
    // (e.g. GetProductsForItems, used only to insert brand-new cache rows) are
    // unaffected.
    public bool Price01Loaded { get; set; } = true;
    public bool Price02Loaded { get; set; } = true;
    public bool PriceLoaded   { get; set; } = true;
    public bool Price04Loaded { get; set; } = true;
    public bool Price05Loaded { get; set; } = true;


    public List<WarehouseStockDto> Warehouses { get; set; } = new List<WarehouseStockDto>();
    public decimal OnHand { get; set; }

    // Ignored in JSON response
    
    public decimal TotalOnHand { get; set; }


}