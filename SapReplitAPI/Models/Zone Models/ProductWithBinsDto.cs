public class ProductWithBinsDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string U_Article_No { get; set; } = string.Empty;
    public string U_MdlTEST { get; set; } = string.Empty;
    public string U_Item_Name { get; set; } = string.Empty; 
    public decimal Price { get; set; } 
    public List<BinDto> Bins { get; set; } = new List<BinDto>();
}