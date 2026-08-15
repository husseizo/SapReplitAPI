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
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Invoicing;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services;
using Serilog;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

public class SapService
{
    private readonly SapSettings _settings;
    private readonly ILogger<SapService> _logger;
    private readonly SapProductService _productService;
    private readonly SapCustomerService _customerService;
    private readonly SapInvoiceService _invoiceService;
    private readonly InvoiceLifecycleStatusService _invoiceLifecycleStatusService;
    private readonly PaymentSettings _paymentSettings;
    private SAPbobsCOM.Company? _company;

    public SapService(
        IOptions<SapSettings> settings,
        IOptions<PaymentSettings> paymentSettings,
        ILogger<SapService> logger,
        SapProductService productService,
        SapCustomerService customerService,
        SapInvoiceService invoiceService,
        InvoiceLifecycleStatusService invoiceLifecycleStatusService)
    {
        _logger = logger;
        _settings = settings.Value;
        _paymentSettings = paymentSettings.Value;
        _productService = productService;
        _customerService = customerService;
        _invoiceService = invoiceService;
        _invoiceLifecycleStatusService = invoiceLifecycleStatusService;
        // ❌ Do NOT Connect() here
        _logger.LogInformation("Payment config: AdvanceAccount={Acct}, DefaultBranchId={Bpl}",
            _paymentSettings.AdvanceCustomerPayments, _paymentSettings.DefaultBranchId);
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
        if (_company != null)
        {
            try
            {
                if (_company.Connected) return _company;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // COM object is corrupted (e.g. SAP server crash) — force reconnect.
                _logger.LogWarning(ex, "⚠️ SAP Company COM object bad state; forcing reconnect.");
                try { Marshal.ReleaseComObject(_company); } catch { }
                _company = null;
            }
        }

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
        var company = GetConnectedCompany();
        return _productService.GetLiveProducts(company, from, to);
    }








    // Order management methods






    public int CreateOrder(CreateOrderDto dto, string? replitId = null)
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

        // === Idempotency key — use caller-supplied ID or generate one ===
        replitId ??= "OR-" + Guid.NewGuid().ToString("N")[..12].ToUpper();
        order.UserFields.Fields.Item("U_ReplitId").Value = replitId;

        // === Pre-fetch OITM — ONE query for all lines, O(1) lookup per line ===
        var oitm = QueryOitm(company, dto.Lines.Select(l => l.ItemCode));

        // === Line Items ===
        int lineIdx = 0;
        foreach (var line in dto.Lines)
        {
            oitm.TryGetValue(line.ItemCode, out var info);

            // ItemDescription: U_Item_Name / U_MdlTEST / ItemName — all from OITM
            var description = BuildDescription(info.ItemName, info.Model, info.SapName, line.ItemCode);

            Console.WriteLine($"[CreateOrder] Line {lineIdx} ({line.ItemCode}): OITM='{info.ItemName}/{info.Model}/{info.SapName}' -> Dscription='{description}'");

            order.Lines.ItemCode        = line.ItemCode;
            order.Lines.Quantity        = line.Quantity;
            order.Lines.Price           = (double)line.Price;
            order.Lines.VatGroup        = "TZ";
            order.Lines.WarehouseCode   = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode;
            order.Lines.ItemDescription = description;

            if (!string.IsNullOrWhiteSpace(info.ItemName))
                order.Lines.UserFields.Fields.Item("U_ItemName").Value = info.ItemName;

            if (!string.IsNullOrWhiteSpace(line.U_Manufacturer))
                order.Lines.UserFields.Fields.Item("U_Manufacturer").Value = line.U_Manufacturer;

            order.Lines.Add();
            lineIdx++;
        }

        // === Commit to SAP ===
        var addRc = order.Add();
        if (addRc != 0)
        {
            var sapErr = company.GetLastErrorDescription();
            _logger.LogError("❌ [CreateOrder] SAP order.Add() rc={Rc} error='{SapErr}'", addRc, sapErr);
            throw new Exception("Failed to create order: " + sapErr);
        }

        return int.Parse(company.GetNewObjectKey()); // Returns DocEntry
    }

    // Builds description from OITM: "U_Item_Name/U_MdlTEST/ItemName" — skips empty parts.
    // Falls back to itemCode when all OITM fields are empty.
    private static string BuildDescription(string? uItemName, string? model, string? sapItemName, string itemCode)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(uItemName))   parts.Add(uItemName.Trim());
        if (!string.IsNullOrWhiteSpace(model))        parts.Add(model.Trim());
        if (!string.IsNullOrWhiteSpace(sapItemName))  parts.Add(sapItemName.Trim());
        return parts.Count > 0 ? string.Join("/", parts) : itemCode;
    }

    // One Recordset query for all item codes → Dictionary for O(1) lookup per line.
    private static Dictionary<string, (string? ItemName, string? Model, string? SapName)> QueryOitm(
        Company company, IEnumerable<string> itemCodes)
    {
        var result = new Dictionary<string, (string? ItemName, string? Model, string? SapName)>(StringComparer.OrdinalIgnoreCase);
        var distinct = itemCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        if (distinct.Count == 0) return result;

        var inClause = string.Join(",", distinct.Select(c => $"'{c.Replace("'", "''")}'"));
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
        try
        {
            rs.DoQuery($"SELECT ItemCode, ItemName, U_Item_Name, U_MdlTEST FROM OITM WHERE ItemCode IN ({inClause})");
            while (!rs.EoF)
            {
                string code      = rs.Fields.Item("ItemCode").Value?.ToString() ?? "";
                string? sapName  = rs.Fields.Item("ItemName").Value?.ToString();
                string? itemName = rs.Fields.Item("U_Item_Name").Value?.ToString();
                string? model    = rs.Fields.Item("U_MdlTEST").Value?.ToString();
                result[code] = (itemName, model, sapName);
                rs.MoveNext();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
        return result;
    }

    /// <summary>
    /// Checks whether an order with the given ReplitId already exists in SAP (ORDR.U_ReplitId).
    /// Returns the DocEntry if found, or null if not. Used for idempotent retry.
    /// </summary>
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

            // Pre-fetch OITM — ONE query for all lines
            var oitm = QueryOitm(company, dto.Lines.Where(l => l != null).Select(l => l.ItemCode));

            // Lines
            foreach (var line in dto.Lines)
            {
                if (line == null) continue;
                if (string.IsNullOrWhiteSpace(line.ItemCode))
                    throw new ArgumentException("Each line requires ItemCode.");

                oitm.TryGetValue(line.ItemCode, out var info);

                quot.Lines.ItemCode        = line.ItemCode.Trim();
                quot.Lines.Quantity        = line.Quantity <= 0 ? 1 : line.Quantity;
                quot.Lines.VatGroup        = "TZ";
                quot.Lines.WarehouseCode   = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode.Trim();
                quot.Lines.ItemDescription = BuildDescription(info.ItemName, info.Model, info.SapName, line.ItemCode);
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
        var company = GetConnectedCompany();
        return _customerService.GetCustomersPaged(company, page, pageSize);
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

        // Validate and assign region — prefer explicit Region, fall back to City (ODOO sends city as region)
        string regionInput = !string.IsNullOrWhiteSpace(dto.Region) ? dto.Region : dto.City;
        bp.UserFields.Fields.Item("U_REGION").Value = ValidateRegion(regionInput);

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

        // Save address — prefer Address, fall back to Address1 (ODOO field name)
        string address = !string.IsNullOrWhiteSpace(dto.Address) ? dto.Address : dto.Address1;
        if (!string.IsNullOrWhiteSpace(address))
        {
            bp.Address = address;
            bp.Addresses.AddressType = BoAddressType.bo_BillTo;
            bp.Addresses.AddressName = "Billing";
            bp.Addresses.Street = address;
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
        var company = GetConnectedCompany();
        return await _invoiceService.GetDetailedInvoiceStatusReportAsync(company, salesName, docDate);
    }

    public IReadOnlyDictionary<int, InvoiceLifecycleStatusResult> GetInvoiceLifecycleStatusResults(IEnumerable<int> docEntries)
    {
        var company = GetConnectedCompany();
        return _invoiceLifecycleStatusService.GetLifecycleStatusResults(company, docEntries);
    }

    public IReadOnlyDictionary<int, InvoiceLifecycleEvidence> GetInvoiceLifecycleEvidence(IEnumerable<int> docEntries)
    {
        var company = GetConnectedCompany();
        return _invoiceLifecycleStatusService.LoadEvidence(company, docEntries);
    }

    public void LogInvoiceLifecycleDebug(IEnumerable<int> docNums)
    {
        var company = GetConnectedCompany();
        _invoiceLifecycleStatusService.LogDebugForDocNumbers(company, docNums);
    }



    private int GetSalesEmployeeCodeByName(SAPbobsCOM.Company company, string salesName)
    {
        var rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.BoRecordset);
        try
        {
            rs.DoQuery($@"SELECT SlpCode FROM OSLP WHERE SlpName = '{salesName.Replace("'", "''")}'");

            if (!rs.EoF)
                return Convert.ToInt32(rs.Fields.Item("SlpCode").Value);

            return 0;
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
    }


    // ─── Incoming Payments ────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    public IncomingPaymentResultDto PostIncomingPayment(CreateIncomingPaymentDto dto)
    {
        var company = GetConnectedCompany();
        Payments? payment = null;

        try
        {
            payment = (Payments)company.GetBusinessObject(BoObjectTypes.oIncomingPayments);

            payment.CardCode = dto.CardCode;
            payment.DocDate  = dto.PaymentDate;
            if (!string.IsNullOrWhiteSpace(dto.Remarks))
            {
                payment.JournalRemarks = dto.Remarks;
                payment.Remarks        = dto.Remarks;
            }

            decimal total = dto.Invoices.Count > 0
                ? dto.Invoices.Sum(i => i.AmountApplied)
                : (dto.TotalAmount ?? 0m);

            if (dto.PaymentChannel == "AdvanceCustomerPayments")
            {
                // Settle invoices by consuming the customer's advance balance on the control account.
                // No payment means — the advance receipt IS the funding source.
                // Invoice application lines (positive) + advance receipt lines (negative) net to zero.
                // SAP debits the advance control account and credits AR automatically.
                // NEVER use TransferAccount/CashAccount here — account-lines are rejected on
                // control accounts (SAP error 173-87).

                bool firstInv = true;
                foreach (var inv in dto.Invoices)
                {
                    if (!firstInv) payment.Invoices.Add();
                    payment.Invoices.DocEntry    = inv.DocEntry;
                    payment.Invoices.SumApplied  = (double)inv.AmountApplied;
                    payment.Invoices.InvoiceType = BoRcptInvTypes.it_Invoice;
                    firstInv = false;
                }

                // Advance receipt consumption — oldest first, negative SumApplied.
                decimal remaining = total;
                foreach (var (rcptDocEntry, rcptOpen) in GetOpenAdvanceReceipts(dto.CardCode))
                {
                    decimal consume = Math.Min(rcptOpen, remaining);
                    payment.Invoices.Add();
                    payment.Invoices.DocEntry    = rcptDocEntry;
                    payment.Invoices.SumApplied  = -(double)consume;
                    // it_Receipt = object type 24 (incoming payment). Named enum, not a bare cast,
                    // so nobody "fixes" a mysterious 24 later. If this SDK version lacks the named
                    // member, use (BoRcptInvTypes)24 with this comment kept.
                    payment.Invoices.InvoiceType = BoRcptInvTypes.it_Receipt;
                    remaining -= consume;
                    if (remaining <= 0m) break;
                }

                // GUARD: never send an unbalanced payment to SAP. If the open advances could not
                // cover the requested application, fail cleanly here — the caller maps this to a
                // 422 with the figures, same shape as the controller-level balance check.
                if (remaining > 0m)
                {
                    decimal available = total - remaining;
                    _logger.LogWarning("Advance settlement short: requested {Req}, available {Avail} for {Card}",
                        total, available, dto.CardCode);
                    return new IncomingPaymentResultDto
                    {
                        Success      = false,
                        ErrorCode    = 422,
                        ErrorMessage = $"Insufficient advance balance: requested {total:N2}, available {available:N2}."
                    };
                }
            }
            else
            {
                // Regular invoice payment OR advance receipt (invoices:[]).
                // Debit the physical channel GL.
                string receivingGl = GetPaymentGlAccount(dto.PaymentChannel);
                bool   isCash      = dto.PaymentChannel == "CashOnHand";

                if (isCash)
                {
                    payment.CashSum     = (double)total;
                    payment.CashAccount = receivingGl;
                }
                else
                {
                    payment.TransferSum       = (double)total;
                    payment.TransferAccount   = receivingGl;
                    payment.TransferReference = dto.TransferReference ?? string.Empty;
                    payment.TransferDate      = dto.PaymentDate;
                }

                // Advance receipt: credit side — advance control account via the BP-side mechanism.
                // JDT1 lands as Account=<advance acct>, ShortName=CardCode — balance query stays exact.
                // Invoice payments leave ControlAccount unset; SAP uses the AR control from the invoice.
                if (dto.Invoices.Count == 0)
                    payment.ControlAccount = _paymentSettings.AdvanceCustomerPayments;

                bool first = true;
                foreach (var inv in dto.Invoices)
                {
                    if (!first) payment.Invoices.Add();
                    payment.Invoices.DocEntry    = inv.DocEntry;
                    payment.Invoices.SumApplied  = (double)inv.AmountApplied;
                    payment.Invoices.InvoiceType = BoRcptInvTypes.it_Invoice;
                    first = false;
                }
            }

            // Branch (BPLId) — required when branches are enabled.
            int bplId = _paymentSettings.DefaultBranchId;
            if (dto.Invoices.Count > 0)
            {
                var bplRs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                try
                {
                    bplRs.DoQuery($"SELECT TOP 1 BPLId FROM OINV WHERE DocEntry = {dto.Invoices[0].DocEntry}");
                    if (!bplRs.EoF && bplRs.Fields.Item("BPLId").Value is not null)
                        bplId = Convert.ToInt32(bplRs.Fields.Item("BPLId").Value);
                }
                finally { Marshal.ReleaseComObject(bplRs); }
            }
            payment.BPLID = bplId;

            if (!string.IsNullOrWhiteSpace(dto.ClientReference))
            {
                try { payment.UserFields.Fields.Item("U_ClientRef").Value = dto.ClientReference; }
                catch (Exception udfEx) { _logger.LogDebug("[UDF] Could not set U_ClientRef: {Msg}", udfEx.Message); }
            }

            int ret = payment.Add();
            if (ret != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogError("❌ SAP PostIncomingPayment failed [{Code}]: {Message}", errCode, errMsg);
                return new IncomingPaymentResultDto { Success = false, ErrorCode = errCode, ErrorMessage = errMsg };
            }

            company.GetNewObjectCode(out string newEntryStr);
            int newDocEntry = int.Parse(newEntryStr);

            var p2 = (Payments)company.GetBusinessObject(BoObjectTypes.oIncomingPayments);
            p2.GetByKey(newDocEntry);
            int docNum = p2.DocNum;
            Marshal.ReleaseComObject(p2);

            _logger.LogInformation("✅ Incoming payment created: DocEntry={DocEntry}, DocNum={DocNum}", newDocEntry, docNum);
            return new IncomingPaymentResultDto { Success = true, PaymentDocEntry = newDocEntry, PaymentDocNum = docNum };
        }
        finally
        {
            if (payment != null) Marshal.ReleaseComObject(payment);
        }
    }

    // ─── SAP UDF Setup ───────────────────────────────────────────────────────
    // Creates U_ClientRef on ORCT (Incoming Payments) if it does not already exist.
    // Safe to call on every startup — the CUFD existence check makes it idempotent.

    [SupportedOSPlatform("windows")]
    public void EnsureIncomingPaymentUdfs()
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
        try
        {
            rs.DoQuery("SELECT COUNT(*) AS Cnt FROM CUFD WHERE TableID = 'ORCT' AND AliasID = 'ClientRef'");
            int cnt = (!rs.EoF && rs.Fields.Item("Cnt").Value is not null)
                ? Convert.ToInt32(rs.Fields.Item("Cnt").Value) : 0;
            if (cnt > 0)
            {
                _logger.LogInformation("[UDF] U_ClientRef on ORCT already exists — skipping creation.");
                return;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }

        var udf = (UserFieldsMD)company.GetBusinessObject(BoObjectTypes.oUserFields);
        try
        {
            udf.TableName = "ORCT";
            udf.Name      = "ClientRef";
            udf.Description = "Accounts App Client Reference";
            udf.Type      = BoFieldTypes.db_Alpha;
            udf.EditSize  = 100;
            int ret = udf.Add();
            if (ret != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogWarning("[UDF] Could not create U_ClientRef on ORCT [{Code}]: {Msg}", errCode, errMsg);
            }
            else
            {
                _logger.LogInformation("[UDF] Created U_ClientRef on ORCT successfully.");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(udf);
        }
    }

    // ─── Customer Advance Balance ─────────────────────────────────────────────
    // Returns the net available advance balance on the configured advance GL account
    // for a specific customer. Credits = advance receipts; debits = settlements.
    // Available = SUM(Credit) − SUM(Debit) for that CardCode, excluding cancelled entries.

    // Open (unconsumed) advance receipts for a customer, oldest first.
    // OpenBal is SAP-maintained: original on-account amount minus everything already
    // reconciled/consumed — no manual RCT2 arithmetic needed. The EXISTS clause
    // restricts to receipts genuinely credited to the advance control account, so this
    // list is always the same set of money that GetCustomerAdvanceBalance measures;
    // legacy pre-migration on-account credits (default AR control) are excluded.
    // NOTE: verified against known data before deploy — reconciled receipts RC 23366 /
    // RC 23368 (CUS052) must return OpenBal = 0 here.
    [SupportedOSPlatform("windows")]
    private List<(int DocEntry, decimal OpenAmount)> GetOpenAdvanceReceipts(string cardCode)
    {
        Recordset? rs = null;
        try
        {
            var company = GetConnectedCompany();
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

            string acct = _paymentSettings.AdvanceCustomerPayments;
            string card = cardCode.Replace("'", "''");

            rs.DoQuery($@"
                SELECT r.DocEntry, r.OpenBal
                FROM ORCT r
                WHERE r.CardCode = '{card}'
                  AND r.Canceled = 'N'
                  AND r.NoDocSum > 0
                  AND r.OpenBal  > 0
                  AND EXISTS (SELECT 1 FROM JDT1 j
                              WHERE j.TransId  = r.TransId
                                AND j.Account  = '{acct}'
                                AND j.ShortName = r.CardCode
                                AND j.Credit   > 0)
                ORDER BY r.DocDate ASC, r.DocEntry ASC");

            var list = new List<(int, decimal)>();
            while (!rs.EoF)
            {
                list.Add((
                    Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    Convert.ToDecimal(rs.Fields.Item("OpenBal").Value)
                ));
                rs.MoveNext();
            }
            return list;
        }
        catch (Exception ex)
        {
            // A failed query is a DEFECT, not "customer has no advances". Returning an
            // empty list here would convert bugs into misleading insufficient-balance
            // rejections. Surface it.
            _logger.LogError(ex, "[AdvanceReceipts] Query failed for {CardCode} — failing the request", cardCode);
            throw;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    public decimal GetCustomerAdvanceBalance(string cardCode)
    {
        Recordset? rs = null;
        try
        {
            var company = GetConnectedCompany();
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            string gl  = _paymentSettings.AdvanceCustomerPayments;
            // COALESCE: SUM over no rows → NULL; ISNULL converts to 0.
            // A customer with no 202011 history returns one row with AdvanceBalance = 0.
            string sql = $@"
                SELECT ISNULL(SUM(ISNULL(jdt.Credit,0)) - SUM(ISNULL(jdt.Debit,0)), 0) AS AdvanceBalance
                FROM JDT1 jdt
                INNER JOIN OJDT ojdt ON jdt.TransId = ojdt.TransId
                WHERE jdt.Account   = '{gl}'
                  AND jdt.ShortName = '{cardCode}'
                  AND ojdt.Canceled = 'N'";
            rs.DoQuery(sql);
            if (!rs.EoF)
            {
                var val = rs.Fields.Item("AdvanceBalance").Value;
                if (val == null || val is DBNull) return 0m;
                return Convert.ToDecimal(val);
            }
            return 0m;
        }
        catch (Exception ex)
        {
            // Never let a balance-check failure become a 500 on the posting endpoint.
            // Log and treat as zero — the overdraw guard will then block the posting.
            _logger.LogWarning(ex, "[AdvanceBalance] Could not query 202011 balance for {CardCode} — treating as 0", cardCode);
            return 0m;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ─── GL Account Opening Balances ─────────────────────────────────────────
    // Returns the running balance (Debit − Credit) for each tracked GL account
    // for all journal entries strictly before `asOf`, plus the date of the first
    // ever transaction on that account (across all history, not just before asOf).

    [SupportedOSPlatform("windows")]
    public List<GlAccountBalance> GetGlOpeningBalances(DateTime asOf)
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

        try
        {
            rs.DoQuery($@"
SELECT
    acc_list.Account,
    ISNULL(oact.AcctName, '')                                              AS AccountName,
    ISNULL(SUM(CASE WHEN ojdt.RefDate < '{asOf:yyyy-MM-dd}'
                    THEN jdt.Debit - jdt.Credit ELSE 0 END), 0)           AS OpeningBalance,
    MIN(ojdt.RefDate)                                                      AS FirstTransactionDate
FROM (VALUES ('163000'),('164000'),('165000'),('166000'),('167000'),('202010'),('202011')) acc_list(Account)
LEFT JOIN OACT oact ON oact.AcctCode = acc_list.Account
LEFT JOIN JDT1 jdt  ON jdt.Account  = acc_list.Account
LEFT JOIN OJDT ojdt ON jdt.TransId  = ojdt.TransId
GROUP BY acc_list.Account, oact.AcctName
ORDER BY acc_list.Account");

            var result = new List<GlAccountBalance>();

            while (!rs.EoF)
            {
                object ftd = rs.Fields.Item("FirstTransactionDate").Value;
                result.Add(new GlAccountBalance
                {
                    Account              = rs.Fields.Item("Account").Value?.ToString() ?? "",
                    AccountName          = rs.Fields.Item("AccountName").Value?.ToString() ?? "",
                    OpeningBalance       = Convert.ToDecimal(rs.Fields.Item("OpeningBalance").Value),
                    AsOf                 = asOf,
                    FirstTransactionDate = ftd is DBNull ? null : Convert.ToDateTime(ftd)
                });
                rs.MoveNext();
            }

            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
    }

    private string GetPaymentGlAccount(string channel) => channel switch
    {
        "CashOnHand"              => _paymentSettings.CashOnHand,
        "MPesaLipa"               => _paymentSettings.MPesaLipa,
        "TigoLipa"                => _paymentSettings.TigoLipa,
        "CRDB"                    => _paymentSettings.CRDB,
        "AALNMB"                  => _paymentSettings.AALNMB,
        "AdvanceCustomerPayments" => _paymentSettings.AdvanceCustomerPayments,
        _ => throw new ArgumentException($"Unknown payment channel: {channel}")
    };


    // ─── GL Account Statement ─────────────────────────────────────────────────
    // Fetches JDT1 debit/credit lines for all 6 payment GL accounts.
    // Covers ALL TransTypes (incoming payments, outgoing payments, journal entries,
    // transfers) — both debit and credit sides.
    // ORCT join (incoming payments) and OVPM join (outgoing payments) are done
    // purely on TransId — no ObjType filter, which varies by SAP configuration.

    [SupportedOSPlatform("windows")]
    public List<GlAccountStatement> GetGlAccountStatements(DateTime from, DateTime to)
    {
        var company = GetConnectedCompany();
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

        try
        {
            rs.DoQuery($@"
SELECT
    jdt.TransId,
    jdt.Account,
    ISNULL(acct.AcctName, '')                                   AS AccountName,
    ojdt.RefDate,
    ISNULL(jdt.Debit,  0)                                       AS Debit,
    ISNULL(jdt.Credit, 0)                                       AS Credit,
    ISNULL(jdt.LineMemo, '')                                    AS LineMemo,
    ISNULL(CAST(ojdt.ObjType AS NVARCHAR(20)), '')              AS TransType,
    ISNULL(ojdt.Ref1, '')                                       AS Ref1,
    ISNULL(ojdt.Ref2, '')                                       AS Ref2,
    COALESCE(orct.DocEntry, ovpm.DocEntry)                      AS PaymentDocEntry,
    COALESCE(orct.DocNum,   ovpm.DocNum)                        AS PaymentDocNum,
    inv_link.InvoiceDocEntry,
    inv_link.InvoiceDocNum,
    ISNULL(COALESCE(orct.CardCode, ovpm.CardCode), '')          AS CardCode,
    ISNULL(COALESCE(orct.CardName, ovpm.CardName), '')          AS CardName
FROM JDT1 jdt
INNER JOIN OJDT ojdt  ON jdt.TransId  = ojdt.TransId
LEFT  JOIN OACT acct  ON jdt.Account  = acct.AcctCode
LEFT  JOIN ORCT orct  ON orct.TransId = ojdt.TransId
LEFT  JOIN OVPM ovpm  ON ovpm.TransId = ojdt.TransId
OUTER APPLY (
    SELECT TOP 1
        r.DocEntry  AS InvoiceDocEntry,
        inv.DocNum  AS InvoiceDocNum
    FROM  RCT2 r
    INNER JOIN OINV inv ON inv.DocEntry = r.DocEntry
    WHERE r.DocNum  = orct.DocEntry
      AND r.InvType = 13
    ORDER BY r.DocEntry
) AS inv_link
WHERE jdt.Account IN ('163000','164000','165000','166000','167000','202010','202011')
  AND ojdt.RefDate >= '{from:yyyy-MM-dd}'
  AND ojdt.RefDate <= '{to:yyyy-MM-dd}'
ORDER BY ojdt.RefDate DESC, jdt.TransId DESC");

            var result = new List<GlAccountStatement>();

            while (!rs.EoF)
            {
                object pdeVal = rs.Fields.Item("PaymentDocEntry").Value;
                object pdnVal = rs.Fields.Item("PaymentDocNum").Value;
                object ideVal = rs.Fields.Item("InvoiceDocEntry").Value;
                object idnVal = rs.Fields.Item("InvoiceDocNum").Value;

                result.Add(new GlAccountStatement
                {
                    TransId          = Convert.ToInt32(rs.Fields.Item("TransId").Value),
                    Account          = rs.Fields.Item("Account").Value?.ToString() ?? "",
                    AccountName      = rs.Fields.Item("AccountName").Value?.ToString() ?? "",
                    RefDate          = Convert.ToDateTime(rs.Fields.Item("RefDate").Value),
                    Debit            = Convert.ToDecimal(rs.Fields.Item("Debit").Value),
                    Credit           = Convert.ToDecimal(rs.Fields.Item("Credit").Value),
                    LineMemo         = rs.Fields.Item("LineMemo").Value?.ToString() ?? "",
                    TransType        = rs.Fields.Item("TransType").Value?.ToString() ?? "",
                    Ref1             = rs.Fields.Item("Ref1").Value?.ToString() ?? "",
                    Ref2             = rs.Fields.Item("Ref2").Value?.ToString() ?? "",
                    PaymentDocEntry  = pdeVal is DBNull ? null : Convert.ToInt32(pdeVal),
                    PaymentDocNum    = pdnVal is DBNull ? null : Convert.ToInt32(pdnVal),
                    InvoiceDocEntry  = ideVal is DBNull ? null : Convert.ToInt32(ideVal),
                    InvoiceDocNum    = idnVal is DBNull ? null : Convert.ToInt32(idnVal),
                    CardCode         = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                    CardName         = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                });

                rs.MoveNext();
            }

            _logger.LogInformation("📊 GL account statements fetched: {Count} rows ({From:yyyy-MM-dd}→{To:yyyy-MM-dd})",
                result.Count, from, to);

            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
    }

    // ── Invoice from Open Deliveries ────────────────────────────────────────────

    public List<OpenDeliveryDto> GetOpenDeliveries()
    {
        var company = GetConnectedCompany();
        var results = new List<OpenDeliveryDto>();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery(@"
                SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName,
                       T0.DocDate, T0.DocDueDate, T0.DocCur, T0.DocTotal, T0.SlpCode
                FROM   ODLN T0
                WHERE  T0.DocStatus = 'O'
                  AND  T0.CANCELED  = 'N'
                ORDER BY T0.DocEntry ASC");

            while (!rs.EoF)
            {
                object dueDateRaw = rs.Fields.Item("DocDueDate").Value;
                object slpRaw    = rs.Fields.Item("SlpCode").Value;
                results.Add(new OpenDeliveryDto
                {
                    DocEntry    = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum      = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    CardCode    = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                    CardName    = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                    DocDate     = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    DocDueDate  = dueDateRaw == null || dueDateRaw is DBNull ? null
                                  : Convert.ToDateTime(dueDateRaw),
                    DocCurrency = rs.Fields.Item("DocCur").Value?.ToString() ?? "TZS",
                    DocTotal    = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                    SlpCode     = slpRaw == null || slpRaw is DBNull ? null
                                  : Convert.ToInt32(slpRaw),
                });
                rs.MoveNext();
            }
            return results;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    private List<DeliveryLineDto> GetDeliveryLines(int docEntry)
    {
        var company = GetConnectedCompany();
        var results = new List<DeliveryLineDto>();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT LineNum, ItemCode, Quantity, Price
                FROM   DLN1
                WHERE  DocEntry = {docEntry}
                ORDER BY LineNum ASC");

            while (!rs.EoF)
            {
                results.Add(new DeliveryLineDto
                {
                    DocEntry = docEntry,
                    LineNum  = Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    ItemCode = rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity = Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    Price    = Convert.ToDecimal(rs.Fields.Item("Price").Value),
                });
                rs.MoveNext();
            }
            return results;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    public (int DocEntry, int DocNum) CreateInvoiceFromDelivery(OpenDeliveryDto delivery, DateTime docDueDate)
    {
        var company = GetConnectedCompany();

        var lines = GetDeliveryLines(delivery.DocEntry);
        if (lines.Count == 0)
            throw new Exception($"Delivery {delivery.DocEntry} has no lines to invoice.");

        var invoice = (Documents)company.GetBusinessObject(BoObjectTypes.oInvoices);
        invoice.CardCode    = delivery.CardCode;
        invoice.DocDate     = DateTime.Today;
        invoice.DocDueDate  = docDueDate;
        invoice.DocCurrency = delivery.DocCurrency;
        invoice.BPL_IDAssignedToInvoice = 1;

        if (delivery.SlpCode.HasValue)
            invoice.SalesPersonCode = delivery.SlpCode.Value;

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) invoice.Lines.Add();
            invoice.Lines.BaseType  = 15; // oDeliveryNotes
            invoice.Lines.BaseEntry = delivery.DocEntry;
            invoice.Lines.BaseLine  = lines[i].LineNum;
        }

        if (invoice.Add() != 0)
        {
            string err = company.GetLastErrorDescription();
            throw new Exception($"Failed to invoice delivery {delivery.DocEntry}: {err}");
        }

        int newDocEntry = int.Parse(company.GetNewObjectKey());

        // Fetch DocNum for the audit log
        int docNum = 0;
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM OINV WHERE DocEntry = {newDocEntry}");
            if (!rs.EoF)
                docNum = Convert.ToInt32(rs.Fields.Item("DocNum").Value);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }

        return (newDocEntry, docNum);
    }
}
