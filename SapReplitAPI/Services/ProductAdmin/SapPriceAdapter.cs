using SapReplitAPI.Models.ProductAdmin;

namespace SapReplitAPI.Services.ProductAdmin;

public sealed class SapPriceAdapter : ISapPriceAdapter
{
    private readonly SapService _sap;

    public SapPriceAdapter(SapService sap) => _sap = sap;

    public SapCurrentPrice? ReadItemPrice(string itemCode, int priceListNum)
        => _sap.ReadItemPrice(itemCode, priceListNum);

    public (int rc, string sapError, decimal? actualPriceAfter, string currency) UpdateItemPrice(
        string itemCode, int priceListNum, decimal newPrice)
        => _sap.UpdateItemPrice(itemCode, priceListNum, newPrice);
}
