public class ProductDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? U_Article_No { get; set; }
    public string? U_MdlTEST { get; set; }
    public decimal Price { get; set; }
    public decimal OnHandQty { get; set; }

    public string? WhsCode { get; set; }
    public string? U_Item_Name { get; set; }
    public decimal OnHand { get; set; } // ✅ Correct
}