public class PagedOrderResultDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<OrderHeaderDto> Orders { get; set; } = new();
}
