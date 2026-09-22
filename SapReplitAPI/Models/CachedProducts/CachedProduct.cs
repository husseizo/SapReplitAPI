public class CachedProduct
{
    public int Id { get; set; }

    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? U_Article_No { get; set; }
    public string? U_MdlTEST { get; set; }
    public decimal Price01 { get; set; }  // PL1
    public decimal Price02 { get; set; }  // PL2
    public decimal Price { get; set; }    // PL3 (legacy field name preserved)
    public decimal Price04 { get; set; }  // PL4
    public decimal Price05 { get; set; }  // PL5

    public decimal OnHandQty { get; set; }
    public decimal OnHand { get; set; }
    public string? WhsCode { get; set; }
    public DateTime LastUpdated { get; set; }
    public string? U_Item_Name { get; set; }
    public decimal TotalOnHand { get; set; } // OITM.OnHand


    public int? Whs_001 { get; set; }
    public int? Whs_002 { get; set; }
    public int? Whs_003 { get; set; }
    public int? Whs_004 { get; set; }
}