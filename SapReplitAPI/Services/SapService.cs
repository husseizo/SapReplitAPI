#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type
#pragma warning disable CS8601 // Possible null reference assignment
#pragma warning disable CS8602 // Dereference of a possibly null reference
#pragma warning disable CS8604 // Possible null argument
#pragma warning disable CA1416 // Possible null argument




using Microsoft.Extensions.Options;
using SAPbobsCOM;
using SapReplitAPI.DTOs.Dashboard;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CustomerModels;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.Payments;
using Serilog;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

public class SapService
{
    private readonly SapSettings _settings;
    private readonly ILogger<SapService> _logger;
    private SAPbobsCOM.Company? _company;

    public SapService(IOptions<SapSettings> settings, ILogger<SapService> logger)
    {
        _logger = logger;
        _settings = settings.Value;
        // ❌ Do NOT Connect() here
    }

    private SAPbobsCOM.Company CreateCompany()
    {
        return new SAPbobsCOM.Company
        {
            Server = _settings.Server,
            CompanyDB = _settings.CompanyDB,
            UserName = _settings.UserName,
            Password = _settings.Password,
            DbServerType = SAPbobsCOM.BoDataServerTypes.dst_MSSQL2016, // align with your SQL
            language = SAPbobsCOM.BoSuppLangs.ln_English,
            UseTrusted = false,
            LicenseServer = _settings.LicenseServer, // e.g. "WIN-G...:30000"
            SLDServer = _settings.SLDServer      // e.g. "WIN-G...:40000"
        };
    }

    private SAPbobsCOM.Company GetConnectedCompany()
    {
        if (_company != null && _company.Connected) return _company;

        var c = CreateCompany();
        var rc = c.Connect();
        if (rc != 0)
        {
            var err = c.GetLastErrorDescription();
            _logger.LogError("❌ SAP Connect failed: {Error}", err);
            throw new Exception("SAP Connection failed: " + err);
        }

        _company = c;
        return _company;
    }




    // Product management methods
    private readonly Dictionary<string, string> _warehouseNames = new()
{
    { "001", "Shaurimoyo Main" },
    { "002", "Store" },
    { "003", "Warehouse 3" },
    { "004", "Warehouse 4" }
};


  


    public List<ProductWithWarehouseDto> GetLiveProducts(DateTime? from = null, DateTime? to = null)
    {
        var _company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);

        bool isDelta = from.HasValue;

        // Full sync: only items with stock > 0.
        // Delta sync: catch both master-data edits (OITM.UpdateDate) AND stock movements
        // (OINM.DocDate). SAP only updates OITM.UpdateDate when item master data changes,
        // NOT when stock moves via transactions — so we must also check OINM.
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
    (I.validFor IN ('Y','N') OR I.frozenFor IN ('Y','N'))
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

            // ❌ REMOVE this — there is no "Price" column in the SELECT
            // decimal price = Convert.ToDecimal(rs.Fields.Item("Price")?.Value ?? 0);

            // ✅ Use the aliases you selected
            decimal price03 = Convert.ToDecimal(rs.Fields.Item("Price03")?.Value ?? 0);
            decimal price05 = Convert.ToDecimal(rs.Fields.Item("Price05")?.Value ?? 0);

            decimal totalOn = Convert.ToDecimal(rs.Fields.Item("TotalOnHand")?.Value ?? 0);
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
                    Price = price03,   // keep legacy "Price" as list 03
                    Price05 = price05,   // new list 05
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

        // compute totals from warehouses so 001..004 drive visibility
        foreach (var dto in map.Values)
        {
            var sum = dto.Warehouses.Sum(w => w.OnHandQty);
            dto.TotalOnHand = sum;
            dto.OnHand = sum;
        }

        return map.Values.ToList();
    }








    // Order management methods






    public int CreateOrder(CreateOrderDto dto)
    {
        var company = GetConnectedCompany();
        var order = (Documents)company.GetBusinessObject(BoObjectTypes.oOrders);

        // === Header Fields ===
        order.CardCode = dto.CardCode;
        order.DocDate = dto.DocDate;                                 // Posting Date
        order.TaxDate = dto.DocDate;                                 // Document Date
        order.DocDueDate = dto.DeliveryDate ?? dto.DocDate;          // Delivery Date
        order.DocCurrency = "TZS";                                   // Currency
        order.Series = 8;                                            // Series (from NNM1)
        order.BPL_IDAssignedToInvoice = 1;

        // Assign Sales Employee if provided
        if (dto.SlpCode.HasValue)
            order.SalesPersonCode = dto.SlpCode.Value;

        // === Set UDF on Header (Unique Rep ID) ===
        string uniqueRepId = "Rep" + Guid.NewGuid().ToString("N").Substring(0, 8);
        order.UserFields.Fields.Item("U_ReplitId").Value = uniqueRepId;

        // === Line Items ===
        foreach (var line in dto.Lines)
        {
            order.Lines.ItemCode = line.ItemCode;
            order.Lines.Quantity = line.Quantity;
            order.Lines.Price = (double)line.Price;
            order.Lines.VatGroup = "TZ"; // VAT Code (must exist in OVTG)
            order.Lines.WarehouseCode = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode;

            // Optional UDFs — only set if values are provided
            if (!string.IsNullOrWhiteSpace(line.Dscription))
                order.Lines.UserFields.Fields.Item("U_ItemName").Value = line.Dscription;

            if (line.U_Manufacturer != null)
                order.Lines.UserFields.Fields.Item("U_Manufacturer").Value = line.U_Manufacturer;

            order.Lines.Add();
        }

        // === Commit to SAP ===
        if (order.Add() != 0)
            throw new Exception("Failed to create order: " + company.GetLastErrorDescription());

        return int.Parse(company.GetNewObjectKey()); // Returns DocEntry
    }

    public bool UpdateOrder(UpdateOrderDto dto)
    {
        var company = GetConnectedCompany();
        Console.WriteLine($"🔄 Updating order {dto.DocEntry}...");

        var order = (Documents)_company.GetBusinessObject(BoObjectTypes.oOrders);
        if (!order.GetByKey(dto.DocEntry))
            throw new Exception($"Order {dto.DocEntry} not found.");

        if (order.DocumentStatus != BoStatus.bost_Open)
            throw new Exception("Only open orders can be edited.");

        // 🔒 Check if already delivered
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery($@"
        SELECT TOP 1 DocEntry 
        FROM DLN1 
        WHERE BaseEntry = {dto.DocEntry} AND BaseType = 17
    ");
        if (!rs.EoF)
            throw new Exception("This order has already been delivered and cannot be modified.");

        // 🧹 Clear old lines
        Console.WriteLine($"🧹 Removing {order.Lines.Count} existing lines...");
        for (int i = order.Lines.Count - 1; i >= 0; i--)
        {
            order.Lines.SetCurrentLine(i);
            order.Lines.Delete();
        }

        // ➕ Add updated lines
        Console.WriteLine($"➕ Adding {dto.UpdatedLines.Count} new lines...");
        foreach (var line in dto.UpdatedLines)
        {
            order.Lines.ItemCode = line.ItemCode;
            order.Lines.Quantity = line.Quantity;
            order.Lines.Price = (double)line.Price;
            order.Lines.VatGroup = "TZ"; // TODO: parameterize if needed
            order.Lines.WarehouseCode = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode;

            if (!string.IsNullOrWhiteSpace(line.Dscription))
                order.Lines.UserFields.Fields.Item("U_ItemName").Value = line.Dscription;

            if (!string.IsNullOrWhiteSpace(line.U_Manufacturer))
                order.Lines.UserFields.Fields.Item("U_Manufacturer").Value = line.U_Manufacturer;

            order.Lines.Add();
        }

        // 💾 Save update
        if (order.Update() != 0)
        {
            string err = _company.GetLastErrorDescription();
            Console.WriteLine($"❌ Update failed: {err}");
            throw new Exception("Failed to update order: " + err);
        }

        Console.WriteLine($"✅ Order {dto.DocEntry} updated successfully.");
        return true;
    }



    [SupportedOSPlatform("windows")]
    public List<OrderModel> GetOpenOrders(int? slpCode, string? customer, DateTime? fromDate, DateTime? toDate)
    {
        var orders = new List<OrderModel>();
        Recordset rsHeader = null;
        Recordset rsLines = null;

        try
        {
            var company = GetConnectedCompany();   // ✅ always ensures connected
            fromDate ??= new DateTime(2025, 1, 1);
            toDate ??= DateTime.Today;

            Console.WriteLine($"📅 Fetching open orders between {fromDate:yyyy-MM-dd} and {toDate:yyyy-MM-dd}");

            // 1️⃣ FETCH HEADERS
            string headerQuery = $@"
SELECT 
    T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName, T0.DocDate, T0.DocTotal,
    T0.SlpCode, ISNULL(T1.SlpName, '') AS SlpName
FROM ORDR T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE T0.DocStatus = 'O'
  AND T0.DocDate BETWEEN '{fromDate:yyyy-MM-dd}' AND '{toDate:yyyy-MM-dd}'";

            if (slpCode.HasValue)
            {
                headerQuery += $" AND T0.SlpCode = {slpCode.Value}";
                Console.WriteLine($"🔍 Filtering by salesperson code: {slpCode.Value}");
            }

            if (!string.IsNullOrWhiteSpace(customer))
            {
                string safeCustomer = customer.Replace("'", "''");
                headerQuery += $" AND T0.CardName LIKE '%{safeCustomer}%'";
                Console.WriteLine($"🔍 Filtering by customer: {customer}");
            }

            Console.WriteLine($"📄 Executing header query:\n{headerQuery}");

            rsHeader = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsHeader.DoQuery(headerQuery);

            if (rsHeader.RecordCount == 0)
            {
                Console.WriteLine("⚠️ No open orders found.");
                return orders;
            }

            var docEntries = new List<int>();
            var orderDict = new Dictionary<int, OrderModel>();

            while (!rsHeader.EoF)
            {
                int docEntry = Convert.ToInt32(rsHeader.Fields.Item("DocEntry").Value);
                string slpName = rsHeader.Fields.Item("SlpName").Value?.ToString() ?? "";

                var order = new OrderModel
                {
                    DocEntry = docEntry,
                    DocNum = Convert.ToInt32(rsHeader.Fields.Item("DocNum").Value),
                    CustomerCode = rsHeader.Fields.Item("CardCode").Value?.ToString(),
                    CustomerName = rsHeader.Fields.Item("CardName").Value?.ToString(),
                    DocDate = Convert.ToDateTime(rsHeader.Fields.Item("DocDate").Value),
                    OrderValue = Convert.ToDecimal(rsHeader.Fields.Item("DocTotal").Value),
                    Status = "Open",
                    SlpCode = Convert.ToInt32(rsHeader.Fields.Item("SlpCode").Value),
                    SlpName = slpName,
                    Lines = new List<OrderLineModel>()
                };

                orders.Add(order);
                orderDict[docEntry] = order;
                docEntries.Add(docEntry);

                rsHeader.MoveNext();
            }

            Console.WriteLine($"📦 Found {orders.Count} open orders. Fetching line items...");

            // 2️⃣ FETCH LINES
            string lineQuery = $@"
SELECT 
    T0.DocEntry, T0.ItemCode, T0.Dscription, T0.Quantity, T0.Price, T0.WhsCode,
    ISNULL(T0.U_ItemName, '') AS U_ItemName,
    ISNULL(T0.U_Manufacturer, '') AS U_Manufacturer
FROM RDR1 T0
WHERE T0.DocEntry IN ({string.Join(",", docEntries)})";

            Console.WriteLine($"📄 Executing line query:\n{lineQuery}");

            rsLines = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsLines.DoQuery(lineQuery);

            int lineCount = 0;

            while (!rsLines.EoF)
            {
                int docEntry = Convert.ToInt32(rsLines.Fields.Item("DocEntry").Value);

                if (orderDict.TryGetValue(docEntry, out var order))
                {
                    var line = new OrderLineModel
                    {
                        ItemCode = rsLines.Fields.Item("ItemCode").Value?.ToString(),
                        Dscription = rsLines.Fields.Item("Dscription").Value?.ToString(),
                        Quantity = Convert.ToInt32(rsLines.Fields.Item("Quantity").Value),
                        Price = Convert.ToDecimal(rsLines.Fields.Item("Price").Value),
                        WhsCode = rsLines.Fields.Item("WhsCode").Value?.ToString(),
                        U_ItemName = rsLines.Fields.Item("U_ItemName").Value?.ToString(),
                        U_Manufacturer = rsLines.Fields.Item("U_Manufacturer").Value?.ToString()
                    };

                    order.Lines.Add(line);
                    lineCount++;
                }

                rsLines.MoveNext();
            }

            Console.WriteLine($"✅ Loaded {lineCount} lines across {orders.Count} orders.");
            return orders;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GetOpenOrders] ❌ Failed to fetch batched open orders.");
            Console.WriteLine($"❌ Error while fetching open orders: {ex.Message}");
            return orders;
        }
        finally
        {
            if (rsHeader != null) Marshal.ReleaseComObject(rsHeader);
            if (rsLines != null) Marshal.ReleaseComObject(rsLines);
        }
    }


    [SupportedOSPlatform("windows")]
    public List<OrderModel> GetAllOrders(int? slpCode, string? customer, DateTime? fromDate, DateTime? toDate)
    {
        var orders = new List<OrderModel>();
        Recordset rsHeader = null;
        Recordset rsLines = null;

        try
        {
            var company = GetConnectedCompany();   // ✅ always ensures connected
            fromDate ??= new DateTime(2024, 1, 1);
            toDate ??= DateTime.Today;

            Console.WriteLine($"📅 Fetching ALL orders between {fromDate:yyyy-MM-dd} and {toDate:yyyy-MM-dd}");

            // 1️⃣ FETCH HEADERS (remove DocStatus filter)
            string headerQuery = $@"
SELECT 
    T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName, T0.DocDate, T0.DocTotal,
    T0.SlpCode, ISNULL(T1.SlpName, '') AS SlpName,
    T0.DocStatus, ISNULL(T0.CANCELED, 'N') AS Canceled
FROM ORDR T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE T0.DocDate BETWEEN '{fromDate:yyyy-MM-dd}' AND '{toDate:yyyy-MM-dd}'";

            if (slpCode.HasValue)
            {
                headerQuery += $" AND T0.SlpCode = {slpCode.Value}";
                Console.WriteLine($"🔍 Filtering by salesperson code: {slpCode.Value}");
            }

            if (!string.IsNullOrWhiteSpace(customer))
            {
                string safeCustomer = customer.Replace("'", "''");
                headerQuery += $" AND T0.CardName LIKE '%{safeCustomer}%'";
                Console.WriteLine($"🔍 Filtering by customer: {customer}");
            }

            Console.WriteLine($"📄 Executing header query:\n{headerQuery}");

            rsHeader = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsHeader.DoQuery(headerQuery);

            if (rsHeader.RecordCount == 0)
            {
                Console.WriteLine("⚠️ No orders found.");
                return orders;
            }

            var docEntries = new List<int>();
            var orderDict = new Dictionary<int, OrderModel>();

            while (!rsHeader.EoF)
            {
                int docEntry = Convert.ToInt32(rsHeader.Fields.Item("DocEntry").Value);
                string docStatus = rsHeader.Fields.Item("DocStatus").Value?.ToString() ?? "O";
                string canceled = rsHeader.Fields.Item("Canceled").Value?.ToString() ?? "N";

                string status = (canceled == "Y") ? "Cancelled"
                              : (docStatus == "C") ? "Delivered"
                              : "Open";

                var order = new OrderModel
                {
                    DocEntry = docEntry,
                    DocNum = Convert.ToInt32(rsHeader.Fields.Item("DocNum").Value),
                    CustomerCode = rsHeader.Fields.Item("CardCode").Value?.ToString(),
                    CustomerName = rsHeader.Fields.Item("CardName").Value?.ToString(),
                    DocDate = Convert.ToDateTime(rsHeader.Fields.Item("DocDate").Value),
                    OrderValue = Convert.ToDecimal(rsHeader.Fields.Item("DocTotal").Value),
                    Status = status,
                    SlpCode = Convert.ToInt32(rsHeader.Fields.Item("SlpCode").Value),
                    SlpName = rsHeader.Fields.Item("SlpName").Value?.ToString() ?? "",
                    Lines = new List<OrderLineModel>()
                };

                orders.Add(order);
                orderDict[docEntry] = order;
                docEntries.Add(docEntry);

                rsHeader.MoveNext();
            }

            Console.WriteLine($"📦 Found {orders.Count} orders. Fetching line items...");

            // 2️⃣ FETCH LINES
            string lineQuery = $@"
SELECT 
    T0.DocEntry, T0.ItemCode, T0.Dscription, T0.Quantity, T0.Price, T0.WhsCode,
    ISNULL(T0.U_ItemName, '') AS U_ItemName,
    ISNULL(T0.U_Manufacturer, '') AS U_Manufacturer
FROM RDR1 T0
WHERE T0.DocEntry IN ({string.Join(",", docEntries)})";

            Console.WriteLine($"📄 Executing line query:\n{lineQuery}");

            rsLines = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsLines.DoQuery(lineQuery);

            int lineCount = 0;

            while (!rsLines.EoF)
            {
                int docEntry = Convert.ToInt32(rsLines.Fields.Item("DocEntry").Value);

                if (orderDict.TryGetValue(docEntry, out var order))
                {
                    var line = new OrderLineModel
                    {
                        ItemCode = rsLines.Fields.Item("ItemCode").Value?.ToString(),
                        Dscription = rsLines.Fields.Item("Dscription").Value?.ToString(),
                        Quantity = Convert.ToInt32(rsLines.Fields.Item("Quantity").Value),
                        Price = Convert.ToDecimal(rsLines.Fields.Item("Price").Value),
                        WhsCode = rsLines.Fields.Item("WhsCode").Value?.ToString(),
                        U_ItemName = rsLines.Fields.Item("U_ItemName").Value?.ToString(),
                        U_Manufacturer = rsLines.Fields.Item("U_Manufacturer").Value?.ToString()
                    };

                    order.Lines.Add(line);
                    lineCount++;
                }

                rsLines.MoveNext();
            }

            Console.WriteLine($"✅ Loaded {lineCount} lines across {orders.Count} orders.");
            return orders;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GetAllOrders] ❌ Failed to fetch orders.");
            Console.WriteLine($"❌ Error while fetching orders: {ex.Message}");
            return orders;
        }
        finally
        {
            if (rsHeader != null) Marshal.ReleaseComObject(rsHeader);
            if (rsLines != null) Marshal.ReleaseComObject(rsLines);
        }
    }






    //
    // ✅ Helper: get default Sales Quotation series (ObjectCode '23')
    //    1) Prefer ONNM.DfltSeries (global default per object)
    //    2) Fallback to first unlocked NNM1 series for object 23
    // ✅ Helper: get default Sales Quotation series (ObjectCode = '23')
    private int GetDefaultQuotationSeries()
    {
        Recordset? rs = null;
        var company = GetConnectedCompany();
        try
        {
            rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);

            // 1) ONNM.DfltSeries
            rs.DoQuery(@"
            SELECT TOP 1 O.DfltSeries AS Series
            FROM ONNM O
            WHERE O.ObjectCode = '23' AND O.DfltSeries IS NOT NULL
        ");
            if (!rs.EoF && rs.Fields.Item("Series").Value != null)
                return Convert.ToInt32(rs.Fields.Item("Series").Value);

            // 2) First unlocked series from NNM1 (no DefSeries column!)
            rs.DoQuery(@"
            SELECT TOP 1 N.Series
            FROM NNM1 N
            WHERE N.ObjectCode = '23' AND ISNULL(N.Locked,'N') = 'N'
            ORDER BY N.Series ASC
        ");
            if (rs.EoF)
                throw new Exception("No active Series found for Sales Quotations (OQUT).");

            return Convert.ToInt32(rs.Fields.Item("Series").Value);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }



    public int CreateQuotation(CreateOrderDto dto)
    {
        if (dto == null) throw new ArgumentNullException(nameof(dto));
        if (string.IsNullOrWhiteSpace(dto.CardCode))
            throw new ArgumentException("CardCode is required.", nameof(dto.CardCode));
        if (dto.Lines == null || dto.Lines.Count == 0)
            throw new ArgumentException("At least one line is required.", nameof(dto.Lines));

        // ✅ Auto-fill defaults if missing
        dto.DocDate = dto.DocDate == default ? DateTime.Today : dto.DocDate;
        dto.DeliveryDate = dto.DeliveryDate ?? dto.DocDate;
        dto.DocCur = string.IsNullOrWhiteSpace(dto.DocCur) ? "TZS" : dto.DocCur.Trim();
        dto.Series = (dto.Series.HasValue && dto.Series.Value > 0)
                            ? dto.Series
                            : GetDefaultQuotationSeries();
        dto.SlpCode = dto.SlpCode ?? -1; // optional
        int branchId = 1; // static default

        Documents? quot = null;
        var company = GetConnectedCompany();
        try
        {
            quot = (Documents)_company.GetBusinessObject(BoObjectTypes.oQuotations);

            // Header
            quot.CardCode = dto.CardCode.Trim();
            quot.DocDate = dto.DocDate;
            quot.TaxDate = dto.DocDate;
            quot.DocDueDate = dto.DeliveryDate.Value;
            quot.DocCurrency = dto.DocCur;
            quot.Series = dto.Series.Value;
            quot.BPL_IDAssignedToInvoice = branchId;

            if (dto.SlpCode >= 0)
                quot.SalesPersonCode = dto.SlpCode.Value;

            // Lines
            foreach (var line in dto.Lines)
            {
                if (line == null) continue;
                if (string.IsNullOrWhiteSpace(line.ItemCode))
                    throw new ArgumentException("Each line requires ItemCode.");

                quot.Lines.ItemCode = line.ItemCode.Trim();
                quot.Lines.Quantity = line.Quantity <= 0 ? 1 : line.Quantity;
                quot.Lines.VatGroup = "TZ";
                quot.Lines.WarehouseCode = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode.Trim();
                quot.Lines.Add();
            }

            // Commit
            if (quot.Add() != 0)
                throw new Exception($"Failed to create quotation [{_company.GetLastErrorCode()}]: {_company.GetLastErrorDescription()}");

            return int.Parse(_company.GetNewObjectKey());
        }
        finally
        {
            if (quot != null) Marshal.ReleaseComObject(quot);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }









    // Customer management methods


    public PagedCustomerResultDto GetCustomersPaged(int page, int pageSize)
    {
        Recordset? rs = null;
        var company = GetConnectedCompany();
        try
        {
            rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
            int offset = (page - 1) * pageSize;

            rs.DoQuery("SELECT COUNT(*) AS Total FROM OCRD WHERE CardType = 'C'");
            int totalCount = Convert.ToInt32(rs.Fields.Item("Total").Value);

            rs.DoQuery($@"
SELECT * FROM (
    SELECT 
        T0.[CardCode], 
        T0.[CardName], 
        T0.[Balance], 
        /* VIN UDFs may not exist in every DB; if so SAP DI returns nulls – ok */
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
    LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode  -- ← left join so customers without SLP still return
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

                var dto = new CustomerDto
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
                    TotalSpent = GetCustomerTotalSpent(cardCode),
                    Addresses = GetCustomerAddresses(cardCode)
                };

                result.Customers.Add(dto);
                rs.MoveNext();
            }

            return result;
        }
        finally
        {
            if (rs != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
            rs = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }



   

    public List<CustomerAddressDto> GetCustomerAddresses(string cardCode)
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
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

   

    public decimal GetCustomerTotalSpent(string cardCode)
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery($@"
        SELECT ISNULL(SUM(DocTotal), 0) AS TotalSpent
        FROM OINV WHERE CardCode = '{cardCode}' AND CANCELED = 'N'
    ");

        return Convert.ToDecimal(rs.Fields.Item("TotalSpent").Value);
    }


    public string CreateCustomer(CreateCustomerDto dto)
    {
        // 🔍 Log SalesPerson info from DTO
        _logger.LogInformation("Creating customer with SlpCode: {code} or Name: {name}", dto.SlpCode, dto.SalesPersonName);

        // Auto-generate CardCode from OCRD table
        string generatedCode = GenerateNextCustomerCode();
        if (string.IsNullOrWhiteSpace(generatedCode))
            throw new Exception("Failed to generate CardCode");

        // Validate required fields
        if (string.IsNullOrWhiteSpace(dto.CardName))
            throw new Exception("CardName is required");

        // Create BP object
        var bp = (BusinessPartners)_company.GetBusinessObject(BoObjectTypes.oBusinessPartners);
        bp.CardType = BoCardTypes.cCustomer;
        bp.CardCode = generatedCode;
        bp.CardName = dto.CardName;

        // Assign phone number
        string phone = dto.Phone?.Trim() ?? "";
        bp.Phone1 = phone;
        bp.UserFields.Fields.Item("U_Phone").Value = phone;

        // Assign customer type
        bp.UserFields.Fields.Item("U_Customer_Type").Value = dto.CustomerType?.Trim() ?? "";

        // Validate and assign region
        bp.UserFields.Fields.Item("U_REGION").Value = ValidateRegion(dto.Region);

        // Vehicle Identification Numbers (optional UDFs)
        if (!string.IsNullOrWhiteSpace(dto.VIN1))
            bp.UserFields.Fields.Item("U_VIN1").Value = dto.VIN1.Trim();
        if (!string.IsNullOrWhiteSpace(dto.VIN2))
            bp.UserFields.Fields.Item("U_VIN2").Value = dto.VIN2.Trim();
        if (!string.IsNullOrWhiteSpace(dto.VIN3))
            bp.UserFields.Fields.Item("U_VIN3").Value = dto.VIN3.Trim();

        // 🎯 Assign SalesPerson
        if (dto.SlpCode > 0)
        {
            bp.SalesPersonCode = dto.SlpCode;
        }
        else if (!string.IsNullOrWhiteSpace(dto.SalesPersonName))
        {
            var slpCode = GetSlpCodeByName(dto.SalesPersonName);
            if (slpCode.HasValue)
            {
                bp.SalesPersonCode = slpCode.Value;
            }
            else
            {
                throw new Exception($"Salesperson '{dto.SalesPersonName}' not found.");
            }
        }
        else
        {
            throw new Exception("Salesperson information is missing.");
        }

        // Save address (optional)
        if (!string.IsNullOrWhiteSpace(dto.Address))
        {
            bp.Address = dto.Address;
            bp.Addresses.AddressType = BoAddressType.bo_BillTo;
            bp.Addresses.AddressName = "Billing";
            bp.Addresses.Street = dto.Address;
            bp.Addresses.Add();
        }

        // Attempt to add the customer to SAP
        if (bp.Add() != 0)
        {
            string error = _company.GetLastErrorDescription();
            throw new Exception("Failed to create customer: " + error);
        }

        return generatedCode;
    }



    private string GenerateNextCustomerCode()
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);

        // Get the highest numeric part of CardCode that starts with 'CUS'
        rs.DoQuery(@"
        SELECT ISNULL(MAX(CAST(SUBSTRING(CardCode, 4, LEN(CardCode)) AS INT)), 0) AS MaxCode
        FROM OCRD 
        WHERE ISNUMERIC(SUBSTRING(CardCode, 4, LEN(CardCode))) = 1 
          AND CardCode LIKE 'CUS%'");

        int maxNum = Convert.ToInt32(rs.Fields.Item("MaxCode").Value);
        int nextNum = maxNum + 1;

        return $"CUS{nextNum.ToString("D6")}";
    }

    private string ValidateRegion(string inputRegion)
    {
        var valid = new[]
        {
        "ARUSHA", "DAR ES SALAAM", "DODOMA", "GEITA", "IRINGA", "KAGERA", "KATAVI",
        "KIGOMA", "KILIMANJARO", "LINDI", "MANYARA", "MARA", "MBEYA", "MOROGORO",
        "MTWARA", "MWANZA", "NJOMBE", "PEMBA", "PWANI", "RUKWA", "RUVUMA", "SHINYANGA",
        "SIMIYU", "SINGIDA", "SONGWE", "TABORA", "TANGA", "UNGUJA"
    };

        if (string.IsNullOrWhiteSpace(inputRegion)) return "CHOSE REGION";

        string upper = inputRegion.Trim().ToUpper();
        if (valid.Contains(upper)) return upper;

        throw new Exception($"Invalid region '{inputRegion}'. Allowed: {string.Join(", ", valid)}");
    }

   

    private int? GetSlpCodeByName(string slpName)
    {
        if (string.IsNullOrWhiteSpace(slpName))
            return null;

        var company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery($"SELECT SlpCode FROM OSLP WHERE SlpName = '{slpName.Replace("'", "''")}'");

        if (!rs.EoF)
            return Convert.ToInt32(rs.Fields.Item("SlpCode").Value);

        return null;
    }








    // Invoice management methods

    public List<InvoiceDto> GetInvoices(string? status = null, string? customer = null, DateTime? from = null, DateTime? to = null, bool isDelta = false)
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);

        var filters = new List<string> { "1 = 1" };

        // Set default 'from' date to Jan 1, 2024 if not provided
        if (!from.HasValue)
            from = new DateTime(2024, 1, 1);

        if (!string.IsNullOrWhiteSpace(customer))
            filters.Add($"(T0.CardName LIKE '%{customer}%' OR T0.CardCode LIKE '%{customer}%')");

        if (!string.IsNullOrWhiteSpace(status))
            filters.Add($"T0.DocStatus = '{status}'");

        if (from.HasValue)
        {
            if (isDelta)
                // For delta syncs: catch invoices created OR updated (e.g. paid) since last sync.
                // UpdateDate captures DocStatus/PaidToDate changes on old invoices that DocDate alone would miss.
                filters.Add($"(T0.DocDate >= '{from.Value:yyyy-MM-dd}' OR T0.UpdateDate >= '{from.Value:yyyy-MM-dd}')");
            else
                filters.Add($"T0.DocDate >= '{from.Value:yyyy-MM-dd}'");
        }

        if (to.HasValue)
            filters.Add($"T0.DocDate <= '{to.Value:yyyy-MM-dd}'");

        // 🧾 STEP 1: Build and log the SQL
        string headerQuery = $@"
SELECT 
    T0.DocEntry, T0.DocNum, T0.DocDate, T0.DocStatus, T0.CANCELED,
    T0.CardCode, T0.CardName, T0.DocTotal, T0.PaidToDate,
    (T0.DocTotal - T0.PaidToDate) AS BalanceDue,
    DATEDIFF(DAY, T0.DocDueDate, GETDATE()) AS DaysOverdue,
    T1.SlpCode AS SalesEmployeeCode,
    T1.SlpName AS SalesEmployeeName
FROM OINV T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE {string.Join(" AND ", filters)}
ORDER BY T0.DocDate DESC";

        Console.WriteLine("📤 Executing SAP invoice header query:");
        Console.WriteLine(headerQuery);

        rs.DoQuery(headerQuery);

        var results = new List<InvoiceDto>();
        var docEntryList = new List<int>();

        while (!rs.EoF)
        {
            var docEntry = Convert.ToInt32(rs.Fields.Item("DocEntry").Value);
            docEntryList.Add(docEntry);

            string canceledValue = Convert.ToString(rs.Fields.Item("Canceled").Value);
            string canceledLabel = canceledValue switch
            {
                "N" => "Not Canceled",
                "Y" => "Canceled",
                "C" => "Cancellation",
                _ => "Unknown"
            };

            results.Add(new InvoiceDto
            {
                DocEntry = docEntry,
                DocNum = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                DocDate = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                Status = Convert.ToString(rs.Fields.Item("DocStatus").Value),
                Canceled = canceledLabel,
                CardCode = Convert.ToString(rs.Fields.Item("CardCode").Value),
                CardName = Convert.ToString(rs.Fields.Item("CardName").Value),
                DocTotal = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                PaidToDate = Convert.ToDecimal(rs.Fields.Item("PaidToDate").Value),
                BalanceDue = Convert.ToDecimal(rs.Fields.Item("BalanceDue").Value),
                SalesEmployeeCode = Convert.ToInt32(rs.Fields.Item("SalesEmployeeCode").Value),
                SalesEmployeeName = Convert.ToString(rs.Fields.Item("SalesEmployeeName").Value),
                DaysOverdue = Convert.ToInt32(rs.Fields.Item("DaysOverdue").Value),
                Lines = new List<InvoiceLineDto>()
            });

            rs.MoveNext();
        }

        Console.WriteLine($"✅ Retrieved {results.Count} invoice headers.");

        if (docEntryList.Count == 0)
        {
            Console.WriteLine("⚠️ No invoices found. Skipping line query.");
            return results;
        }

        // 📦 STEP 2: Invoice lines
        var rsLines = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
        string joinedDocEntries = string.Join(",", docEntryList);

        string lineQuery = $@"
SELECT 
    T2.DocEntry, T2.LineNum, T2.ItemCode, T2.Dscription,
    T2.Quantity, T2.Price, T2.LineTotal,
    T3.U_Item_Name, T3.U_MdlTEST
FROM INV1 T2
LEFT JOIN OITM T3 ON T2.ItemCode = T3.ItemCode
WHERE T2.DocEntry IN ({joinedDocEntries})";

        Console.WriteLine("📤 Executing SAP invoice line query:");
        Console.WriteLine(lineQuery);

        rsLines.DoQuery(lineQuery);

        var lineMap = results.ToDictionary(r => r.DocEntry);
        int lineCount = 0;

        while (!rsLines.EoF)
        {
            int docEntry = Convert.ToInt32(rsLines.Fields.Item("DocEntry").Value);
            if (lineMap.TryGetValue(docEntry, out var invoice))
            {
                invoice.Lines.Add(new InvoiceLineDto
                {
                    LineNum = Convert.ToInt32(rsLines.Fields.Item("LineNum").Value),
                    ItemCode = rsLines.Fields.Item("ItemCode").Value.ToString(),
                    Dscription = rsLines.Fields.Item("Dscription").Value.ToString(),
                    Quantity = Convert.ToDecimal(rsLines.Fields.Item("Quantity").Value),
                    Price = Convert.ToDecimal(rsLines.Fields.Item("Price").Value),
                    LineTotal = Convert.ToDecimal(rsLines.Fields.Item("LineTotal").Value),
                    U_Item_Name = rsLines.Fields.Item("U_Item_Name").Value?.ToString(),
                    U_MdlTEST = rsLines.Fields.Item("U_MdlTEST").Value?.ToString()
                });

                lineCount++;
            }

            rsLines.MoveNext();
        }

        Console.WriteLine($"📦 Retrieved {lineCount} invoice lines.");
        return results;
    }

    public List<InvoicePaymentDto> GetInvoicePayments(
     int? docNum = null,
     DateTime? from = null,
     DateTime? to = null,
     int page = 1,
     int pageSize = 50)
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);

        string filter = docNum.HasValue ? $"AND OINV.DocNum = {docNum.Value}" : "";

        if (from.HasValue)
            filter += $" AND ORCT.DocDate >= '{from.Value:yyyy-MM-dd HH:mm:ss}'";

        if (to.HasValue)
            filter += $" AND ORCT.DocDate <= '{to.Value:yyyy-MM-dd HH:mm:ss}'";

        int offset = (page - 1) * pageSize;

        string query = $@"
WITH PaymentsCTE AS (
    SELECT DISTINCT TOP {pageSize + offset}
        OINV.DocEntry AS InvoiceDocEntry,
        ORCT.DocEntry AS PaymentDocEntry,
        ORCT.DocNum AS PaymentNumber,
        OINV.DocNum AS InvoiceDocNum,
        ORCT.DocDate AS PaymentDate,
        ORCT.CardCode,
        ORCT.CardName,
        RCT2.SumApplied AS AmountApplied,
        ORCT.TrsfrSum AS BankTransferAmount,
        ORCT.TrsfrRef AS BankTransferReference,
        JDT1.Account AS DebitAccountCode,
        OACT.AcctName AS DebitAccountName,
        OSLP.SlpCode AS SalesEmployeeCode,
        OSLP.SlpName AS SalesEmployeeName,
        ROW_NUMBER() OVER (ORDER BY ORCT.DocDate DESC) AS RowNum
    FROM ORCT
    INNER JOIN RCT2 ON RCT2.DocNum = ORCT.DocEntry AND RCT2.InvType = 13
    INNER JOIN OINV ON OINV.DocEntry = RCT2.DocEntry
    LEFT JOIN JDT1 ON JDT1.TransId = ORCT.TransId AND JDT1.Debit > 0
    LEFT JOIN OACT ON JDT1.Account = OACT.AcctCode
    LEFT JOIN OSLP ON OINV.SlpCode = OSLP.SlpCode
    WHERE 1 = 1 {filter}
)
SELECT *
FROM PaymentsCTE
WHERE RowNum > {offset}
ORDER BY PaymentDate DESC";

        Console.WriteLine("📤 Executing SAP payment query:");
        Console.WriteLine($"🔢 Page: {page}, PageSize: {pageSize}, Offset: {offset}");
        if (docNum.HasValue)
            Console.WriteLine($"📄 Filtering for Invoice DocNum = {docNum.Value}");
        Console.WriteLine("📜 Final SQL:");
        Console.WriteLine(query);

        rs.DoQuery(query);

        var results = new List<InvoicePaymentDto>();
        int rowCount = 0;

        while (!rs.EoF)
        {
            results.Add(new InvoicePaymentDto
            {
                DocEntry = Convert.ToInt32(rs.Fields.Item("InvoiceDocEntry").Value),
                PaymentDocEntry = Convert.ToInt32(rs.Fields.Item("PaymentDocEntry").Value),
                PaymentNumber = Convert.ToInt32(rs.Fields.Item("PaymentNumber").Value),
                PaymentDate = Convert.ToDateTime(rs.Fields.Item("PaymentDate").Value),
                CardCode = rs.Fields.Item("CardCode").Value?.ToString(),
                CardName = rs.Fields.Item("CardName").Value?.ToString(),
                AmountApplied = Convert.ToDecimal(rs.Fields.Item("AmountApplied").Value),
                BankTransferAmount = Convert.ToDecimal(rs.Fields.Item("BankTransferAmount").Value),
                BankTransferReference = rs.Fields.Item("BankTransferReference").Value?.ToString(),
                DebitAccountCode = rs.Fields.Item("DebitAccountCode").Value?.ToString(),
                DebitAccountName = rs.Fields.Item("DebitAccountName").Value?.ToString(),
                SalesEmployeeCode = rs.Fields.Item("SalesEmployeeCode").Value?.ToString(),
                SalesEmployeeName = rs.Fields.Item("SalesEmployeeName").Value?.ToString(),
                InvoiceDocNum = Convert.ToInt32(rs.Fields.Item("InvoiceDocNum").Value),
            });

            rs.MoveNext();
            rowCount++;
        }

        Console.WriteLine($"✅ Retrieved {rowCount} payment rows.");
        return results;
    }





    public async Task<List<DetailedInvoiceReportRow>> GetDetailedInvoiceStatusReportAsync(string salesName, DateTime docDate)
    {
        return await Task.Run(() =>
        {
            var result = new List<DetailedInvoiceReportRow>();
            var company = GetConnectedCompany();   // ✅ always ensures connected

            string docDateFormatted = docDate.ToString("yyyy-MM-dd");

            string query = $@"
WITH CTE AS (
    SELECT DISTINCT
        T3.[SlpName] AS SALESNAME, 
        T0.DOCDATE AS [DATE],
        CASE 
            WHEN T2.DOCTOTAL >= 0.00 THEN T2.DOCDATE 
        END AS PaidDate,
        T0.cardname AS CUSTOMER,
        T0.docnum AS INVOICE,
        T0.U_ReinvoicedFrom AS ReinvoicedFrom,
        CASE 
            WHEN T0.DOCSTATUS = 'O' THEN 'Open Invoice'
            WHEN T0.DOCSTATUS = 'C' THEN 'Closed Invoice'
        END AS InvoiceStatus,
        CASE 
            WHEN T0.DOCSTATUS = 'C' AND T0.CANCELED = 'n' THEN T0.DOCTOTAL
            ELSE 0.00 
        END AS CashSales,
        CASE 
            WHEN T0.DOCSTATUS = 'O' AND T0.CANCELED = 'n' THEN T0.DOCTOTAL
            ELSE 0.00
        END AS CreditSales,
        CASE 
            WHEN T0.DOCSTATUS = 'o' AND T0.CANCELED IN ('c', 'y') THEN T0.DOCTOTAL
            WHEN T0.DOCSTATUS = 'c' AND T0.CANCELED IN ('c', 'y') THEN T0.DOCTOTAL
            ELSE 0.00
        END AS ReturnedSales,
        CASE 
            WHEN T0.U_ReinvoicedFrom IS NOT NULL AND T0.U_ReinvoicedFrom <> '' AND T0.DocStatus = 'C' THEN 
                'Amount Paid on ' + CAST(T0.DocNum AS NVARCHAR) + ' = ' + 
                ISNULL(FORMAT(TPaidThis.PaidAmount, 'N2'), '0.00') + 
                ' | Returned Amount from ' + T0.U_ReinvoicedFrom + ' = ' + 
                ISNULL(FORMAT(INV_OLD.DocTotal - ISNULL(RINV.ReinvoicedTotal, 0), 'N2'), '0.00')
            WHEN T0.U_ReinvoicedFrom IS NOT NULL AND T0.U_ReinvoicedFrom <> '' AND T0.DocStatus = 'O' THEN 
                'Amount Not Paid And Reinvoiced | Returned Amount  From ' + T0.U_ReinvoicedFrom + ' = ' + 
                ISNULL(FORMAT(INV_OLD.DocTotal - ISNULL(RINV.ReinvoicedTotal, 0), 'N2'), '0.00')
            WHEN T0.DOCSTATUS = 'O' AND T0.DocTotal = 0.00 THEN 
                'Amount Not Paid'
            WHEN T0.DOCSTATUS = 'C' THEN 
                'Amount Paid' 
            ELSE 'Amount Not Paid'
        END AS PaymentsStatus,
        CASE 
            WHEN T0.CANCELED = 'N' THEN 'Not Canceled' 
            WHEN T0.CANCELED = 'y' THEN 'Yes Canceled' 
            ELSE 'Cancellation' 
        END AS CancellationStatus
    FROM 
        [dbo].[OINV] T0 
    LEFT JOIN [dbo].[RCT2] T1 ON T1.[DocEntry] = T0.[DocEntry] AND T1.InvType = 13
    LEFT JOIN [dbo].[ORCT] T2 ON T2.[DocNum] = T1.[DocNum]
    LEFT JOIN [dbo].[OSLP] T3 ON T0.SLPCODE = T3.SLPCODE 
    INNER JOIN [dbo].[INV1] T4 ON T0.[DocEntry] = T4.[DocEntry] 
    LEFT JOIN [dbo].[OINV] INV_OLD ON INV_OLD.DocNum = TRY_CAST(REPLACE(T0.U_ReinvoicedFrom, 'INV', '') AS INT)
    LEFT JOIN (
        SELECT T1.DocEntry, SUM(ISNULL(T1.SumApplied, 0)) AS PaidAmount
        FROM RCT2 T1
        WHERE T1.InvType = 13
        GROUP BY T1.DocEntry
    ) AS TPaidThis ON TPaidThis.DocEntry = T0.DocEntry
    LEFT JOIN (
        SELECT 
            REPLACE(U_ReinvoicedFrom, 'INV', '') AS RefDocNum,
            SUM(Doctotal) AS ReinvoicedTotal
        FROM OINV
        WHERE ISNULL(U_ReinvoicedFrom, '') <> ''
        GROUP BY REPLACE(U_ReinvoicedFrom, 'INV', '')
    ) AS RINV ON RINV.RefDocNum = CAST(TRY_CAST(REPLACE(T0.U_ReinvoicedFrom, 'INV', '') AS INT) AS NVARCHAR)
    WHERE 
        T3.[SlpName] = '{salesName}' AND 
        CAST(T0.[DocDate] AS DATE) = '{docDateFormatted}' AND 
        T0.cardCODE <> 'CUS000625'
)

SELECT 
    SALESNAME, [DATE] AS PostingDate, INVOICE AS InvoiceNo, ReinvoicedFrom, InvoiceStatus, 
    PaidDate, CUSTOMER, CashSales, CreditSales, ReturnedSales, PaymentsStatus, CancellationStatus
FROM CTE
";

            SAPbobsCOM.Recordset rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.BoRecordset);
            rs.DoQuery(query);

            while (!rs.EoF)
            {
                DateTime pd;
                DateTime paid;

                var row = new DetailedInvoiceReportRow
                {
                    SalesName = rs.Fields.Item("SALESNAME").Value.ToString(),
                    PostingDate = DateTime.TryParse(rs.Fields.Item("PostingDate").Value.ToString(), out pd) ? pd : (DateTime?)null,
                    InvoiceNo = rs.Fields.Item("InvoiceNo").Value.ToString(),
                    ReinvoicedFrom = rs.Fields.Item("ReinvoicedFrom").Value.ToString(),
                    InvoiceStatus = rs.Fields.Item("InvoiceStatus").Value.ToString(),
                    PaidDate = DateTime.TryParse(rs.Fields.Item("PaidDate").Value.ToString(), out paid) ? paid : (DateTime?)null,
                    Customer = rs.Fields.Item("Customer").Value.ToString(),
                    CashSales = Convert.ToDecimal(rs.Fields.Item("CashSales").Value),
                    CreditSales = Convert.ToDecimal(rs.Fields.Item("CreditSales").Value),
                    ReturnedCashInvoice = Convert.ToDecimal(rs.Fields.Item("ReturnedSales").Value),
                    PaymentsStatus = rs.Fields.Item("PaymentsStatus").Value.ToString(),
                    CancellationStatus = rs.Fields.Item("CancellationStatus").Value.ToString(),
                    SlpCode = GetSalesEmployeeCodeByName(company, salesName)
                };

                result.Add(row);
                rs.MoveNext();
            }

            return result;
        });
    }



    private int GetSalesEmployeeCodeByName(SAPbobsCOM.Company company, string salesName)
    {
        var rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.BoRecordset);
        rs.DoQuery($@"SELECT SlpCode FROM OSLP WHERE SlpName = '{salesName.Replace("'", "''")}'");

        if (!rs.EoF)
            return Convert.ToInt32(rs.Fields.Item("SlpCode").Value);

        return -1;
    }
}