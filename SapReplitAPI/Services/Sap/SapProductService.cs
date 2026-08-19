using SAPbobsCOM;
using SapReplitAPI.Models;

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
    P03.Price AS Price03,
    P05.Price AS Price05,
    I.OnHand  AS TotalOnHand,
    W.WhsCode,
    W.OnHand  AS OnHandQty
FROM OITM I
JOIN OITW W     ON W.ItemCode = I.ItemCode
LEFT JOIN ITM1 P03 ON P03.ItemCode = I.ItemCode AND P03.PriceList = 3
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

                decimal price03 = Convert.ToDecimal(rs.Fields.Item("Price03")?.Value ?? 0);
                decimal price05 = Convert.ToDecimal(rs.Fields.Item("Price05")?.Value ?? 0);

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
                        Price = price03,
                        Price05 = price05,
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
}
