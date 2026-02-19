
using SapReplitAPI.Models.CustomerModels;

public class PagedCustomerResultDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<CustomerDto> Customers { get; set; } = new();
}