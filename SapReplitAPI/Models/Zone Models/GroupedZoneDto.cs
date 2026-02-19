public class GroupedZoneDto
{
    public string Warehouse { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;    // parsed from BinCode
    public List<ProductDto> Items { get; set; } = new List<ProductDto>();
}