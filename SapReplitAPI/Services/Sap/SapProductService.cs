using SAPbobsCOM;
using SapReplitAPI.Models;
using SapReplitAPI.Models.ProductAdmin;

public class SapProductService
{
    public List<ProductWithWarehouseDto> GetLiveProducts(SAPbobsCOM.Company company, DateTime? from = null, DateTime? to = null)
    {
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

        try
        {
            bool isDelta = from.HasValue;

            string whereExtra = isDelta
                ? $@"AND (
            I.UpdateDate >= '{from!.Value:yyyy-MM-dd}'
            OR I.ItemCode IN (
                SELECT DISTINCT ItemCode FROM OINM
                WHERE DocDate >= '{from!.Value:yyyy-MM-dd}'
            )
         )"
                : "AND I.OnHand > 0";

            string sql = $@"
SELECT
    I.ItemCode,
    I.ItemName,
    I.U_Article_No,
    I.U_MdlTEST,
    I.U_Item_Name,
    P01.Price AS Price01,
    P02.Price AS Price02,
    P03.Price AS Price03,
    P04.Price AS Price04,
    P05.Price AS Price05,
    I.OnHand  AS TotalOnHand,
    W.WhsCode,
    W.OnHand  AS OnHandQty
FROM OITM I
JOIN OITW W     ON W.ItemCode = I.ItemCode
LEFT JOIN ITM1 P01 ON P01.ItemCode = I.ItemCode AND P01.PriceList = 1
LEFT JOIN ITM1 P02 ON P02.ItemCode = I.ItemCode AND P02.PriceList = 2
LEFT JOIN ITM1 P03 ON P03.ItemCode = I.ItemCode AND P03.PriceList = 3
LEFT JOIN ITM1 P04 ON P04.ItemCode = I.ItemCode AND P04.PriceList = 4
LEFT JOIN ITM1 P05 ON P05.ItemCode = I.ItemCode AND P05.PriceList = 5
WHERE
     I.frozenFor = 'N'
    AND W.WhsCode IN ('001','002','003','004')
    {whereExtra}
ORDER BY I.ItemCode, W.WhsCode";

            rs.DoQuery(sql);

            var warehouseNames = new Dictionary<string, string>
            {
                { "001", "Shaurimoyo Main" },
                { "002", "Kilwa Store" },
                { "003", "GODOWN" },
                { "004", "KISUTU Branch" }
            };

            var map = new Dictionary<string, ProductWithWarehouseDto>(StringComparer.OrdinalIgnoreCase);

            while (!rs.EoF)
            {
                string itemCode = rs.Fields.Item("ItemCode")?.Value?.ToString() ?? "";
                string itemName = rs.Fields.Item("ItemName")?.Value?.ToString() ?? "";
                string article = rs.Fields.Item("U_Article_No")?.Value?.ToString() ?? "";
                string mdl = rs.Fields.Item("U_MdlTEST")?.Value?.ToString() ?? "";
                string itemNm = rs.Fields.Item("U_Item_Name")?.Value?.ToString() ?? "";

                // LEFT JOINs mean a missing ITM1 row for a price list comes back as
                // DBNull/null — that's "not loaded", distinct from a genuine SAP price
                // of 0. Track which price lists actually had a row so callers never
                // mistake "no data" for "confirmed zero" and overwrite a cached price.
                object? raw01 = rs.Fields.Item("Price01")?.Value;
                object? raw02 = rs.Fields.Item("Price02")?.Value;
                object? raw03 = rs.Fields.Item("Price03")?.Value;
                object? raw04 = rs.Fields.Item("Price04")?.Value;
                object? raw05 = rs.Fields.Item("Price05")?.Value;

                bool loaded01 = raw01 != null && raw01 != DBNull.Value;
                bool loaded02 = raw02 != null && raw02 != DBNull.Value;
                bool loaded03 = raw03 != null && raw03 != DBNull.Value;
                bool loaded04 = raw04 != null && raw04 != DBNull.Value;
                bool loaded05 = raw05 != null && raw05 != DBNull.Value;

                decimal price01 = loaded01 ? Convert.ToDecimal(raw01) : 0;
                decimal price02 = loaded02 ? Convert.ToDecimal(raw02) : 0;
                decimal price03 = loaded03 ? Convert.ToDecimal(raw03) : 0;
                decimal price04 = loaded04 ? Convert.ToDecimal(raw04) : 0;
                decimal price05 = loaded05 ? Convert.ToDecimal(raw05) : 0;

                string whsCode = rs.Fields.Item("WhsCode")?.Value?.ToString() ?? "";
                decimal onHandW = Convert.ToDecimal(rs.Fields.Item("OnHandQty")?.Value ?? 0);

                if (!map.TryGetValue(itemCode, out var dto))
                {
                    dto = new ProductWithWarehouseDto
                    {
                        ItemCode = itemCode,
                        ItemName = itemName,
                        U_Article_No = article,
                        U_MdlTEST = mdl,
                        U_Item_Name = itemNm,
                        Price01 = price01,
                        Price02 = price02,
                        Price = price03,
                        Price04 = price04,
                        Price05 = price05,
                        Price01Loaded = loaded01,
                        Price02Loaded = loaded02,
                        PriceLoaded   = loaded03,
                        Price04Loaded = loaded04,
                        Price05Loaded = loaded05,
                        TotalOnHand = 0,
                        OnHand = 0,
                        Warehouses = new List<WarehouseStockDto>()
                    };
                    map[itemCode] = dto;
                }

                dto.Warehouses.Add(new WarehouseStockDto
                {
                    WarehouseCode = whsCode,
                    WarehouseName = warehouseNames.TryGetValue(whsCode, out var nm) ? nm : "Unknown",
                    OnHandQty = onHandW
                });

                rs.MoveNext();
            }

            foreach (var dto in map.Values)
            {
                var sum = dto.Warehouses.Sum(w => w.OnHandQty);
                dto.TotalOnHand = sum;
                dto.OnHand = sum;
            }

            return map.Values.ToList();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
        }
    }

    // ── Price list read ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current price and currency for a specific item + price list from SAP.
    /// Returns null if the item does not exist or the price list row is absent for that item.
    /// </summary>
    public SapCurrentPrice? ReadItemPrice(SAPbobsCOM.Company company, string itemCode, int priceListNum)
    {
        var items = (SAPbobsCOM.Items)company.GetBusinessObject(BoObjectTypes.oItems);
        try
        {
            if (items.GetByKey(itemCode) == false)
                return null; // item not found

            var priceLists = items.PriceList;
            for (int i = 0; i < priceLists.Count; i++)
            {
                priceLists.SetCurrentLine(i);
                if (priceLists.PriceList == priceListNum)
                {
                    return new SapCurrentPrice
                    {
                        Price    = (decimal)priceLists.Price,
                        Currency = priceLists.Currency ?? string.Empty,
                    };
                }
            }
            return null; // price list row not found for this item
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(items);
        }
    }

    // ── Price list write ──────────────────────────────────────────────────────

    /// <summary>
    /// Updates the price for a specific price list on an existing SAP item.
    /// Does NOT change currency — reads the current currency and preserves it.
    /// Returns (rc, sapError, actualPriceAfter, currency).
    /// rc == 0 means SAP accepted the change; rc != 0 is a definitive SAP rejection.
    /// actualPriceAfter is the price read back from SAP after Update() succeeds.
    /// Throws if item not found or price list row not found.
    /// </summary>
    public (int rc, string sapError, decimal? actualPriceAfter, string currency) UpdateItemPrice(
        SAPbobsCOM.Company company, string itemCode, int priceListNum, decimal newPrice)
    {
        var items = (SAPbobsCOM.Items)company.GetBusinessObject(BoObjectTypes.oItems);
        try
        {
            if (items.GetByKey(itemCode) == false)
                throw new InvalidOperationException($"SAP item '{itemCode}' not found.");

            // Find the target price list row by PriceList number (not by index).
            var priceLists = items.PriceList;
            int targetIndex = -1;
            for (int i = 0; i < priceLists.Count; i++)
            {
                priceLists.SetCurrentLine(i);
                if (priceLists.PriceList == priceListNum)
                {
                    targetIndex = i;
                    break;
                }
            }
            if (targetIndex == -1)
                throw new InvalidOperationException(
                    $"Price list {priceListNum} not found for item '{itemCode}' in SAP.");

            priceLists.SetCurrentLine(targetIndex);
            string currency = priceLists.Currency ?? string.Empty;
            priceLists.Price = (double)newPrice;

            int rc = items.Update();
            if (rc != 0)
                return (rc, company.GetLastErrorDescription(), null, currency);

            // Readback: re-fetch from SAP and verify the price was persisted.
            var verify = (SAPbobsCOM.Items)company.GetBusinessObject(BoObjectTypes.oItems);
            try
            {
                verify.GetByKey(itemCode);
                var vpl = verify.PriceList;
                for (int i = 0; i < vpl.Count; i++)
                {
                    vpl.SetCurrentLine(i);
                    if (vpl.PriceList == priceListNum)
                    {
                        decimal actual = (decimal)vpl.Price;
                        return (0, string.Empty, actual, currency);
                    }
                }
                return (0, string.Empty, null, currency); // readback row vanished — treat as mismatch upstream
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(verify);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(items);
        }
    }
}
