using SAPbobsCOM;
using SapReplitAPI.Models.CustomerModels;

public class SapCustomerService
{
    public PagedCustomerResultDto GetCustomersPaged(SAPbobsCOM.Company company, int page, int pageSize)
    {
        Recordset? rs = null;

        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            int offset = (page - 1) * pageSize;

            rs.DoQuery("SELECT COUNT(*) AS Total FROM OCRD WHERE CardType = 'C'");
            int totalCount = Convert.ToInt32(rs.Fields.Item("Total").Value);

            rs.DoQuery($@"
SELECT * FROM (
    SELECT 
        T0.[CardCode], 
        T0.[CardName], 
        T0.[Balance], 
        T0.[U_VIN1], 
        T0.[U_VIN2], 
        T0.[U_VIN3],
        T0.[U_REGION], 
        T0.[U_Phone], 
        T0.[U_Customer_Type], 
        T0.[SlpCode],
        T1.[SlpName],
        ROW_NUMBER() OVER (ORDER BY T0.[CardName]) AS RowNum
    FROM OCRD T0  
    LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
    WHERE T0.[CardType] = 'C'
) AS Sub
WHERE RowNum > {offset} AND RowNum <= {offset + pageSize}
ORDER BY RowNum
");

            var result = new PagedCustomerResultDto
            {
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                Customers = new List<CustomerDto>()
            };

            while (!rs.EoF)
            {
                string GetStr(string name) => rs.Fields.Item(name).Value?.ToString() ?? "";
                decimal GetDec(string name)
                {
                    var v = rs.Fields.Item(name).Value;
                    if (v == null || v is DBNull) return 0m;
                    return Convert.ToDecimal(v);
                }
                int? GetIntOrNull(string name)
                {
                    var v = rs.Fields.Item(name).Value;
                    if (v == null || v is DBNull) return null;
                    return Convert.ToInt32(v);
                }

                var cardCode = GetStr("CardCode");

                result.Customers.Add(new CustomerDto
                {
                    CardCode = cardCode,
                    CardName = GetStr("CardName"),
                    Balance = GetDec("Balance"),
                    Region = GetStr("U_REGION"),
                    Phone = GetStr("U_Phone"),
                    CustomerType = GetStr("U_Customer_Type"),
                    VIN1 = GetStr("U_VIN1"),
                    VIN2 = GetStr("U_VIN2"),
                    VIN3 = GetStr("U_VIN3"),
                    SalesPersonName = GetStr("SlpName"),
                    SalesPersonCode = GetIntOrNull("SlpCode"),
                    TotalSpent = GetCustomerTotalSpent(company, cardCode),
                    Addresses = GetCustomerAddresses(company, cardCode)
                });

                rs.MoveNext();
            }

            return result;
        }
        finally
        {
            if (rs != null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private List<CustomerAddressDto> GetCustomerAddresses(SAPbobsCOM.Company company, string cardCode)
    {
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

        try
        {
            rs.DoQuery($@"
        SELECT Address, City, AdresType FROM CRD1
        WHERE CardCode = '{cardCode}'
    ");

            var list = new List<CustomerAddressDto>();
            while (!rs.EoF)
            {
                list.Add(new CustomerAddressDto
                {
                    Address = rs.Fields.Item("Address").Value.ToString(),
                    City = rs.Fields.Item("City").Value.ToString(),
                    AddressType = rs.Fields.Item("AdresType").Value.ToString()
                });
                rs.MoveNext();
            }

            return list;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
        }
    }

    private decimal GetCustomerTotalSpent(SAPbobsCOM.Company company, string cardCode)
    {
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

        try
        {
            rs.DoQuery($@"
        SELECT ISNULL(SUM(DocTotal), 0) AS TotalSpent
        FROM OINV WHERE CardCode = '{cardCode}' AND CANCELED = 'N'
    ");

            return Convert.ToDecimal(rs.Fields.Item("TotalSpent").Value);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
        }
    }
}
