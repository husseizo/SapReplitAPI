using SapReplitAPI.Models.ProductAdmin;

namespace SapReplitAPI.Services.ProductAdmin;

public interface ISapPriceAdapter
{
    SapCurrentPrice? ReadItemPrice(string itemCode, int priceListNum);
    (int rc, string sapError, decimal? actualPriceAfter, string currency) UpdateItemPrice(
        string itemCode, int priceListNum, decimal newPrice);
}
