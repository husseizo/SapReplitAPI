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
using SapReplitAPI.Models.SoDelivery;
using SapReplitAPI.Models.ZoneFulfillment;
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
    private readonly SapWarehouseInventoryService _warehouseService;
    private readonly SapBinInventoryService       _binInventoryService;
    private readonly PaymentSettings _paymentSettings;
    private SAPbobsCOM.Company? _company;

    public SapService(
        IOptions<SapSettings> settings,
        IOptions<PaymentSettings> paymentSettings,
        ILogger<SapService> logger,
        SapProductService productService,
        SapCustomerService customerService,
        SapInvoiceService invoiceService,
        InvoiceLifecycleStatusService invoiceLifecycleStatusService,
        SapWarehouseInventoryService warehouseService,
        SapBinInventoryService binInventoryService)
    {
        _logger = logger;
        _settings = settings.Value;
        _paymentSettings = paymentSettings.Value;
        _productService = productService;
        _customerService = customerService;
        _invoiceService = invoiceService;
        _invoiceLifecycleStatusService = invoiceLifecycleStatusService;
        _warehouseService = warehouseService;
        _binInventoryService = binInventoryService;
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

    // ── Warehouse inventory ───────────────────────────────────────────────────

    public List<SapReplitAPI.Models.Inventory.WarehouseInventoryRow> GetWarehouseInventorySnapshot()
    {
        var company = GetConnectedCompany();
        return _warehouseService.GetFullSnapshot(company);
    }

    public List<SapReplitAPI.Models.Inventory.WarehouseInventoryRow> GetWarehouseInventorySnapshotForItems(
        IReadOnlyCollection<string> itemCodes)
    {
        var company = GetConnectedCompany();
        return _warehouseService.GetSnapshotForItems(company, itemCodes);
    }

    public SapReplitAPI.Models.Inventory.ChangedItemsResult GetChangedWarehouseItemCodes(DateTime from)
    {
        var company = GetConnectedCompany();
        return _warehouseService.GetChangedItemCodes(company, from);
    }

    // ── Bin inventory ─────────────────────────────────────────────────────────

    public List<SapReplitAPI.Models.Inventory.BinInventoryRow> GetBinInventorySnapshot()
    {
        var company = GetConnectedCompany();
        return _binInventoryService.GetFullSnapshot(company);
    }

    public List<SapReplitAPI.Models.Inventory.BinInventoryRow> GetBinInventorySnapshotForItems(
        IReadOnlyCollection<string> itemCodes)
    {
        var company = GetConnectedCompany();
        return _binInventoryService.GetSnapshotForItems(company, itemCodes);
    }

    public SapReplitAPI.Models.Inventory.ChangedItemsResult GetChangedBinItemCodes(DateTime from)
    {
        var company = GetConnectedCompany();
        return _binInventoryService.GetChangedItemCodes(company, from);
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

        // === Line Items ===
        // Dscription is pre-enriched by the caller (controller calls EnrichOrderLines first).
        // Sync job uses the description stored in Neon — no OITM query here.
        foreach (var line in dto.Lines)
        {
            var description = !string.IsNullOrWhiteSpace(line.Dscription) ? line.Dscription : line.ItemCode;

            order.Lines.ItemCode        = line.ItemCode;
            order.Lines.Quantity        = line.Quantity;
            order.Lines.Price           = (double)line.Price;
            order.Lines.VatGroup        = "TZ";
            order.Lines.WarehouseCode   = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode;
            order.Lines.ItemDescription = description;

            if (!string.IsNullOrWhiteSpace(line.U_ItemName))
                order.Lines.UserFields.Fields.Item("U_ItemName").Value = line.U_ItemName;

            if (!string.IsNullOrWhiteSpace(line.U_Manufacturer))
                order.Lines.UserFields.Fields.Item("U_Manufacturer").Value = line.U_Manufacturer;

            order.Lines.Add();
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

    // Enriches line Dscription + U_ItemName from OITM before sending to SAP.
    // MUST be called only from HTTP request context (controller), never from background jobs.
    public void EnrichOrderLines(List<OrderLineDto> lines)
    {
        var company = GetConnectedCompany();
        var oitm = QueryOitm(company, lines.Select(l => l.ItemCode));
        foreach (var line in lines)
        {
            oitm.TryGetValue(line.ItemCode, out var info);
            var desc = BuildDescription(info.ItemName, info.Model, info.SapName, line.ItemCode);
            if (!string.IsNullOrWhiteSpace(desc))
                line.Dscription = desc;
            if (!string.IsNullOrWhiteSpace(info.ItemName))
                line.U_ItemName = info.ItemName;
        }
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
    public List<OrderModel> GetAllOrders(int? slpCode, string? customer, DateTime? fromDate, DateTime? toDate, bool useUpdateDate = false)
    {
        var orders = new List<OrderModel>();
        Recordset rsHeader = null;
        Recordset rsLines = null;

        try
        {
            var company = GetConnectedCompany();   // ✅ always ensures connected
            fromDate ??= new DateTime(2024, 1, 1);
            toDate ??= DateTime.Today;

            Console.WriteLine(useUpdateDate
                ? $"📅 Fetching ALL orders updated since {fromDate:yyyy-MM-dd HH:mm:ss}"
                : $"📅 Fetching ALL orders between {fromDate:yyyy-MM-dd} and {toDate:yyyy-MM-dd}");

            // 1️⃣ FETCH HEADERS (remove DocStatus filter)
            string dateClause = useUpdateDate
                ? $"T0.UpdateDate >= '{fromDate:yyyy-MM-dd HH:mm:ss}'"
                : $"T0.DocDate BETWEEN '{fromDate:yyyy-MM-dd}' AND '{toDate:yyyy-MM-dd}'";
            string headerQuery = $@"
SELECT
    T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName, T0.DocDate, T0.DocTotal,
    T0.SlpCode, ISNULL(T1.SlpName, '') AS SlpName,
    T0.DocStatus, ISNULL(T0.CANCELED, 'N') AS Canceled
FROM ORDR T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE {dateClause}";

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

            // Lines — Dscription pre-enriched by caller via EnrichOrderLines
            foreach (var line in dto.Lines)
            {
                if (line == null) continue;
                if (string.IsNullOrWhiteSpace(line.ItemCode))
                    throw new ArgumentException("Each line requires ItemCode.");

                var description = !string.IsNullOrWhiteSpace(line.Dscription) ? line.Dscription : line.ItemCode.Trim();

                quot.Lines.ItemCode        = line.ItemCode.Trim();
                quot.Lines.Quantity        = line.Quantity <= 0 ? 1 : line.Quantity;
                quot.Lines.VatGroup        = "TZ";
                quot.Lines.WarehouseCode   = string.IsNullOrWhiteSpace(line.WhsCode) ? "001" : line.WhsCode.Trim();
                quot.Lines.ItemDescription = description;
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


    // Serializes generate→Add() to prevent concurrent requests racing on the same candidate CardCode.
    private static readonly SemaphoreSlim _customerCreateSemaphore = new(1, 1);

    public string CreateCustomer(CreateCustomerDto dto)
    {
        _logger.LogInformation("[CUSTOMER-CREATE] Request: SlpCode={code} SalesPersonName={name}", dto.SlpCode, dto.SalesPersonName);

        if (string.IsNullOrWhiteSpace(dto.CardName))
            throw new Exception("CardName is required");

        _customerCreateSemaphore.Wait();
        try
        {
            return CreateCustomerCore(dto);
        }
        finally
        {
            _customerCreateSemaphore.Release();
        }
    }

    private string CreateCustomerCore(CreateCustomerDto dto)
    {
        const int MaxAttempts = 5;

        string phone      = dto.Phone?.Trim() ?? string.Empty;
        string address    = CustomerCreationHelpers.SelectAddress(dto.Address, dto.Address1);
        bool   hasAddress = !string.IsNullOrWhiteSpace(address);
        int    slpCode    = ResolveSlpCode(dto);

        GetConnectedCompany();

        // Tracks the highest card-code number we know is occupied (OCRD committed or CRD1 orphan).
        // Passed to GenerateNextCustomerCode so the MAX query is floored above any known-blocked code.
        int skipMinimum = 0;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            string candidate = GenerateNextCustomerCode(skipMinimum);

            // Phase 5/7 — pre-flight: OCRD (committed BP) and CRD1 (orphaned address rows
            // from prior failed bp.Add() calls that SAP did not fully roll back)
            bool inOcrd = CardCodeExistsInOcrd(candidate);
            bool inCrd1 = CardCodeHasAnyCrd1Row(candidate);
            if (inOcrd || inCrd1)
            {
                _logger.LogWarning(
                    "[CUSTOMER-CREATE] Candidate {code} unavailable (OCRD={inOcrd} CRD1={inCrd1}), advancing skipMinimum={skip} attempt={n}/{max}",
                    candidate, inOcrd, inCrd1, skipMinimum, attempt, MaxAttempts);
                skipMinimum = CustomerCreationHelpers.ParseCardCodeNum(candidate);
                continue;
            }

            _logger.LogInformation(
                "[CUSTOMER-CREATE] Attempting Add: CardCode={code} LiveDB={liveDb} Attempt={n}",
                candidate, _company!.CompanyDB, attempt);

            // Phase 7 — fresh COM object per attempt; never reuse a failed BusinessPartners object
            var bp = (BusinessPartners)_company!.GetBusinessObject(BoObjectTypes.oBusinessPartners);
            bp.CardType = BoCardTypes.cCustomer;
            bp.CardCode = candidate;
            bp.CardName = dto.CardName;
            bp.Phone1 = phone;
            bp.UserFields.Fields.Item("U_Phone").Value = phone;
            bp.UserFields.Fields.Item("U_Customer_Type").Value = dto.CustomerType?.Trim() ?? string.Empty;
            string regionInput = !string.IsNullOrWhiteSpace(dto.Region) ? dto.Region : dto.City;
            bp.UserFields.Fields.Item("U_REGION").Value = ValidateRegion(regionInput);
            if (!string.IsNullOrWhiteSpace(dto.VIN1)) bp.UserFields.Fields.Item("U_VIN1").Value = dto.VIN1.Trim();
            if (!string.IsNullOrWhiteSpace(dto.VIN2)) bp.UserFields.Fields.Item("U_VIN2").Value = dto.VIN2.Trim();
            if (!string.IsNullOrWhiteSpace(dto.VIN3)) bp.UserFields.Fields.Item("U_VIN3").Value = dto.VIN3.Trim();
            bp.SalesPersonCode = slpCode;

            // Phase 4 — Address fix: SAP DI API pre-initialises Addresses row 0; setting fields on it
            // without calling Add() produces exactly ONE CRD1 row.  The previous bp.Addresses.Add()
            // appended an empty row 1 whose default fields collided with the auto-created row → ODBC -2035.
            if (hasAddress)
            {
                bp.Address = address;
                bp.Addresses.AddressType = BoAddressType.bo_BillTo;
                bp.Addresses.AddressName = "Billing";
                bp.Addresses.Street      = address;
                // ⚠️  DO NOT call bp.Addresses.Add() here — it creates a duplicate empty CRD1 row
            }

            int rc = bp.Add();
            if (rc == 0) return candidate;

            int    errCode = _company.GetLastErrorCode();
            string errDesc = _company.GetLastErrorDescription();
            bool   ocrdNow = CardCodeExistsInOcrd(candidate);
            bool   crd1Now = CardCodeHasAnyCrd1Row(candidate);
            _logger.LogError(
                "[CUSTOMER-CREATE] bp.Add() failed: rc={rc} ErrorCode={errCode} Description={desc} CardCode={code} OcrdNow={ocrdNow} Crd1Now={crd1Now} Attempt={n}",
                rc, errCode, errDesc, candidate, ocrdNow, crd1Now, attempt);

            if (CustomerCreationHelpers.ShouldRetryOnCardCodeCollision(errCode, ocrdNow, crd1Now))
            {
                _logger.LogWarning("[CUSTOMER-CREATE] -2035 collision for {code} (OCRD={o} CRD1={c}), advancing skipMinimum", candidate, ocrdNow, crd1Now);
                skipMinimum = CustomerCreationHelpers.ParseCardCodeNum(candidate);
                continue;
            }

            throw new Exception($"Failed to create customer: {errDesc}");
        }

        throw new Exception($"Failed to generate a unique CardCode after {MaxAttempts} attempts");
    }

    private int ResolveSlpCode(CreateCustomerDto dto)
    {
        if (dto.SlpCode > 0)
            return dto.SlpCode;

        if (!string.IsNullOrWhiteSpace(dto.SalesPersonName))
        {
            var code = GetSlpCodeByName(dto.SalesPersonName);
            if (code.HasValue) return code.Value;
            throw new Exception($"Salesperson '{dto.SalesPersonName}' not found.");
        }

        throw new Exception("Salesperson information is missing.");
    }

    private bool CardCodeExistsInOcrd(string cardCode)
    {
        var rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery($"SELECT COUNT(*) AS N FROM OCRD WHERE CardCode = '{cardCode.Replace("'", "''")}'");
        return Convert.ToInt32(rs.Fields.Item("N").Value) > 0;
    }

    // Detects orphaned CRD1 rows left by a prior bp.Add() that SAP did not fully roll back.
    // If CUS001360 has a CRD1 row but no OCRD row, the next bp.Add() for CUS001360 will -2035 on CRD1.
    private bool CardCodeHasAnyCrd1Row(string cardCode)
    {
        var rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery($"SELECT COUNT(*) AS N FROM CRD1 WHERE CardCode = '{cardCode.Replace("'", "''")}'");
        return Convert.ToInt32(rs.Fields.Item("N").Value) > 0;
    }

    private string GenerateNextCustomerCode(int skipMinimum = 0)
    {
        GetConnectedCompany();
        var rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
        rs.DoQuery(@"SELECT ISNULL(MAX(CAST(SUBSTRING(CardCode, 4, LEN(CardCode)) AS INT)), 0) AS MaxCode
                     FROM OCRD
                     WHERE ISNUMERIC(SUBSTRING(CardCode, 4, LEN(CardCode))) = 1
                       AND CardCode LIKE 'CUS%'");
        int maxNum = Math.Max(Convert.ToInt32(rs.Fields.Item("MaxCode").Value), skipMinimum);
        return $"CUS{(maxNum + 1).ToString("D6")}";
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
    T1.SlpName AS SalesEmployeeName,
    T0.U_ZoneRef, T0.U_ReplitId, T0.U_DeliveryLocation
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
                ZoneRef          = rs.Fields.Item("U_ZoneRef").Value is DBNull ? null : rs.Fields.Item("U_ZoneRef").Value?.ToString(),
                U_ReplitId       = rs.Fields.Item("U_ReplitId").Value is DBNull ? null : rs.Fields.Item("U_ReplitId").Value?.ToString(),
                DeliveryLocation = rs.Fields.Item("U_DeliveryLocation").Value is DBNull ? null : rs.Fields.Item("U_DeliveryLocation").Value?.ToString(),
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

    // Dedicated sync reader — no page cap, full Canceled/CounterRef/UpdatedAt fields.
    // Full sync (isDelta=false): filters by ORCT.DocDate window.
    // Delta sync (isDelta=true): filters by ORCT.UpdateDate+UpdateTS with safety overlap.
    public List<InvoicePaymentDto> GetInvoicePaymentsForSync(DateTime from, DateTime to, bool isDelta)
    {
        _ = GetConnectedCompany();
        var rs = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);

        string filter;
        if (isDelta)
        {
            int fromTs = from.Hour * 10000 + from.Minute * 100 + from.Second;
            int toTs = to.Hour * 10000 + to.Minute * 100 + to.Second;
            filter = $@"
WHERE (
    ORCT.UpdateDate > '{from:yyyy-MM-dd}'
    OR (ORCT.UpdateDate = '{from:yyyy-MM-dd}' AND ORCT.UpdateTS >= {fromTs})
)
AND (
    ORCT.UpdateDate < '{to:yyyy-MM-dd}'
    OR (ORCT.UpdateDate = '{to:yyyy-MM-dd}' AND ORCT.UpdateTS <= {toTs})
)";
        }
        else
        {
            filter = $"WHERE ORCT.DocDate >= '{from:yyyy-MM-dd}' AND ORCT.DocDate <= '{to:yyyy-MM-dd}'";
        }

        string query = $@"
SELECT
    OINV.DocEntry     AS InvoiceDocEntry,
    ORCT.DocEntry     AS PaymentDocEntry,
    ORCT.DocNum       AS PaymentNumber,
    OINV.DocNum       AS InvoiceDocNum,
    ORCT.DocDate      AS PaymentDate,
    ORCT.CardCode,
    ORCT.CardName,
    RCT2.SumApplied   AS AmountApplied,
    ORCT.TrsfrSum     AS BankTransferAmount,
    ORCT.TrsfrRef     AS BankTransferReference,
    JDT1_D.Account    AS DebitAccountCode,
    OACT.AcctName     AS DebitAccountName,
    OSLP.SlpCode      AS SalesEmployeeCode,
    OSLP.SlpName      AS SalesEmployeeName,
    ORCT.Canceled,
    ORCT.CounterRef,
    ORCT.UpdateDate,
    ORCT.UpdateTS,
    ORCT.U_ClientRef  AS ClientReference
FROM ORCT
INNER JOIN RCT2  ON RCT2.DocNum  = ORCT.DocEntry AND RCT2.InvType = 13
INNER JOIN OINV  ON OINV.DocEntry = RCT2.DocEntry
LEFT JOIN (
    SELECT TransId, MIN(Account) AS Account
    FROM JDT1
    WHERE Debit > 0
    GROUP BY TransId
) JDT1_D ON JDT1_D.TransId = ORCT.TransId
LEFT JOIN OACT   ON OACT.AcctCode = JDT1_D.Account
LEFT JOIN OSLP   ON OSLP.SlpCode  = OINV.SlpCode
{filter}
ORDER BY ORCT.DocEntry, RCT2.DocEntry";

        Console.WriteLine($"📤 [GetInvoicePaymentsForSync] isDelta={isDelta}, from={from:yyyy-MM-dd HH:mm:ss}, to={to:yyyy-MM-dd HH:mm:ss}");
        rs.DoQuery(query);

        var results = new List<InvoicePaymentDto>();
        while (!rs.EoF)
        {
            object updateDateRaw = rs.Fields.Item("UpdateDate").Value;
            object updateTsRaw   = rs.Fields.Item("UpdateTS").Value;
            DateTime updatedAt = new DateTime(1900, 1, 1);
            if (updateDateRaw != null && !Convert.IsDBNull(updateDateRaw))
            {
                var date = Convert.ToDateTime(updateDateRaw);
                int ts   = Convert.ToInt32(updateTsRaw ?? 0);
                updatedAt = date.Date
                    .AddHours(ts / 10000)
                    .AddMinutes((ts / 100) % 100)
                    .AddSeconds(ts % 100);
            }

            results.Add(new InvoicePaymentDto
            {
                DocEntry             = Convert.ToInt32(rs.Fields.Item("InvoiceDocEntry").Value),
                PaymentDocEntry      = Convert.ToInt32(rs.Fields.Item("PaymentDocEntry").Value),
                PaymentNumber        = Convert.ToInt32(rs.Fields.Item("PaymentNumber").Value),
                InvoiceDocNum        = Convert.ToInt32(rs.Fields.Item("InvoiceDocNum").Value),
                PaymentDate          = Convert.ToDateTime(rs.Fields.Item("PaymentDate").Value),
                CardCode             = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                CardName             = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                AmountApplied        = Convert.ToDecimal(rs.Fields.Item("AmountApplied").Value),
                BankTransferAmount   = Convert.ToDecimal(rs.Fields.Item("BankTransferAmount").Value),
                BankTransferReference = rs.Fields.Item("BankTransferReference").Value?.ToString() ?? "",
                DebitAccountCode     = rs.Fields.Item("DebitAccountCode").Value?.ToString() ?? "",
                DebitAccountName     = rs.Fields.Item("DebitAccountName").Value?.ToString() ?? "",
                SalesEmployeeCode    = rs.Fields.Item("SalesEmployeeCode").Value?.ToString() ?? "",
                SalesEmployeeName    = rs.Fields.Item("SalesEmployeeName").Value?.ToString() ?? "",
                ClientReference      = rs.Fields.Item("ClientReference").Value?.ToString() ?? "",
                Canceled             = (rs.Fields.Item("Canceled").Value?.ToString() ?? "N") == "Y",
                CounterRef           = rs.Fields.Item("CounterRef").Value?.ToString() ?? "",
                UpdatedAt            = updatedAt,
            });

            rs.MoveNext();
        }

        Console.WriteLine($"✅ [GetInvoicePaymentsForSync] Retrieved {results.Count} rows.");
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

    // ─── Targeted event-driven readers ───────────────────────────────────────
    // Single-document lookups for OutboxPoller event handlers.
    // Synchronous SAP DI API calls wrapped in Task.FromResult — safe because
    // the outbox poller processes events sequentially (no concurrent DI API calls).

    public Task<InvoiceDto?> GetInvoiceByDocEntryAsync(int docEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rsH = null;
        Recordset? rsL = null;
        try
        {
            rsH = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsH.DoQuery($@"
SELECT T0.DocEntry, T0.DocNum, T0.DocDate, T0.DocStatus, T0.CANCELED,
       T0.CardCode, T0.CardName, T0.DocTotal, T0.PaidToDate,
       (T0.DocTotal - T0.PaidToDate) AS BalanceDue,
       DATEDIFF(DAY, T0.DocDueDate, GETDATE()) AS DaysOverdue,
       T1.SlpCode AS SalesEmployeeCode, T1.SlpName AS SalesEmployeeName,
       T0.GroupNum, T0.U_ZoneRef, T0.U_ReplitId, T0.U_DeliveryLocation
FROM OINV T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE T0.DocEntry = {docEntry}");

            if (rsH.EoF) return Task.FromResult<InvoiceDto?>(null);

            string canceledRaw = rsH.Fields.Item("CANCELED").Value?.ToString() ?? "N";
            string canceledLabel = canceledRaw switch
            {
                "N" => "Not Canceled",
                "Y" => "Canceled",
                "C" => "Cancellation",
                _   => "Unknown"
            };

            var dto = new SapReplitAPI.Models.Payments.InvoiceDto
            {
                DocEntry          = Convert.ToInt32(rsH.Fields.Item("DocEntry").Value),
                DocNum            = Convert.ToInt32(rsH.Fields.Item("DocNum").Value),
                DocDate           = Convert.ToDateTime(rsH.Fields.Item("DocDate").Value),
                Status            = rsH.Fields.Item("DocStatus").Value?.ToString() ?? "",
                Canceled          = canceledLabel,
                CardCode          = rsH.Fields.Item("CardCode").Value?.ToString() ?? "",
                CardName          = rsH.Fields.Item("CardName").Value?.ToString() ?? "",
                DocTotal          = Convert.ToDecimal(rsH.Fields.Item("DocTotal").Value),
                PaidToDate        = Convert.ToDecimal(rsH.Fields.Item("PaidToDate").Value),
                BalanceDue        = Convert.ToDecimal(rsH.Fields.Item("BalanceDue").Value),
                DaysOverdue       = Convert.ToInt32(rsH.Fields.Item("DaysOverdue").Value),
                SalesEmployeeCode = Convert.ToInt32(rsH.Fields.Item("SalesEmployeeCode").Value),
                SalesEmployeeName = rsH.Fields.Item("SalesEmployeeName").Value?.ToString() ?? "",
                GroupNum          = Convert.ToInt32(rsH.Fields.Item("GroupNum").Value),
                ZoneRef           = rsH.Fields.Item("U_ZoneRef").Value is DBNull ? null : rsH.Fields.Item("U_ZoneRef").Value?.ToString(),
                U_ReplitId        = rsH.Fields.Item("U_ReplitId").Value is DBNull ? null : rsH.Fields.Item("U_ReplitId").Value?.ToString(),
                DeliveryLocation  = rsH.Fields.Item("U_DeliveryLocation").Value is DBNull ? null : rsH.Fields.Item("U_DeliveryLocation").Value?.ToString(),
                Lines             = new List<SapReplitAPI.Models.Payments.InvoiceLineDto>()
            };

            rsL = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsL.DoQuery($@"
SELECT T2.LineNum, T2.ItemCode, T2.Dscription, T2.Quantity, T2.Price, T2.LineTotal,
       T3.U_Item_Name, T3.U_MdlTEST
FROM INV1 T2
LEFT JOIN OITM T3 ON T2.ItemCode = T3.ItemCode
WHERE T2.DocEntry = {docEntry}");

            while (!rsL.EoF)
            {
                dto.Lines.Add(new SapReplitAPI.Models.Payments.InvoiceLineDto
                {
                    LineNum    = Convert.ToDecimal(rsL.Fields.Item("LineNum").Value),
                    ItemCode   = rsL.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Dscription = rsL.Fields.Item("Dscription").Value?.ToString() ?? "",
                    Quantity   = Convert.ToDecimal(rsL.Fields.Item("Quantity").Value),
                    Price      = Convert.ToDecimal(rsL.Fields.Item("Price").Value),
                    LineTotal  = Convert.ToDecimal(rsL.Fields.Item("LineTotal").Value),
                    U_Item_Name = rsL.Fields.Item("U_Item_Name").Value?.ToString() ?? "",
                    U_MdlTEST  = rsL.Fields.Item("U_MdlTEST").Value?.ToString() ?? ""
                });
                rsL.MoveNext();
            }

            return Task.FromResult<InvoiceDto?>(dto);
        }
        finally
        {
            if (rsH != null) Marshal.ReleaseComObject(rsH);
            if (rsL != null) Marshal.ReleaseComObject(rsL);
        }
    }

    /// <summary>
    /// For a cancellation document (OINV.CANCELED='C'), returns the DocEntry of the original
    /// invoice via INV1.BaseEntry — SAP's authoritative document chain field.
    /// Returns null if no qualifying base reference is found.
    /// </summary>
    public Task<int?> GetOriginalInvoiceDocEntryAsync(int cancellationDocEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT TOP 1 BaseEntry
FROM INV1
WHERE DocEntry = {cancellationDocEntry}
  AND BaseType  = 13
  AND BaseEntry > 0");
            if (rs.EoF) return Task.FromResult<int?>(null);
            int baseEntry = Convert.ToInt32(rs.Fields.Item("BaseEntry").Value);
            return Task.FromResult<int?>(baseEntry > 0 ? baseEntry : null);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    public Task<SapReplitAPI.Services.Events.SapCreditMemoResult?> GetCreditMemoByDocEntryAsync(int docEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rsH = null;
        Recordset? rsL = null;
        try
        {
            rsH = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsH.DoQuery($"SELECT DocEntry, DocNum FROM ORIN WHERE DocEntry = {docEntry}");

            if (rsH.EoF) return Task.FromResult<SapReplitAPI.Services.Events.SapCreditMemoResult?>(null);

            int foundDocEntry = Convert.ToInt32(rsH.Fields.Item("DocEntry").Value);
            int foundDocNum   = Convert.ToInt32(rsH.Fields.Item("DocNum").Value);

            rsL = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsL.DoQuery($@"
SELECT BaseEntry, BaseType
FROM RIN1
WHERE DocEntry = {docEntry} AND BaseEntry > 0");

            var lines = new List<(int BaseEntry, int BaseType)>();
            while (!rsL.EoF)
            {
                lines.Add((
                    Convert.ToInt32(rsL.Fields.Item("BaseEntry").Value),
                    Convert.ToInt32(rsL.Fields.Item("BaseType").Value)
                ));
                rsL.MoveNext();
            }

            var result = new SapReplitAPI.Services.Events.SapCreditMemoResult(foundDocEntry, foundDocNum, lines);
            return Task.FromResult<SapReplitAPI.Services.Events.SapCreditMemoResult?>(result);
        }
        finally
        {
            if (rsH != null) Marshal.ReleaseComObject(rsH);
            if (rsL != null) Marshal.ReleaseComObject(rsL);
        }
    }

    public Task<List<SapReplitAPI.Models.Payments.InvoicePaymentDto>> GetPaymentByDocEntryAsync(int paymentDocEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT
    OINV.DocEntry  AS InvoiceDocEntry,
    ORCT.DocEntry  AS PaymentDocEntry,
    ORCT.DocNum    AS PaymentNumber,
    OINV.DocNum    AS InvoiceDocNum,
    ORCT.DocDate   AS PaymentDate,
    ORCT.CardCode,
    ORCT.CardName,
    RCT2.SumApplied   AS AmountApplied,
    ORCT.TrsfrSum     AS BankTransferAmount,
    ORCT.TrsfrRef     AS BankTransferReference,
    JDT1_D.Account    AS DebitAccountCode,
    OACT.AcctName     AS DebitAccountName,
    OSLP.SlpCode      AS SalesEmployeeCode,
    OSLP.SlpName      AS SalesEmployeeName,
    ORCT.Canceled,
    ORCT.CounterRef,
    ORCT.U_ClientRef  AS ClientReference
FROM ORCT
INNER JOIN RCT2 ON RCT2.DocNum = ORCT.DocEntry AND RCT2.InvType = 13
INNER JOIN OINV ON OINV.DocEntry = RCT2.DocEntry
LEFT JOIN (
    SELECT TransId, MIN(Account) AS Account
    FROM JDT1 WHERE Debit > 0 GROUP BY TransId
) JDT1_D ON JDT1_D.TransId = ORCT.TransId
LEFT JOIN OACT ON OACT.AcctCode = JDT1_D.Account
LEFT JOIN OSLP ON OSLP.SlpCode  = OINV.SlpCode
WHERE ORCT.DocEntry = {paymentDocEntry}
ORDER BY RCT2.DocEntry");

            var results = new List<SapReplitAPI.Models.Payments.InvoicePaymentDto>();
            while (!rs.EoF)
            {
                results.Add(new SapReplitAPI.Models.Payments.InvoicePaymentDto
                {
                    DocEntry              = Convert.ToInt32(rs.Fields.Item("InvoiceDocEntry").Value),
                    PaymentDocEntry       = Convert.ToInt32(rs.Fields.Item("PaymentDocEntry").Value),
                    PaymentNumber         = Convert.ToInt32(rs.Fields.Item("PaymentNumber").Value),
                    InvoiceDocNum         = Convert.ToInt32(rs.Fields.Item("InvoiceDocNum").Value),
                    PaymentDate           = Convert.ToDateTime(rs.Fields.Item("PaymentDate").Value),
                    CardCode              = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                    CardName              = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                    AmountApplied         = Convert.ToDecimal(rs.Fields.Item("AmountApplied").Value),
                    BankTransferAmount    = Convert.ToDecimal(rs.Fields.Item("BankTransferAmount").Value),
                    BankTransferReference = rs.Fields.Item("BankTransferReference").Value?.ToString() ?? "",
                    DebitAccountCode      = rs.Fields.Item("DebitAccountCode").Value?.ToString() ?? "",
                    DebitAccountName      = rs.Fields.Item("DebitAccountName").Value?.ToString() ?? "",
                    SalesEmployeeCode     = rs.Fields.Item("SalesEmployeeCode").Value?.ToString() ?? "",
                    SalesEmployeeName     = rs.Fields.Item("SalesEmployeeName").Value?.ToString() ?? "",
                    ClientReference       = rs.Fields.Item("ClientReference").Value?.ToString() ?? "",
                    Canceled              = (rs.Fields.Item("Canceled").Value?.ToString() ?? "N") == "Y",
                    CounterRef            = rs.Fields.Item("CounterRef").Value?.ToString() ?? ""
                });
                rs.MoveNext();
            }
            return Task.FromResult(results);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Returns true if OINM contains at least one inventory movement for this document.
    /// transType = SAP object type (13 = OINV, 14 = ORIN).
    /// docNum    = SAP document number (DocNum, not DocEntry).
    /// A true result means the invoice/credit-memo directly drove a stock posting —
    /// important for surfacing physical inventory observability in metrics.
    /// </summary>
    public Task<bool> CheckOinmAsync(int transType, int docEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM OINM
    WHERE TransType = {transType}
      AND BaseEntry = {docEntry}
) THEN 1 ELSE 0 END AS HasMovement");

            bool hasMovement = !rs.EoF
                && Convert.ToInt32(rs.Fields.Item("HasMovement").Value) == 1;

            return Task.FromResult(hasMovement);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // OINM schema varies across SAP B1 versions/patches.
            // The probe is observability-only — a query failure must not block invoice processing.
            _logger?.LogWarning(ex, "[CheckOinmAsync] OINM probe failed for TransType={TransType} DocEntry={DocEntry} — returning false.", transType, docEntry);
            return Task.FromResult(false);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
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

    // ─── Incoming Payment Lookup / Cancel ───────────────────────────────────

    public sealed record OrctSummary(
        int      DocEntry,
        int      DocNum,
        bool     Canceled,
        string   CardCode,
        DateTime DocDate,
        decimal  DocTotal,
        string   CounterRef);

    public sealed record CancelPaymentResult(
        bool    Success,
        int?    SapErrorCode    = null,
        string? SapErrorMessage = null);

    public Task<OrctSummary?> GetOrctSummaryByDocEntryAsync(int paymentDocEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT DocEntry, DocNum,
       ISNULL(Canceled,'N') AS Canceled,
       ISNULL(CardCode,'')  AS CardCode,
       DocDate, DocTotal,
       ISNULL(CounterRef,'') AS CounterRef
FROM ORCT
WHERE DocEntry = {paymentDocEntry}");
            if (rs.EoF) return Task.FromResult<OrctSummary?>(null);
            return Task.FromResult<OrctSummary?>(new OrctSummary(
                DocEntry  : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                DocNum    : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                Canceled  : (rs.Fields.Item("Canceled").Value?.ToString() ?? "N") == "Y",
                CardCode  : rs.Fields.Item("CardCode").Value?.ToString()  ?? "",
                DocDate   : Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                DocTotal  : Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                CounterRef: rs.Fields.Item("CounterRef").Value?.ToString() ?? ""));
        }
        finally { if (rs != null) Marshal.ReleaseComObject(rs); }
    }

    public Task<List<OrctSummary>> GetOrctsByInvoiceDocEntryAsync(int invoiceDocEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT DISTINCT
    ORCT.DocEntry,
    ORCT.DocNum,
    ISNULL(ORCT.Canceled,'N') AS Canceled,
    ISNULL(ORCT.CardCode,'')  AS CardCode,
    ORCT.DocDate,
    ORCT.DocTotal,
    ISNULL(ORCT.CounterRef,'') AS CounterRef
FROM ORCT
INNER JOIN RCT2 ON RCT2.DocNum = ORCT.DocEntry AND RCT2.InvType = 13
WHERE RCT2.DocEntry = {invoiceDocEntry}");
            var results = new List<OrctSummary>();
            while (!rs.EoF)
            {
                results.Add(new OrctSummary(
                    DocEntry  : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum    : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    Canceled  : (rs.Fields.Item("Canceled").Value?.ToString() ?? "N") == "Y",
                    CardCode  : rs.Fields.Item("CardCode").Value?.ToString()  ?? "",
                    DocDate   : Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    DocTotal  : Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                    CounterRef: rs.Fields.Item("CounterRef").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            return Task.FromResult(results);
        }
        finally { if (rs != null) Marshal.ReleaseComObject(rs); }
    }

    [SupportedOSPlatform("windows")]
    public CancelPaymentResult CancelIncomingPayment(int paymentDocEntry)
    {
        var company = GetConnectedCompany();
        Payments? p = null;
        try
        {
            p = (Payments)company.GetBusinessObject(BoObjectTypes.oIncomingPayments);
            object keyResult = p.GetByKey(paymentDocEntry);
            if (!(keyResult is true) && p.DocEntry != paymentDocEntry)
                return new CancelPaymentResult(false, -1, $"Payment DocEntry {paymentDocEntry} not found in SAP.");

            int ret = p.Cancel();
            if (ret != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogError("[PaymentCancel] SAP refused cancellation DocEntry={DocEntry} [{Code}]: {Msg}",
                    paymentDocEntry, errCode, errMsg);
                return new CancelPaymentResult(false, errCode, errMsg);
            }
            _logger.LogInformation("[PaymentCancel] Cancelled ORCT DocEntry={DocEntry}", paymentDocEntry);
            return new CancelPaymentResult(true);
        }
        finally { if (p != null) Marshal.ReleaseComObject(p); }
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
                  AND  ISNULL(T0.U_ZoneRef, '') <> 'ZoneFulfillment'
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

    // ── SO → Delivery Automation ──────────────────────────────────────────────────

    /// <summary>
    /// Returns all open, non-cancelled Sales Orders whose DocDate equals processingDate.
    /// Ordered by DocEntry ASC. Returns empty list (never throws) when no records match.
    /// </summary>
    public List<OpenSoDto> GetOpenSosForDate(DateTime processingDate)
    {
        var company = GetConnectedCompany();
        var results = new List<OpenSoDto>();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            string dateStr = processingDate.ToString("yyyy-MM-dd");
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName,
                       T0.DocDate, T0.DocDueDate, T0.DocCur, T0.DocTotal
                FROM   ORDR T0
                WHERE  T0.DocDate   = '{dateStr}'
                  AND  T0.DocStatus = 'O'
                  AND  T0.CANCELED  = 'N'
                  AND  ISNULL(T0.U_ZoneRef, '') <> 'ZoneFulfillment'
                ORDER BY T0.DocEntry ASC");

            while (!rs.EoF)
            {
                object dueDateRaw = rs.Fields.Item("DocDueDate").Value;
                results.Add(new OpenSoDto
                {
                    DocEntry    = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum      = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    CardCode    = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                    CardName    = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                    DocDate     = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    DocDueDate  = dueDateRaw == null || dueDateRaw is DBNull
                                  ? null : Convert.ToDateTime(dueDateRaw),
                    DocCurrency = rs.Fields.Item("DocCur").Value?.ToString() ?? "TZS",
                    DocTotal    = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
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

    /// <summary>
    /// Returns a single open, non-cancelled Sales Order by DocEntry — regardless of DocDate.
    /// For backlog pilot use only. Do NOT use in the nightly batch (use GetOpenSosForDate for that).
    /// Returns null if the DocEntry does not exist, is closed (DocStatus='C'), or is cancelled.
    /// </summary>
    public OpenSoDto? GetOpenSoByDocEntry(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName,
                       T0.DocDate, T0.DocDueDate, T0.DocCur, T0.DocTotal
                FROM   ORDR T0
                WHERE  T0.DocEntry  = {docEntry}
                  AND  T0.DocStatus = 'O'
                  AND  T0.CANCELED  = 'N'");

            if (rs.EoF) return null;

            object dueDateRaw = rs.Fields.Item("DocDueDate").Value;
            return new OpenSoDto
            {
                DocEntry    = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                DocNum      = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                CardCode    = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                CardName    = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                DocDate     = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                DocDueDate  = dueDateRaw == null || dueDateRaw is DBNull
                              ? null : Convert.ToDateTime(dueDateRaw),
                DocCurrency = rs.Fields.Item("DocCur").Value?.ToString() ?? "TZS",
                DocTotal    = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
            };
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Returns the U_ZoneRef UDF value for the given ORDR DocEntry, or null if not found/empty.
    /// Used by the pilot delivery guard to refuse Zone Fulfillment-managed SOs.
    /// </summary>
    public string? GetSoUZoneRef(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT T0.U_ZoneRef FROM ORDR T0 WHERE T0.DocEntry = {docEntry}");
            if (rs.EoF) return null;
            return rs.Fields.Item("U_ZoneRef").Value?.ToString()?.Trim();
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Returns open RDR1 lines (LineStatus='O', OpenQty>0) for the given SO DocEntry.
    /// Ordered by LineNum ASC. OpenQty is the remaining un-delivered quantity — NOT original Quantity.
    /// WhsCode comes directly from RDR1; no warehouse substitution is performed here.
    /// </summary>
    public List<OpenSoLineDto> GetOpenSoLines(int docEntry)
    {
        var company = GetConnectedCompany();
        var results = new List<OpenSoLineDto>();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.LineNum, T0.ItemCode, T0.Dscription,
                       T0.Quantity, T0.OpenQty, T0.WhsCode
                FROM   RDR1 T0
                WHERE  T0.DocEntry   = {docEntry}
                  AND  T0.LineStatus = 'O'
                  AND  T0.OpenQty    > 0
                ORDER BY T0.LineNum ASC");

            while (!rs.EoF)
            {
                results.Add(new OpenSoLineDto
                {
                    DocEntry        = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    LineNum         = Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    ItemCode        = rs.Fields.Item("ItemCode").Value?.ToString()   ?? "",
                    ItemDescription = rs.Fields.Item("Dscription").Value?.ToString() ?? "",
                    Quantity        = Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    OpenQty         = Convert.ToDecimal(rs.Fields.Item("OpenQty").Value),
                    WhsCode         = rs.Fields.Item("WhsCode").Value?.ToString()    ?? "",
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

    /// <summary>
    /// Batch-queries live OITW stock for all unique (ItemCode, WhsCode) combinations in the
    /// given SO lines. Returns a dictionary keyed by (ItemCode.ToUpper(), WhsCode.ToUpper()) → OnHand.
    /// Callers MUST normalize keys to uppercase before lookup. Uses live SAP values — never the local
    /// product cache. Missing OITW rows are treated as OnHand = 0 (not present in the result).
    /// </summary>
    public Dictionary<(string ItemCode, string WhsCode), decimal> GetStockForSoLines(
        List<OpenSoLineDto> lines)
    {
        var result = new Dictionary<(string, string), decimal>();

        var uniquePairs = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemCode) && !string.IsNullOrWhiteSpace(l.WhsCode))
            .Select(l => (ItemCode: l.ItemCode.Trim(), WhsCode: l.WhsCode.Trim()))
            .GroupBy(p => (p.ItemCode.ToUpperInvariant(), p.WhsCode.ToUpperInvariant()))
            .Select(g => g.First())
            .ToList();

        if (uniquePairs.Count == 0) return result;

        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

            // One OR-condition per unique (ItemCode, WhsCode) pair — precise composite match,
            // avoids cross-product from separate IN clauses on each column.
            var conditions = string.Join(" OR ", uniquePairs.Select(p =>
                $"(T0.ItemCode = '{p.ItemCode.Replace("'", "''")}'" +
                $" AND T0.WhsCode = '{p.WhsCode.Replace("'", "''")}')"
            ));

            rs.DoQuery($@"
                SELECT T0.ItemCode, T0.WhsCode, ISNULL(T0.OnHand, 0) AS OnHand
                FROM   OITW T0
                WHERE  {conditions}");

            while (!rs.EoF)
            {
                string itemCode = rs.Fields.Item("ItemCode").Value?.ToString() ?? "";
                string whsCode  = rs.Fields.Item("WhsCode").Value?.ToString()  ?? "";
                decimal onHand  = Convert.ToDecimal(rs.Fields.Item("OnHand").Value);
                result[(itemCode.Trim().ToUpperInvariant(), whsCode.Trim().ToUpperInvariant())] = onHand;
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Reads fresh OITW (OnHand, IsCommited) for the given (ItemCode, WhsCode) pairs.
    /// Available = max(0, OnHand - IsCommited).
    /// Missing OITW rows are returned with Available=0.
    /// Called by SapOitwAdapter — not by legacy delivery code.
    /// </summary>
    public List<(string ItemCode, string WhsCode, decimal Available)> GetOitwAvailable(
        IEnumerable<(string ItemCode, string WhsCode)> pairs)
    {
        var uniquePairs = pairs
            .Select(p => (ItemCode: p.ItemCode.Trim(), WhsCode: p.WhsCode.Trim()))
            .Distinct()
            .ToList();

        var result = new List<(string, string, decimal)>();
        if (uniquePairs.Count == 0) return result;

        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

            var conditions = string.Join(" OR ", uniquePairs.Select(p =>
                $"(T0.ItemCode = '{p.ItemCode.Replace("'", "''")}'" +
                $" AND T0.WhsCode = '{p.WhsCode.Replace("'", "''")}')"
            ));

            rs.DoQuery($@"
                SELECT T0.ItemCode, T0.WhsCode,
                       ISNULL(T0.OnHand,    0) AS OnHand,
                       ISNULL(T0.IsCommited, 0) AS IsCommited
                FROM   OITW T0
                WHERE  {conditions}");

            while (!rs.EoF)
            {
                string itemCode  = rs.Fields.Item("ItemCode").Value?.ToString()  ?? "";
                string whsCode   = rs.Fields.Item("WhsCode").Value?.ToString()   ?? "";
                decimal onHand   = Convert.ToDecimal(rs.Fields.Item("OnHand").Value);
                decimal commited = Convert.ToDecimal(rs.Fields.Item("IsCommited").Value);
                decimal avail    = Math.Max(0m, onHand - commited);
                result.Add((itemCode.Trim(), whsCode.Trim(), avail));
                rs.MoveNext();
            }

            // Pad missing pairs with Available=0
            var found = result.Select(r => (r.Item1.ToUpperInvariant(), r.Item2.ToUpperInvariant())).ToHashSet();
            foreach (var p in uniquePairs)
            {
                if (!found.Contains((p.ItemCode.ToUpperInvariant(), p.WhsCode.ToUpperInvariant())))
                    result.Add((p.ItemCode, p.WhsCode, 0m));
            }

            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Zone Fulfillment — creates one Model-B Sales Order in SAP.
    /// One RDR1 line per AllocationFragment (RequestLineId × WhsCode).
    /// Fragments must be passed in insertion order (LineSeq then WHS priority).
    /// Returns DocEntry, DocNum?, and all RDR1 lines read back immediately after Add().
    /// Throws SapOrderAddException on SAP-definitive failure.
    /// Caller handles UnknownOutcome on any other exception.
    /// </summary>
    public (int DocEntry, int? DocNum, List<SapReplitAPI.Models.ZoneFulfillment.Rdr1Line> Rdr1Lines)
        CreateZoneFulfillmentOrder(
            string cardCode,
            DateTime docDate,
            DateTime deliveryDate,
            int? slpCode,
            string uReplitId,
            string deliveryLocation,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.AllocationFragment> orderedFragments,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.DomainRequestLine>  requestLines)
    {
        var company = GetConnectedCompany();
        var order   = (Documents)company.GetBusinessObject(BoObjectTypes.oOrders);

        order.CardCode                = cardCode;
        order.DocDate                 = docDate;
        order.TaxDate                 = docDate;
        order.DocDueDate              = deliveryDate;
        order.DocCurrency             = "TZS";
        order.Series                  = 8;
        order.BPL_IDAssignedToInvoice = 1;

        if (slpCode.HasValue)
            order.SalesPersonCode = slpCode.Value;

        order.UserFields.Fields.Item("U_ZoneRef").Value          = "ZoneFulfillment";
        order.UserFields.Fields.Item("U_DeliveryLocation").Value = deliveryLocation;
        order.UserFields.Fields.Item("U_ReplitId").Value         = uReplitId;

        var lineById = requestLines.ToDictionary(l => l.RequestLineId);

        // Batch-fetch OITM description for all unique ItemCodes.
        // Field order: U_Item_Name → U_MdlTEST → ItemName (business-defined).
        // This enriches RDR1.Dscription — the field the pick list UI joins to show line descriptions.
        var itemCodes = orderedFragments
            .Select(f => lineById.TryGetValue(f.RequestLineId, out var l) ? l.ItemCode : "")
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct();
        var oitmMap = QueryOitm(company, itemCodes);

        foreach (var frag in orderedFragments)
        {
            if (!lineById.TryGetValue(frag.RequestLineId, out var reqLine))
                throw new InvalidOperationException($"Fragment references unknown RequestLineId {frag.RequestLineId}");

            // OITM-derived description (authoritative source of truth).
            // Fallback chain: OITM fields → request payload → ItemCode.
            string desc;
            if (oitmMap.TryGetValue(reqLine.ItemCode, out var oitmInfo))
            {
                desc = BuildDescription(oitmInfo.ItemName, oitmInfo.Model, oitmInfo.SapName, reqLine.ItemCode);
                _logger.LogInformation("[ZF-SAP] Line ItemCode={Item} OITM desc='{Desc}'", reqLine.ItemCode, desc);
            }
            else
            {
                desc = reqLine.Description ?? reqLine.U_ItemName ?? reqLine.ItemCode;
                _logger.LogWarning("[ZF-SAP] Line ItemCode={Item} not in OITM — falling back to request payload: '{Desc}'", reqLine.ItemCode, desc);
            }

            // Enforce RDR1.Dscription field length — never rely on silent SAP truncation.
            int maxDescLen = GetRdr1DescriptionMaxLength();
            if (desc.Length > maxDescLen)
            {
                _logger.LogWarning(
                    "[ZF-SAP] ZoneFulfillmentDescriptionTruncated ItemCode={Item} OriginalLength={Orig} StoredLength={Stored}",
                    reqLine.ItemCode, desc.Length, maxDescLen);
                desc = desc[..maxDescLen];
            }

            order.Lines.ItemCode        = reqLine.ItemCode;
            order.Lines.Quantity        = (double)frag.SoLineQty;
            order.Lines.Price           = (double)reqLine.UnitPrice;
            order.Lines.VatGroup        = "TZ";
            order.Lines.WarehouseCode   = frag.WhsCode;
            order.Lines.ItemDescription = desc;

            if (!string.IsNullOrWhiteSpace(reqLine.U_ItemName))
                order.Lines.UserFields.Fields.Item("U_ItemName").Value = reqLine.U_ItemName;
            if (!string.IsNullOrWhiteSpace(reqLine.U_Manufacturer))
                order.Lines.UserFields.Fields.Item("U_Manufacturer").Value = reqLine.U_Manufacturer;

            order.Lines.Add();
        }

        int addRc = order.Add();
        if (addRc != 0)
        {
            string sapErr = company.GetLastErrorDescription();
            _logger.LogError("[ZF-SAP] ORDR.Add() rc={Rc} uReplitId={ReplitId} err='{Err}'", addRc, uReplitId, sapErr);
            throw new SapReplitAPI.Services.ZoneFulfillment.SapOrderAddException(addRc, sapErr);
        }

        int docEntry = int.Parse(company.GetNewObjectKey());

        // Read back RDR1 immediately
        var rdr1 = ReadRdr1ForZf(company, docEntry);
        int? docNum = GetDocNumForZf(company, docEntry);

        _logger.LogInformation("[ZF-SAP] ORDR.Add() SUCCESS DocEntry={DocEntry} DocNum={DocNum} uReplitId={ReplitId} lines={N}",
            docEntry, docNum, uReplitId, rdr1.Count);

        return (docEntry, docNum, rdr1);
    }

    /// <summary>Checks if a non-cancelled ORDR with the given U_ReplitId already exists.</summary>
    public (int DocEntry, int DocNum)? FindZoneFulfillmentOrder(string uReplitId)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum
                FROM   ORDR T0
                WHERE  T0.U_ReplitId = '{uReplitId.Replace("'", "''")}'
                  AND  T0.CANCELED   = N'N'");
            if (rs.EoF) return null;
            return (Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    Convert.ToInt32(rs.Fields.Item("DocNum").Value));
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ── Zone Fulfillment OITM description helpers ─────────────────────────────

    /// <summary>
    /// Returns the three OITM description components for one ItemCode plus the
    /// joined description string. Used to populate PKL1.Dscription at pick list creation.
    /// Field order: U_Item_Name → U_MdlTEST → ItemName (business-defined).
    /// </summary>
    public (string? UItemName, string? UMdlTest, string? ItemName, string Description)
        GetZoneFulfillmentItemDescription(string itemCode)
    {
        var company = GetConnectedCompany();
        var oitm = QueryOitm(company, new[] { itemCode });

        if (!oitm.TryGetValue(itemCode, out var info))
        {
            _logger.LogWarning("[ZF-PL-DESC] OITM not found for ItemCode={Item} — using ItemCode as fallback", itemCode);
            return (null, null, null, itemCode);
        }

        // Field order is business-defined: U_Item_Name → U_MdlTEST → ItemName
        // QueryOitm returns: (ItemName=U_Item_Name, Model=U_MdlTEST, SapName=ItemName)
        string desc = BuildDescription(info.ItemName, info.Model, info.SapName, itemCode);

        _logger.LogInformation(
            "[ZF-PL-DESC] OITM ItemCode={Item} U_Item_Name='{A}' U_MdlTEST='{B}' ItemName='{C}' → Description='{D}'",
            itemCode, info.ItemName, info.Model, info.SapName, desc);

        return (info.ItemName, info.Model, info.SapName, desc);
    }

    // Cached on first call — RDR1.Dscription length does not change at runtime.
    private static int _rdr1DescMaxLength;

    private int GetRdr1DescriptionMaxLength()
    {
        if (_rdr1DescMaxLength > 0) return _rdr1DescMaxLength;
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery(
                "SELECT c.max_length FROM sys.columns c " +
                "JOIN sys.tables tb ON c.object_id = tb.object_id " +
                "WHERE tb.name = 'RDR1' AND c.name = 'Dscription'");
            int charLen = 100; // conservative SAP B1 default if query returns nothing
            if (!rs.EoF)
            {
                // sys.columns.max_length is in bytes; nvarchar stores 2 bytes/char; -1 = nvarchar(max)
                int rawLen = Convert.ToInt32(rs.Fields.Item("max_length").Value);
                charLen = rawLen == -1 ? int.MaxValue : rawLen / 2;
            }
            _rdr1DescMaxLength = charLen;
            _logger.LogInformation("[ZF-SAP] RDR1.Dscription char max_length={Len}", charLen);
            return charLen;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Creates one OPKL (Pick List) for a single Zone Fulfillment SO line.
    /// lineDescription is written to PKL1.Dscription via pl.Lines.ItemDescription.
    /// SAP auto-assigns bin allocation for bin-managed warehouses.
    /// Throws SapPickListAddException on rc != 0 (definitive SAP failure).
    /// Any other exception → UnknownOutcome (caller handles).
    /// </summary>
    public int CreateZoneFulfillmentPickList(
        int     soDocEntry,
        int     soLineNum,
        double  releasedQty,
        string  uReplitId,
        string? lineDescription = null,
        int?    ownerCode       = null)
    {
        var company = GetConnectedCompany();
        // oPickLists is not Documents — use dynamic to avoid COM interface mismatch
        dynamic pl = company.GetBusinessObject(BoObjectTypes.oPickLists);

        pl.UserFields.Fields.Item("U_ReplitId").Value = uReplitId;

        // Bug #1 fix: stamp picker assignment before Add()
        if (ownerCode.HasValue)
            pl.OwnerCode = ownerCode.Value;

        pl.Lines.OrderEntry       = soDocEntry;
        pl.Lines.OrderRowID       = soLineNum;
        pl.Lines.ReleasedQuantity = releasedQty;
        pl.Lines.BaseObjectType   = (int)BoObjectTypes.oOrders;   // 17

        // NOTE: PickListsLine COM object (SAPbobsCOM PL18) does NOT expose ItemDescription.
        // PKL1 has no Dscription column in this installation.
        // Description is shown in the SAP B1 UI by joining to the source SO line (RDR1.Dscription).
        // lineDescription is logged for audit but cannot be written to the pick list directly.
        if (!string.IsNullOrWhiteSpace(lineDescription))
            _logger.LogInformation("[ZF-PL] LineDescription='{Desc}' derived from OITM but PickListsLine has no ItemDescription property in SAPbobsCOM PL18 — description not written to OPKL/PKL1", lineDescription);

        int rc = (int)pl.Add();
        if (rc != 0)
        {
            string sapErr = company.GetLastErrorDescription();
            _logger.LogError("[ZF-PL] OPKL.Add() rc={Rc} soDocEntry={DocEntry} soLineNum={LineNum} err='{Err}'",
                rc, soDocEntry, soLineNum, sapErr);
            throw new SapReplitAPI.Services.ZoneFulfillment.SapPickListAddException(rc, sapErr);
        }

        int absEntry = int.Parse(company.GetNewObjectKey());
        _logger.LogInformation(
            "[ZF-PL] OPKL.Add() SUCCESS AbsEntry={Abs} soDocEntry={DocEntry} soLineNum={LineNum} uReplitId={Rid} description='{Desc}'",
            absEntry, soDocEntry, soLineNum, uReplitId, lineDescription ?? "(none)");
        return absEntry;
    }

    /// <summary>
    /// Bug #2 fix (Section 13): Creates ONE OPKL with N PKL1 lines — one per SoLineFragment.
    /// All lines must belong to the same WHS group.
    /// pl.OwnerCode = ownerCode is stamped before Add() (Bug #1 fix).
    /// Gate: caller must ensure PICK_LIST_MUTATION_ENABLED = true before invoking.
    /// </summary>
    public int CreateZoneFulfillmentPickListMultiLine(
        string                                                               uReplitId,
        int                                                                  ownerCode,
        IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.PickListLineSpec>  lines)
    {
        if (lines.Count == 0)
            throw new ArgumentException("At least one PickListLineSpec is required.", nameof(lines));

        var company = GetConnectedCompany();
        dynamic pl = company.GetBusinessObject(BoObjectTypes.oPickLists);

        pl.UserFields.Fields.Item("U_ReplitId").Value = uReplitId;
        pl.OwnerCode = ownerCode;

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) pl.Lines.Add();
            pl.Lines.OrderEntry       = lines[i].SoDocEntry;
            pl.Lines.OrderRowID       = lines[i].SoLineNum;
            pl.Lines.ReleasedQuantity = lines[i].ReleasedQty;
            pl.Lines.BaseObjectType   = (int)BoObjectTypes.oOrders;  // 17
        }

        int rc = (int)pl.Add();
        if (rc != 0)
        {
            string sapErr = company.GetLastErrorDescription();
            _logger.LogError("[ZF-PL] OPKL.Add() (multi-line) rc={Rc} uReplitId={Rid} lineCount={N} err='{Err}'",
                rc, uReplitId, lines.Count, sapErr);
            throw new SapReplitAPI.Services.ZoneFulfillment.SapPickListAddException(rc, sapErr);
        }

        int absEntry = int.Parse(company.GetNewObjectKey());
        _logger.LogInformation(
            "[ZF-PL] OPKL.Add() (multi-line) SUCCESS AbsEntry={Abs} uReplitId={Rid} lineCount={N} ownerCode={Owner}",
            absEntry, uReplitId, lines.Count, ownerCode);
        return absEntry;
    }

    /// <summary>
    /// Finds an existing non-cancelled OPKL by U_ReplitId.
    /// Returns AbsEntry if found, null if not found.
    /// Used for UnknownOutcome recovery only.
    /// </summary>
    public int? FindZoneFulfillmentPickList(string uReplitId)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT T0.AbsEntry FROM OPKL T0 WHERE T0.U_ReplitId = N'{uReplitId.Replace("'", "''")}' AND T0.Canceled = N'N'");
            if (rs.EoF) return null;
            return Convert.ToInt32(rs.Fields.Item("AbsEntry").Value);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Finds an existing non-cancelled OPKL for a specific SO line fragment.
    /// Used for atomicity recovery: when OPKL.Add() succeeded on a prior attempt but
    /// MolasIntegration persist failed, the SAP record already exists and must be
    /// recovered rather than duplicated.
    /// Returns AbsEntry if found, null if not found.
    /// </summary>
    public int? FindZoneFulfillmentPickListForFragment(string uReplitId, int soDocEntry, int soLineNum)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT O.AbsEntry
                FROM   OPKL O
                JOIN   PKL1 P ON O.AbsEntry = P.AbsEntry
                WHERE  O.U_ReplitId = N'{uReplitId.Replace("'", "''")}'
                  AND  O.Canceled   = N'N'
                  AND  P.OrderEntry = {soDocEntry}
                  AND  P.OrderLine  = {soLineNum}");
            if (rs.EoF) return null;
            return Convert.ToInt32(rs.Fields.Item("AbsEntry").Value);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ── C-SAP-03: Pick execution helpers ─────────────────────────────────────────

    /// <summary>
    /// Reads live PKL1 state for one exact pick list line.
    /// Used for desired-state idempotency check (crash recovery) and post-mutation verification.
    /// Read-only — no SAP document is modified.
    /// </summary>
    public SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState? ReadZoneFulfillmentPickListLine(
        int absEntry, int soDocEntry, int soLineNum)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT P.AbsEntry, P.OrderEntry, P.OrderLine, P.BaseObject,
                       P.RelQtty, P.PickQtty, P.PickStatus
                FROM   PKL1 P
                WHERE  P.AbsEntry   = {absEntry}
                  AND  P.OrderEntry = {soDocEntry}
                  AND  P.OrderLine  = {soLineNum}");
            if (rs.EoF) return null;
            return new SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState(
                AbsEntry   : Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                OrderEntry : Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                OrderLine  : Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                BaseObject : Convert.ToInt32(rs.Fields.Item("BaseObject").Value),
                RelQtty    : Convert.ToDecimal(rs.Fields.Item("RelQtty").Value),
                PickQtty   : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value),
                PickStatus : rs.Fields.Item("PickStatus").Value?.ToString() ?? "");
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// §3 Bin availability: AvailableForPick = OIBQ.OnHandQty (physical on-hand).
    /// OBBQ pick-reservation data is NOT AVAILABLE in this SAP B1 schema (Build 1000280 PL18).
    /// Pre-mutation gate (§4) re-reads OIBQ per bin immediately before pl.Update().
    /// Never returns a bin where physical qty <= 0.
    /// Sorted by OnHandQty DESC, BinCode.
    /// </summary>
    public List<SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc> QueryBinForPick(
        string itemCode, string whsCode)
    {
        var company = GetConnectedCompany();

        // §3: Subtract active OBBQ commitments per bin (fail-safe: empty dict on schema mismatch)
        var obbqCommitted = TryQueryObbqCommittedByBin(company, itemCode, whsCode);

        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT B.AbsEntry AS BinAbsEntry, B.BinCode, I.OnHandQty
                FROM   OIBQ I
                JOIN   OBIN B ON I.BinAbs = B.AbsEntry
                WHERE  I.ItemCode = N'{itemCode.Replace("'", "''")}'
                  AND  I.WhsCode  = N'{whsCode.Replace("'", "''")}'
                  AND  I.OnHandQty > 0
                ORDER  BY I.OnHandQty DESC, B.BinCode");

            var list = new List<SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc>();
            while (!rs.EoF)
            {
                int     binAbs    = Convert.ToInt32(rs.Fields.Item("BinAbsEntry").Value);
                string  binCode   = rs.Fields.Item("BinCode").Value?.ToString()?.Trim() ?? "";
                decimal physQty   = Convert.ToDecimal(rs.Fields.Item("OnHandQty").Value);
                decimal committed = obbqCommitted.GetValueOrDefault(binAbs, 0m);
                decimal effective = physQty - committed;

                _logger.LogInformation(
                    "[ZF-PICK] BinAvail Item={Item} Whs={Whs} Bin={Bin} Physical={Phys} Effective={Eff}",
                    itemCode, whsCode, binCode, physQty, effective);

                if (effective > 0)
                    list.Add(new SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc(binAbs, binCode, effective));

                rs.MoveNext();
            }

            // Re-sort by effective qty descending (OIBQ ORDER BY was on OnHandQty, not effective)
            list.Sort((a, b) => b.Qty.CompareTo(a.Qty));

            _logger.LogInformation("[ZF-PICK] OBBQ-filtered bins: Item={Item} Whs={Whs} eligible={N}",
                itemCode, whsCode, list.Count);
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// OBBQ pick-reservation awareness: NOT APPLICABLE for this SAP B1 schema.
    /// Live schema confirmed (Build 1000280 PL18): OBBQ has columns
    /// AbsEntry, ItemCode, SnBMDAbs, BinAbs, OnHandQty, WhsCode — no CommQtty.
    /// OBBQ is the batch/serial bin quantity table, not a pick-reservation table.
    /// Pick reservations are enforced by the SAP DI API engine at pl.Update() time.
    /// Bin availability uses OIBQ.OnHandQty only; pre-mutation gate re-reads OIBQ per bin.
    /// </summary>
    private static Dictionary<int, decimal> TryQueryObbqCommittedByBin(
        SAPbobsCOM.Company company, string itemCode, string whsCode)
        => new();

    /// <summary>
    /// Queries OITW for live OnHand/IsCommited/OnOrder for pre-mutation gate.
    /// </summary>
    public SapReplitAPI.Models.ZoneFulfillment.OitwState? QueryPickListOitw(
        string itemCode, string whsCode)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT OnHand, IsCommited, OnOrder
                FROM   OITW
                WHERE  ItemCode = N'{itemCode.Replace("'", "''")}'
                  AND  WhsCode  = N'{whsCode.Replace("'", "''")}'");
            if (rs.EoF) return null;
            return new SapReplitAPI.Models.ZoneFulfillment.OitwState(
                ItemCode   : itemCode,
                WhsCode    : whsCode,
                OnHand     : Convert.ToDecimal(rs.Fields.Item("OnHand").Value),
                IsCommited : Convert.ToDecimal(rs.Fields.Item("IsCommited").Value),
                OnOrder    : Convert.ToDecimal(rs.Fields.Item("OnOrder").Value));
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// §4: Re-reads OIBQ+OBBQ for each selected bin immediately before pl.Update().
    /// Throws BinReservationConflictException if any bin's effective available < requested qty.
    /// </summary>
    private void RecheckBinAvailabilityOrThrow(
        SAPbobsCOM.Company company,
        string itemCode,
        string whsCode,
        IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc> binAllocs)
    {
        var obbqNow = TryQueryObbqCommittedByBin(company, itemCode, whsCode);
        foreach (var alloc in binAllocs)
        {
            if (alloc.Qty <= 0) continue;
            Recordset recheckRs = null;
            decimal physQty = 0m;
            try
            {
                recheckRs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                recheckRs.DoQuery($@"
                    SELECT OnHandQty FROM OIBQ
                    WHERE  BinAbs   = {alloc.BinAbsEntry}
                      AND  ItemCode = N'{itemCode.Replace("'", "''")}'
                      AND  WhsCode  = N'{whsCode.Replace("'", "''")}'");
                if (!recheckRs.EoF)
                    physQty = Convert.ToDecimal(recheckRs.Fields.Item("OnHandQty").Value);
            }
            finally { if (recheckRs != null) Marshal.ReleaseComObject(recheckRs); }

            decimal committed = obbqNow.GetValueOrDefault(alloc.BinAbsEntry, 0m);
            decimal effective = physQty - committed;

            _logger.LogInformation(
                "[ZF-PICK-GATE] §4 Pre-mutation recheck Bin={Bin} Physical={Phys} Committed={Comm} Effective={Eff} Requested={Req}",
                alloc.BinCode, physQty, committed, effective, alloc.Qty);

            if (effective < alloc.Qty)
            {
                throw new SapReplitAPI.Models.ZoneFulfillment.BinReservationConflictException(
                    new SapReplitAPI.Models.ZoneFulfillment.BinReservationConflict
                    {
                        ItemCode              = itemCode,
                        WhsCode               = whsCode,
                        BinAbsEntry           = alloc.BinAbsEntry,
                        BinCode               = alloc.BinCode,
                        PhysicalQty           = physQty,
                        CommittedQty          = committed,
                        EffectiveAvailableQty = effective,
                        RequestedPickQty      = alloc.Qty
                    });
            }
        }
    }

    /// <summary>§6: Returns true if ORDR.CANCELED='Y' for the given DocEntry.</summary>
    public bool GetSapOrderCancelledState(int soDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT CANCELED FROM ORDR WHERE DocEntry = {soDocEntry}");
            if (rs.EoF) return false;
            return (rs.Fields.Item("CANCELED").Value?.ToString() ?? "N") == "Y";
        }
        finally { if (rs != null) Marshal.ReleaseComObject(rs); }
    }

    /// <summary>
    /// Queries ORDR UDFs + DocStatus + Canceled for pre-mutation gate traceability.
    /// </summary>
    public SapReplitAPI.Models.ZoneFulfillment.SoUdfState? GetZoneFulfillmentSoUdfs(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT U_ZoneRef, U_DeliveryLocation, U_ReplitId, DocStatus, CANCELED
                FROM   ORDR
                WHERE  DocEntry = {docEntry}");
            if (rs.EoF) return null;
            return new SapReplitAPI.Models.ZoneFulfillment.SoUdfState(
                UZoneRef          : rs.Fields.Item("U_ZoneRef").Value?.ToString(),
                UDeliveryLocation : rs.Fields.Item("U_DeliveryLocation").Value?.ToString(),
                UReplitId         : rs.Fields.Item("U_ReplitId").Value?.ToString(),
                DocStatus         : rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                Canceled          : rs.Fields.Item("CANCELED").Value?.ToString() ?? "");
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Executes a SAP DI API pick list update — sets PickedQuantity and bin allocation.
    /// IMPLEMENT ONLY — DO NOT CALL until explicit per-fragment authorization.
    ///
    /// Safeguards:
    ///   - Loads OPKL by AbsEntry via GetByKey
    ///   - Locates exact line by (OrderEntry, OrderRowID)
    ///   - Validates desiredPickedQty >= 0 and <= ReleasedQuantity
    ///   - Sets PickedQuantity (desired-state, not delta)
    ///   - Applies BinAllocations for bin-managed warehouses using live OIBQ data
    ///   - Calls pl.Update()
    ///   - Reads PKL1 back immediately for post-mutation verification
    ///
    /// No direct SQL UPDATE to OPKL/PKL1/OIBQ/OITW/OBBQ. DI API only.
    /// </summary>
    public (int Rc, string? SapError, SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState? PostState)
        UpdateZoneFulfillmentPickList(
            int absEntry,
            int soDocEntry,
            int soLineNum,
            double desiredPickedQty,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc> binAllocs,
            string itemCode = "",
            string whsCode  = "")
    {
        try
        {
            return UpdateZoneFulfillmentPickListCore(absEntry, soDocEntry, soLineNum, desiredPickedQty, binAllocs, itemCode, whsCode);
        }
        catch (Exception ex) when (ex is not SapReplitAPI.Services.ZoneFulfillment.SapPickListUpdateException
                                   && ex is not SapReplitAPI.Models.ZoneFulfillment.BinReservationConflictException)
        {
            _logger.LogError(ex,
                "[ZF-PICK] UpdateZoneFulfillmentPickList threw {Type}: {Msg}",
                ex.GetType().Name, ex.Message);
            throw new SapReplitAPI.Services.ZoneFulfillment.SapPickListUpdateException(-9999,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private (int Rc, string? SapError, SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState? PostState)
        UpdateZoneFulfillmentPickListCore(
            int absEntry,
            int soDocEntry,
            int soLineNum,
            double desiredPickedQty,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc> binAllocs,
            string itemCode = "",
            string whsCode  = "")
    {
        var company = GetConnectedCompany();
        dynamic pl = company.GetBusinessObject(BoObjectTypes.oPickLists);

        // In SAPbobsCOM PL18, oPickLists.GetByKey() returns bool, not int.
        object keyResult = pl.GetByKey(absEntry);
        bool getKeyOk = keyResult is bool b ? b : (int)keyResult == 0;
        if (!getKeyOk)
        {
            string loadErr = company.GetLastErrorDescription();
            _logger.LogError("[ZF-PICK] GetByKey({Abs}) failed err='{Err}'", absEntry, loadErr);
            return (-1, loadErr, null);
        }

        // Locate the exact COM line index using PKL1.PickEntry ordering.
        // Identity is (AbsEntry, soDocEntry, soLineNum) — OrderEntry alone is ambiguous
        // in multi-line OPKLs where all lines share the same SO DocEntry.
        // PKL1 is queried before GetByKey; the COM object pl.Lines is ordered by PickEntry
        // (SAP insertion order), so the 0-based PickEntry-ordered position = COM index.
        int matchedLine;
        {
            Recordset pkRs = null;
            try
            {
                pkRs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                pkRs.DoQuery($@"
                    SELECT PickEntry, OrderEntry, OrderLine
                    FROM   PKL1
                    WHERE  AbsEntry = {absEntry}
                    ORDER  BY PickEntry");
                int idx = 0;
                int found = -1;
                while (!pkRs.EoF)
                {
                    int oe = Convert.ToInt32(pkRs.Fields.Item("OrderEntry").Value);
                    int ol = Convert.ToInt32(pkRs.Fields.Item("OrderLine").Value);
                    if (oe == soDocEntry && ol == soLineNum) { found = idx; break; }
                    idx++;
                    pkRs.MoveNext();
                }
                matchedLine = found;
            }
            finally { if (pkRs != null) Marshal.ReleaseComObject(pkRs); }
        }
        if (matchedLine < 0)
        {
            string msg = $"PKL1 has no line for AbsEntry={absEntry} SO={soDocEntry} Line={soLineNum}";
            _logger.LogError("[ZF-PICK] {Msg}", msg);
            throw new InvalidOperationException(msg);
        }

        pl.Lines.SetCurrentLine(matchedLine);
        double released = (double)pl.Lines.ReleasedQuantity;
        if (desiredPickedQty < 0 || desiredPickedQty > released)
            throw new InvalidOperationException(
                $"desiredPickedQty {desiredPickedQty} is out of range [0, {released}]");

        // Desired-state assignment — not additive
        pl.Lines.PickedQuantity = desiredPickedQty;

        // Bin-managed warehouse: SAP silently ignores PickedQuantity without BinAllocation entries.
        // GetByKey loads existing PKL2 rows into BinAllocations — zero them all out first so
        // stale allocations (e.g. a bin reserved by an orphaned pick list) don't block Update().
        int existingBinRows = 0;
        try { existingBinRows = (int)pl.Lines.BinAllocations.Count; } catch { existingBinRows = 0; }
        for (int i = 0; i < existingBinRows; i++)
        {
            pl.Lines.BinAllocations.SetCurrentLine(i);
            pl.Lines.BinAllocations.Quantity = 0.0;
        }

        if (binAllocs.Count > 0)
        {
            for (int i = 0; i < binAllocs.Count; i++)
            {
                if (i < existingBinRows)
                    pl.Lines.BinAllocations.SetCurrentLine(i);
                else
                    pl.Lines.BinAllocations.Add();
                pl.Lines.BinAllocations.BinAbsEntry = binAllocs[i].BinAbsEntry;
                pl.Lines.BinAllocations.Quantity    = (double)binAllocs[i].Qty;
            }
            _logger.LogInformation("[ZF-PICK] Set {N} bin allocation(s) for AbsEntry={Abs}",
                binAllocs.Count, absEntry);
        }

        // §4: Fail-closed pre-mutation OBBQ recheck — re-read availability just before pl.Update()
        if (binAllocs.Count > 0 && !string.IsNullOrEmpty(itemCode))
            RecheckBinAvailabilityOrThrow(company, itemCode, whsCode, binAllocs);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int rc = (int)pl.Update();
        sw.Stop();

        if (rc != 0)
        {
            string sapErr = company.GetLastErrorDescription();
            _logger.LogError("[ZF-PICK] pl.Update() rc={Rc} AbsEntry={Abs} elapsed={Ms}ms err='{Err}'",
                rc, absEntry, sw.ElapsedMilliseconds, sapErr);
            return (rc, sapErr, null);
        }

        _logger.LogInformation("[ZF-PICK] pl.Update() SUCCESS AbsEntry={Abs} soDocEntry={Doc} soLineNum={Line} pickedQty={Qty} elapsed={Ms}ms",
            absEntry, soDocEntry, soLineNum, desiredPickedQty, sw.ElapsedMilliseconds);

        // Read back immediately from PKL1
        var postState = ReadZoneFulfillmentPickListLine(absEntry, soDocEntry, soLineNum);
        return (0, null, postState);
    }

    private static List<SapReplitAPI.Models.ZoneFulfillment.Rdr1Line> ReadRdr1ForZf(Company company, int docEntry)
    {
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.LineNum, T0.ItemCode, T0.WhsCode, T0.Quantity, T0.OpenQty
                FROM   RDR1 T0
                WHERE  T0.DocEntry = {docEntry}
                ORDER  BY T0.LineNum ASC");
            var list = new List<SapReplitAPI.Models.ZoneFulfillment.Rdr1Line>();
            while (!rs.EoF)
            {
                list.Add(new SapReplitAPI.Models.ZoneFulfillment.Rdr1Line(
                    Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    rs.Fields.Item("ItemCode").Value?.ToString()?.Trim() ?? "",
                    rs.Fields.Item("WhsCode").Value?.ToString()?.Trim()  ?? "",
                    Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    Convert.ToDecimal(rs.Fields.Item("OpenQty").Value)));
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    private static int? GetDocNumForZf(Company company, int docEntry)
    {
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM ORDR WHERE DocEntry = {docEntry}");
            return rs.EoF ? null : Convert.ToInt32(rs.Fields.Item("DocNum").Value);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Creates one Delivery Note from the given open Sales Order lines.
    /// All-or-nothing: all lines must pass stock validation or no delivery is created.
    /// Performs a live stock revalidation immediately before delivery.Add() as a safety gate.
    /// Warehouse: taken exactly from each SO line — no substitution.
    /// BaseType=17 (oOrders), BaseEntry=so.DocEntry, BaseLine=line.LineNum, Qty=line.OpenQty.
    /// </summary>
    public CreateDeliveryResult CreateDeliveryFromSo(
        OpenSoDto so, List<OpenSoLineDto> lines, DateTime deliveryDate)
    {
        // Defensive: exclude any lines with OpenQty <= 0 or missing ItemCode/WhsCode.
        // GetOpenSoLines already filters these, but this gate is a safety net.
        var deliverableLines = lines
            .Where(l => l.OpenQty > 0
                     && !string.IsNullOrWhiteSpace(l.ItemCode)
                     && !string.IsNullOrWhiteSpace(l.WhsCode))
            .ToList();

        if (deliverableLines.Count == 0)
            return new CreateDeliveryResult
            {
                Success         = false,
                FailureType     = DeliveryFailureType.NoOpenLines,
                SapErrorMessage = $"SO {so.DocEntry}: no deliverable lines (all OpenQty <= 0 or missing ItemCode/WhsCode).",
            };

        var company = GetConnectedCompany();
        Documents delivery = null;
        Recordset rs       = null;
        try
        {
            // ── Build Delivery Note ───────────────────────────────────────────────
            delivery = (Documents)company.GetBusinessObject(BoObjectTypes.oDeliveryNotes);
            delivery.CardCode                = so.CardCode;
            delivery.DocDate                 = deliveryDate;   // nightly processing date — NOT so.DocDate
            delivery.TaxDate                 = deliveryDate;
            delivery.DocDueDate              = deliveryDate;
            delivery.DocCurrency             = so.DocCurrency;
            delivery.BPL_IDAssignedToInvoice = 1;

            // ── Shared bin allocation state for the entire Delivery ──────────────────
            // binStockMap  : key=(ItemCode|WhsCode, normalised uppercase)
            //                value=list of BinStockEntry; Remaining is decremented in place
            //                as each line allocates — same physical bin qty cannot be reused.
            // whsBinCache  : WhsCode → bool (avoids re-querying OWHS per line).
            // itemMgmtCache: ItemCode → (isBatch, isSerial) (avoids re-querying OITM per line).
            // lineBinAllocs: lineIdx → entries allocated for that line (for pre-Add validation).
            var binStockMap   = new Dictionary<string, List<BinStockEntry>>(StringComparer.OrdinalIgnoreCase);
            var whsBinCache   = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var itemMgmtCache = new Dictionary<string, (bool IsBatch, bool IsSerial)>(StringComparer.OrdinalIgnoreCase);
            var lineBinAllocs = new Dictionary<int, List<BinAllocationEntry>>();

            for (int i = 0; i < deliverableLines.Count; i++)
            {
                if (i > 0) delivery.Lines.Add();

                var line = deliverableLines[i];
                delivery.Lines.BaseType      = 17;                            // oOrders — Sales Order
                delivery.Lines.BaseEntry     = so.DocEntry;
                delivery.Lines.BaseLine      = line.LineNum;
                delivery.Lines.Quantity      = (double)line.OpenQty;
                delivery.Lines.WarehouseCode = line.WhsCode;                  // exact SO line warehouse, no substitution

                // Bin location allocation — normal items in bin-managed warehouses only.
                // Batch/serial items are explicitly blocked (unsupported flow).
                // Non-bin warehouses produce NotRequired and are skipped below.
                // The shared binStockMap ensures duplicate item/warehouse lines draw from
                // the same pool — the same physical bin quantity cannot be allocated twice.
                var (binStatus, binEntries, binErr) = ResolveBinAllocationsForLine(
                    company, line.ItemCode, line.WhsCode, line.OpenQty,
                    binStockMap, whsBinCache, itemMgmtCache);

                switch (binStatus)
                {
                    case BinAllocStatus.UnsupportedBatch:
                    case BinAllocStatus.UnsupportedSerial:
                    case BinAllocStatus.InsufficientStock:
                        _logger.LogError(
                            "[CreateDeliveryFromSo] Bin allocation blocked — SO {SoDocEntry} Line {Ln} Item {Item}: {Reason}",
                            so.DocEntry, line.LineNum, line.ItemCode, binErr);
                        return new CreateDeliveryResult
                        {
                            Success         = false,
                            FailureType     = DeliveryFailureType.SapError,
                            SapErrorMessage = binErr,
                        };

                    case BinAllocStatus.NotRequired:
                        lineBinAllocs[i] = new List<BinAllocationEntry>();
                        break;

                    case BinAllocStatus.Allocated:
                        lineBinAllocs[i] = binEntries;
                        foreach (var entry in binEntries)
                        {
                            _logger.LogInformation(
                                "[CreateDeliveryFromSo] Bin alloc: {Item}/{Whs} Bin={Bin} Qty={Qty} BaseLineNumber={Idx}",
                                line.ItemCode, line.WhsCode, entry.Code, entry.Qty, i);
                            delivery.Lines.BinAllocations.BinAbsEntry   = entry.AbsEntry;
                            delivery.Lines.BinAllocations.Quantity       = (double)entry.Qty;
                            delivery.Lines.BinAllocations.BaseLineNumber = i;
                            delivery.Lines.BinAllocations.Add();
                        }
                        break;
                }
            }

            // ── Pre-Add() bin allocation validation ───────────────────────────────────
            // Guards against any logic gap before SAP sees the document.
            // Check 1: sum of bin allocations per line == delivery line quantity.
            // Check 2: total allocated per bin (OriginalQty - Remaining) ≤ OriginalQty.
            string? binValidErr = ValidateBinAllocations(deliverableLines, lineBinAllocs, binStockMap);
            if (binValidErr != null)
            {
                _logger.LogError("[CreateDeliveryFromSo] Bin pre-Add validation failed: {Err}", binValidErr);
                return new CreateDeliveryResult
                {
                    Success         = false,
                    FailureType     = DeliveryFailureType.SapError,
                    SapErrorMessage = $"Bin allocation validation failed (delivery NOT created): {binValidErr}",
                };
            }

            // ── Final stock revalidation — IMMEDIATELY before Add() ───────────────
            // Delivery is fully built; Add() follows directly after this gate with no
            // other work in between, minimising the race window vs. the earlier check
            // performed by SoDeliveryService.
            var liveStock = GetStockForSoLines(deliverableLines);
            var stockFail = CheckStockSufficiency(deliverableLines, liveStock);
            if (stockFail != null) return stockFail;

            // ── Commit ───────────────────────────────────────────────────────────
            int addRc = delivery.Add();
            if (addRc != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogError(
                    "[CreateDeliveryFromSo] Add() failed [{Code}]: {Msg} — SO DocEntry={DocEntry}",
                    errCode, errMsg, so.DocEntry);
                return new CreateDeliveryResult
                {
                    Success         = false,
                    FailureType     = DeliveryFailureType.SapError,
                    SapErrorCode    = errCode.ToString(),
                    SapErrorMessage = errMsg,
                };
            }

            int newDocEntry = int.Parse(company.GetNewObjectKey());

            // Fetch DocNum (GetNewObjectKey returns DocEntry, not DocNum)
            int docNum = 0;
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM ODLN WHERE DocEntry = {newDocEntry}");
            if (!rs.EoF) docNum = Convert.ToInt32(rs.Fields.Item("DocNum").Value);

            _logger.LogInformation(
                "[CreateDeliveryFromSo] Created Delivery DocEntry={DocEntry}, DocNum={DocNum} for SO {SoDocEntry}",
                newDocEntry, docNum, so.DocEntry);

            // Build LineBinAllocations keyed by SO line number (not loop index) so
            // callers can correlate by LineNum regardless of filtering.
            var lineBinAllocByLineNum = lineBinAllocs
                .Where(kv => kv.Value.Count > 0)
                .ToDictionary(
                    kv => deliverableLines[kv.Key].LineNum,
                    kv => kv.Value.Select(e => new LineBinAlloc(e.Code, e.Qty)).ToList());

            return new CreateDeliveryResult
            {
                Success             = true,
                DeliveryDocEntry    = newDocEntry,
                DeliveryDocNum      = docNum,
                FailureType         = DeliveryFailureType.None,
                LineBinAllocations  = lineBinAllocByLineNum,
            };
        }
        finally
        {
            if (rs       != null) Marshal.ReleaseComObject(rs);
            if (delivery != null) Marshal.ReleaseComObject(delivery);
        }
    }

    // ── Backfill: delivery bin allocations from OIBD ──────────────────────────────

    /// <summary>
    /// Queries SAP's OIBD table for the bin allocations recorded when a Delivery Note was posted.
    /// Returns a dict keyed by "{ItemCode}|{WhsCode}" (upper-case) → list of bin allocs.
    /// Returns empty if OIBD has no rows for this delivery (non-bin warehouse or SAP error).
    /// ObjType '15' = oDeliveryNotes in SAP B1.
    /// </summary>
    public Dictionary<string, List<LineBinAlloc>> GetDeliveryBinAllocations(int deliveryDocEntry)
    {
        var result  = new Dictionary<string, List<LineBinAlloc>>(StringComparer.OrdinalIgnoreCase);
        var company = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT B.BinCode, D.ItemCode, D.WhsCode, D.Quantity
                FROM   OIBD D
                INNER  JOIN OBIN B ON B.AbsEntry = D.BinAbs
                WHERE  D.ObjType  = '15'
                  AND  D.AbsEntry = {deliveryDocEntry}
                  AND  D.Quantity > 0
                ORDER  BY D.ItemCode, D.WhsCode, D.Quantity DESC");

            while (!rs.EoF)
            {
                string  binCode  = rs.Fields.Item("BinCode").Value?.ToString() ?? "";
                string  itemCode = rs.Fields.Item("ItemCode").Value?.ToString() ?? "";
                string  whsCode  = rs.Fields.Item("WhsCode").Value?.ToString() ?? "";
                decimal qty      = Convert.ToDecimal(rs.Fields.Item("Quantity").Value);

                if (!string.IsNullOrEmpty(binCode))
                {
                    string key = $"{itemCode.ToUpperInvariant()}|{whsCode.ToUpperInvariant()}";
                    if (!result.TryGetValue(key, out var list))
                        result[key] = list = new List<LineBinAlloc>();
                    list.Add(new LineBinAlloc(binCode, qty));
                }
                rs.MoveNext();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SapService] GetDeliveryBinAllocations({DocEntry}) — OIBD query failed: {Err}",
                deliveryDocEntry, ex.Message);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
        return result;
    }

    // ── Bin allocation support types ─────────────────────────────────────────────

    private enum BinAllocStatus
    {
        NotRequired,      // warehouse has no bin management — no BinAllocations needed
        Allocated,        // one or more entries ready to write to BinAllocations
        InsufficientStock,// total bin stock < required qty — abort delivery
        UnsupportedBatch, // item is batch-managed — not yet implemented
        UnsupportedSerial,// item is serial-managed — not yet implemented
    }

    private sealed record BinAllocationEntry(int AbsEntry, string Code, decimal Qty);

    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mutable per-bin stock entry. Shared across all lines of one Delivery creation.
    /// Remaining is decremented as each successive line allocates from this bin, so
    /// the same physical stock cannot be promised to two lines simultaneously.
    /// </summary>
    private sealed class BinStockEntry
    {
        public int     AbsEntry    { get; init; }
        public string  Code        { get; init; } = "";
        public decimal OriginalQty { get; init; }
        public decimal Remaining   { get; set; }   // mutable — decremented per allocation
    }

    /// <summary>
    /// Resolves bin allocations for one delivery line, drawing from the shared
    /// <paramref name="binStockMap"/> whose Remaining values already reflect all
    /// allocations made for earlier lines in this Delivery.
    ///
    /// Per-line rules (applied in order):
    ///   1. Batch-managed item  (OITM.ManBtchNum='Y') → UnsupportedBatch  (caller aborts).
    ///   2. Serial-managed item (OITM.ManSerNum='Y')  → UnsupportedSerial (caller aborts).
    ///   3. Non-bin warehouse   (OWHS.BinActivat≠'Y') → NotRequired       (no allocations).
    ///   4. Normal item + bin-managed warehouse:
    ///      a. Load all active OIBQ bins once per (ItemCode, WhsCode); cache in binStockMap.
    ///      b. Sum Remaining across all cached bins — if < requiredQty → InsufficientStock.
    ///      c. Greedy fill from fullest-remaining bin first; decrement Remaining in place.
    ///      d. Return Allocated with one BinAllocationEntry per bin used.
    ///
    /// Caches (avoid re-querying SAP for duplicate lines):
    ///   whsBinManagedCache  — WhsCode → isBinManaged
    ///   itemMgmtCache       — ItemCode → (isBatch, isSerial)
    ///   binStockMap         — "ITEMCODE|WHSCODE" → List&lt;BinStockEntry&gt;
    /// </summary>
    private (BinAllocStatus Status, List<BinAllocationEntry> Entries, string? ErrorMsg)
        ResolveBinAllocationsForLine(
            SAPbobsCOM.Company company,
            string itemCode,
            string whsCode,
            decimal requiredQty,
            Dictionary<string, List<BinStockEntry>> binStockMap,
            Dictionary<string, bool> whsBinManagedCache,
            Dictionary<string, (bool IsBatch, bool IsSerial)> itemMgmtCache)
    {
        string itemKeyN   = itemCode.ToUpperInvariant();
        string whsKeyN    = whsCode.ToUpperInvariant();
        string itemWhsKey = $"{itemKeyN}|{whsKeyN}";

        // 1. Item management type check — cached per ItemCode.
        if (!itemMgmtCache.TryGetValue(itemKeyN, out var mgmt))
        {
            Recordset rsItem = null;
            try
            {
                rsItem = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rsItem.DoQuery(
                    $"SELECT ManBtchNum, ManSerNum FROM OITM WHERE ItemCode = '{itemCode.Replace("'", "''")}'");
                bool isBatch  = !rsItem.EoF && rsItem.Fields.Item("ManBtchNum").Value?.ToString() == "Y";
                bool isSerial = !rsItem.EoF && rsItem.Fields.Item("ManSerNum").Value?.ToString()  == "Y";
                mgmt = (isBatch, isSerial);
                itemMgmtCache[itemKeyN] = mgmt;
            }
            finally { if (rsItem != null) Marshal.ReleaseComObject(rsItem); }
        }

        if (mgmt.IsBatch)
            return (BinAllocStatus.UnsupportedBatch, new List<BinAllocationEntry>(),
                $"Item {itemCode} is batch-managed. Batch + bin allocation is not yet supported. " +
                "Process this order manually in SAP B1.");
        if (mgmt.IsSerial)
            return (BinAllocStatus.UnsupportedSerial, new List<BinAllocationEntry>(),
                $"Item {itemCode} is serial-managed. Serial + bin allocation is not yet supported. " +
                "Process this order manually in SAP B1.");

        // 2. Warehouse bin management check — cached per WhsCode.
        if (!whsBinManagedCache.TryGetValue(whsKeyN, out bool isBinManaged))
        {
            Recordset rsWhs = null;
            try
            {
                rsWhs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rsWhs.DoQuery(
                    $"SELECT BinActivat FROM OWHS WHERE WhsCode = '{whsCode.Replace("'", "''")}'");
                isBinManaged = !rsWhs.EoF && rsWhs.Fields.Item("BinActivat").Value?.ToString() == "Y";
                whsBinManagedCache[whsKeyN] = isBinManaged;
            }
            finally { if (rsWhs != null) Marshal.ReleaseComObject(rsWhs); }
        }

        if (!isBinManaged)
            return (BinAllocStatus.NotRequired, new List<BinAllocationEntry>(), null);

        // 3. Load bin stock from OIBQ the FIRST time this (ItemCode, WhsCode) is seen.
        //    Subsequent lines for the same combination reuse the same BinStockEntry objects
        //    whose Remaining values already reflect earlier lines' allocations.
        if (!binStockMap.ContainsKey(itemWhsKey))
        {
            binStockMap[itemWhsKey] = LoadBinsFromOibq(company, itemCode, whsCode);
        }

        var bins = binStockMap[itemWhsKey];

        // 4. Verify remaining bin stock (post-prior-lines) covers requiredQty.
        decimal totalRemaining = bins.Sum(b => b.Remaining);
        if (totalRemaining < requiredQty)
        {
            string detail = bins.Count == 0
                ? "no bins with stock"
                : string.Join(", ", bins.Select(b =>
                    $"{b.Code}: orig={b.OriginalQty} rem={b.Remaining}"));
            return (BinAllocStatus.InsufficientStock, new List<BinAllocationEntry>(),
                $"Item {itemCode} Whs {whsCode}: remaining bin stock ({totalRemaining}) " +
                $"< required ({requiredQty}) after prior-line allocations. Bins: [{detail}].");
        }

        // 5. Greedy fill — fullest-remaining bin first.
        //    bin.Remaining is decremented in place so the next line for the same
        //    item/warehouse sees the reduced pool (no double-allocation).
        //
        //    Example: BIN-A=6, BIN-B=5, Line0=Qty6 → BIN-A:6 (rem=0).
        //             Line1=Qty5 → pool is now BIN-B:5 → BIN-B:5 (rem=0). Total=11. ✓
        var entries = new List<BinAllocationEntry>();
        decimal toFill = requiredQty;
        foreach (var bin in bins.OrderByDescending(b => b.Remaining))
        {
            if (toFill <= 0 || bin.Remaining <= 0) break;
            decimal alloc = Math.Min(toFill, bin.Remaining);
            entries.Add(new BinAllocationEntry(bin.AbsEntry, bin.Code, alloc));
            bin.Remaining -= alloc;
            toFill        -= alloc;
        }

        return (BinAllocStatus.Allocated, entries, null);
    }

    /// <summary>
    /// Queries OIBQ + OBIN for all active bins that hold stock for the given normal item
    /// and warehouse. Returns one BinStockEntry per bin, with Remaining = OriginalQty.
    /// Called once per (ItemCode, WhsCode) combination and cached in binStockMap.
    /// </summary>
    private List<BinStockEntry> LoadBinsFromOibq(
        SAPbobsCOM.Company company, string itemCode, string whsCode)
    {
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT Q.BinAbs, B.BinCode, Q.OnHandQty
                FROM   OIBQ Q
                INNER  JOIN OBIN B ON B.AbsEntry = Q.BinAbs
                WHERE  Q.ItemCode  = '{itemCode.Replace("'", "''")}'
                  AND  Q.WhsCode   = '{whsCode.Replace("'", "''")}'
                  AND  Q.OnHandQty > 0
                ORDER  BY Q.OnHandQty DESC");

            var result = new List<BinStockEntry>();
            while (!rs.EoF)
            {
                decimal qty = Convert.ToDecimal(rs.Fields.Item("OnHandQty").Value);
                result.Add(new BinStockEntry
                {
                    AbsEntry    = Convert.ToInt32(rs.Fields.Item("BinAbs").Value),
                    Code        = rs.Fields.Item("BinCode").Value?.ToString() ?? "",
                    OriginalQty = qty,
                    Remaining   = qty,
                });
                rs.MoveNext();
            }
            return result;
        }
        finally { if (rs != null) Marshal.ReleaseComObject(rs); }
    }

    /// <summary>
    /// Pre-Add() guard. Called after all lines are built, before delivery.Add().
    ///
    /// Check 1 — per-line totals: for every bin-managed line (non-empty allocations),
    ///            sum(alloc.Qty) must equal deliverableLines[i].OpenQty.
    ///            A mismatch means the greedy fill has a logic gap.
    ///
    /// Check 2 — per-bin totals: for every bin in binStockMap,
    ///            (OriginalQty - Remaining) must not exceed OriginalQty.
    ///            Over-allocation here indicates a Remaining underflow bug.
    ///
    /// Returns null when all checks pass; otherwise a descriptive error string.
    /// The caller must NOT call delivery.Add() when a non-null string is returned.
    /// </summary>
    private string? ValidateBinAllocations(
        List<OpenSoLineDto> deliverableLines,
        Dictionary<int, List<BinAllocationEntry>> lineBinAllocs,
        Dictionary<string, List<BinStockEntry>> binStockMap)
    {
        // Check 1: per-line allocation total == line quantity
        for (int i = 0; i < deliverableLines.Count; i++)
        {
            if (!lineBinAllocs.TryGetValue(i, out var allocs) || allocs.Count == 0)
                continue; // non-bin-managed line — nothing to verify

            decimal allocTotal = allocs.Sum(a => a.Qty);
            decimal required   = deliverableLines[i].OpenQty;
            if (allocTotal != required)
                return $"Line {i} (Item={deliverableLines[i].ItemCode}): bin allocation total " +
                       $"{allocTotal} ≠ line quantity {required}. Allocation logic gap detected.";
        }

        // Check 2: no bin is over-allocated beyond its original OIBQ stock
        foreach (var kvp in binStockMap)
        {
            foreach (var bin in kvp.Value)
            {
                decimal allocated = bin.OriginalQty - bin.Remaining;
                if (allocated > bin.OriginalQty)
                    return $"Bin {bin.Code} (AbsEntry={bin.AbsEntry}) over-allocated: " +
                           $"allocated={allocated} > original OIBQ stock={bin.OriginalQty}.";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns open, non-cancelled Sales Orders with DocDate strictly before processingDate
    /// (backlog). Headers only — no automatic processing. Ordered DocDate ASC, DocEntry ASC.
    /// </summary>
    public List<BacklogSoDto> GetBacklogSos(DateTime processingDate)
    {
        var company = GetConnectedCompany();
        var results = new List<BacklogSoDto>();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            string dateStr = processingDate.ToString("yyyy-MM-dd");
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum, T0.CardCode, T0.CardName,
                       T0.DocDate, T0.DocTotal,
                       (SELECT COUNT(*) FROM RDR1 T1
                        WHERE T1.DocEntry   = T0.DocEntry
                          AND T1.LineStatus = 'O'
                          AND T1.OpenQty    > 0) AS OpenLinesCount
                FROM   ORDR T0
                WHERE  T0.DocDate   < '{dateStr}'
                  AND  T0.DocStatus = 'O'
                  AND  T0.CANCELED  = 'N'
                ORDER BY T0.DocDate ASC, T0.DocEntry ASC");

            while (!rs.EoF)
            {
                results.Add(new BacklogSoDto
                {
                    DocEntry       = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum         = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    CustomerCode   = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                    CustomerName   = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                    DocDate        = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    DocTotal       = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                    OpenLinesCount = Convert.ToInt32(rs.Fields.Item("OpenLinesCount").Value),
                    // DaysOpen and enrichment fields populated by SoDeliveryService.GetBacklogAsync
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

    /// <summary>
    /// Aggregates required OpenQty by (ItemCode, WhsCode) across all lines — handles duplicate
    /// lines on the same item+warehouse (e.g. Line 0: ITEM-A/WH01/6, Line 1: ITEM-A/WH01/7 →
    /// total required = 13, not checked independently as 6 and 7).
    /// Returns a failure result if any combination is short, or null if all stock is sufficient.
    /// </summary>
    private CreateDeliveryResult? CheckStockSufficiency(
        List<OpenSoLineDto> lines,
        Dictionary<(string ItemCode, string WhsCode), decimal> stock)
    {
        // Sum required OpenQty per unique (ItemCode, WhsCode) — keys normalized with Trim + ToUpper.
        // Filters out zero-qty lines (defensive) and lines with blank ItemCode/WhsCode.
        // WH01 and WH02 remain separate groups; only same-warehouse totals are aggregated.
        var required = lines
            .Where(l => l.OpenQty > 0
                     && !string.IsNullOrWhiteSpace(l.ItemCode)
                     && !string.IsNullOrWhiteSpace(l.WhsCode))
            .GroupBy(l => (l.ItemCode.Trim().ToUpperInvariant(), l.WhsCode.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.OpenQty));

        foreach (var ((itemCode, whsCode), totalRequired) in required)
        {
            // Missing OITW row → OnHand = 0 → always fails (TryGetValue safe, no KeyNotFoundException)
            decimal onHand = stock.TryGetValue((itemCode, whsCode), out var qty) ? qty : 0m;
            if (onHand < totalRequired)
            {
                return new CreateDeliveryResult
                {
                    Success     = false,
                    FailureType = DeliveryFailureType.InsufficientStock,
                    SapErrorMessage =
                        $"Insufficient stock: {itemCode} in {whsCode} — " +
                        $"required {totalRequired:F4}, available {onHand:F4}",
                };
            }
        }
        return null; // all lines have sufficient stock
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




    // ─── Targeted product fetch (event path for new items) ───────────────────

    public List<ProductWithWarehouseDto> GetProductsForItems(IReadOnlyList<string> itemCodes)
    {
        if (itemCodes.Count == 0) return new();
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            string inClause = string.Join(",", itemCodes.Select(c => $"'{c.Replace("'", "''")}'"));
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT I.ItemCode, I.ItemName, I.U_Article_No, I.U_MdlTEST, I.U_Item_Name,
       P03.Price AS Price03, P05.Price AS Price05,
       W.WhsCode, W.OnHand AS OnHandQty
FROM OITM I
JOIN OITW W ON W.ItemCode = I.ItemCode AND W.WhsCode IN ('001','002','003','004')
LEFT JOIN ITM1 P03 ON P03.ItemCode = I.ItemCode AND P03.PriceList = 3
LEFT JOIN ITM1 P05 ON P05.ItemCode = I.ItemCode AND P05.PriceList = 5
WHERE I.ItemCode IN ({inClause})
  AND I.frozenFor = 'N'
ORDER BY I.ItemCode, W.WhsCode");

            var map = new Dictionary<string, ProductWithWarehouseDto>(StringComparer.OrdinalIgnoreCase);
            while (!rs.EoF)
            {
                string ic = rs.Fields.Item("ItemCode").Value?.ToString() ?? "";
                if (!map.TryGetValue(ic, out var dto))
                {
                    dto = new ProductWithWarehouseDto
                    {
                        ItemCode     = ic,
                        ItemName     = rs.Fields.Item("ItemName").Value?.ToString() ?? "",
                        U_Article_No = rs.Fields.Item("U_Article_No").Value?.ToString() ?? "",
                        U_MdlTEST   = rs.Fields.Item("U_MdlTEST").Value?.ToString() ?? "",
                        U_Item_Name  = rs.Fields.Item("U_Item_Name").Value?.ToString() ?? "",
                        Price        = Convert.ToDecimal(rs.Fields.Item("Price03").Value ?? 0),
                        Price05      = Convert.ToDecimal(rs.Fields.Item("Price05").Value ?? 0),
                        Warehouses   = new List<WarehouseStockDto>()
                    };
                    map[ic] = dto;
                }
                dto.Warehouses.Add(new WarehouseStockDto
                {
                    WarehouseCode = rs.Fields.Item("WhsCode").Value?.ToString() ?? "",
                    OnHandQty     = Convert.ToDecimal(rs.Fields.Item("OnHandQty").Value ?? 0)
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
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ─── Delivery SAP readers ─────────────────────────────────────────────────

    /// <summary>
    /// ACTIVE-ONLY query: non-cancelled ZF ODLN DLN1 lines with full ZF identity fields.
    /// ALL of the following must match: CANCELED='N', U_ZoneRef='ZoneFulfillment',
    /// U_ReplitId, BaseType=17 (ORDR), BaseEntry=soDocEntry.
    /// Only this result may be used for ActiveSapDeliveredQty, eligibility, and idempotency.
    /// </summary>
    public List<SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine>
        FindZoneFulfillmentDeliveries(string uReplitId, int soDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum,
                       T1.LineNum, T1.BaseLine, T1.ItemCode, T1.Quantity, T1.WhsCode
                FROM   ODLN T0
                JOIN   DLN1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T0.CANCELED    = N'N'
                  AND  T0.U_ZoneRef   = N'ZoneFulfillment'
                  AND  T0.U_ReplitId  = N'{uReplitId.Replace("'", "''")}'
                  AND  T1.BaseType    = 17
                  AND  T1.BaseEntry   = {soDocEntry}");
            while (!rs.EoF)
            {
                result.Add(new SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine(
                    DocEntry  : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum    : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    DlnLineNum: Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    BaseLine  : Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                    ItemCode  : rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity  : Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    WhsCode   : rs.Fields.Item("WhsCode").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Reads DLN1 lines for a known ODLN DocEntry — ACTIVE (CANCELED='N') only.
    /// Used when a legacy ODLN (without ZF UDFs) was recorded in MolasIntegration and is still active.
    /// Returns empty if the ODLN is cancelled.
    /// </summary>
    public List<SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine> ReadDln1ByDocEntry(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum,
                       T1.LineNum, T1.BaseLine, T1.ItemCode, T1.Quantity, T1.WhsCode
                FROM   ODLN T0
                JOIN   DLN1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T0.DocEntry  = {docEntry}
                  AND  T0.CANCELED  = N'N'");
            while (!rs.EoF)
            {
                result.Add(new SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine(
                    DocEntry  : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum    : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    DlnLineNum: Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    BaseLine  : Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                    ItemCode  : rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity  : Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    WhsCode   : rs.Fields.Item("WhsCode").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Reads DLN1 lines for a known ODLN DocEntry regardless of cancellation status.
    /// For AUDIT / HISTORICAL display only — must never feed ActiveSapDeliveredQty.
    /// Also returns the CANCELED field so callers can distinguish state.
    /// </summary>
    public (List<SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine> Lines, string? Canceled)
        ReadDln1ByDocEntryAny(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine>();
        string? canceled = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum, T0.CANCELED,
                       T1.LineNum, T1.BaseLine, T1.ItemCode, T1.Quantity, T1.WhsCode
                FROM   ODLN T0
                JOIN   DLN1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T0.DocEntry = {docEntry}");
            while (!rs.EoF)
            {
                canceled ??= rs.Fields.Item("CANCELED").Value?.ToString();
                result.Add(new SapReplitAPI.Models.ZoneFulfillment.SapDeliveryLine(
                    DocEntry  : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum    : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    DlnLineNum: Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    BaseLine  : Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                    ItemCode  : rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity  : Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    WhsCode   : rs.Fields.Item("WhsCode").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            return (result, canceled);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Returns the ODLN header state for a given DocEntry. Read-only diagnostic.
    /// </summary>
    public record OdlnHeaderState(
        int     DocEntry,
        int     DocNum,
        string  DocStatus,
        string  Canceled,
        string  DocDate,
        string  CardCode,
        string  UZoneRef,
        string  UDeliveryLocation,
        string  UReplitId);

    public OdlnHeaderState? GetOdlnHeaderState(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT DocEntry, DocNum, DocStatus, CANCELED,
                       CONVERT(varchar,DocDate,23) AS DocDate,
                       CardCode,
                       ISNULL(U_ZoneRef,'')           AS U_ZoneRef,
                       ISNULL(U_DeliveryLocation,'')  AS U_DeliveryLocation,
                       ISNULL(U_ReplitId,'')          AS U_ReplitId
                FROM   ODLN
                WHERE  DocEntry = {docEntry}");
            if (rs.EoF) return null;
            return new OdlnHeaderState(
                DocEntry          : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                DocNum            : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                DocStatus         : rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                Canceled          : rs.Fields.Item("CANCELED").Value?.ToString() ?? "",
                DocDate           : rs.Fields.Item("DocDate").Value?.ToString() ?? "",
                CardCode          : rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                UZoneRef          : rs.Fields.Item("U_ZoneRef").Value?.ToString() ?? "",
                UDeliveryLocation : rs.Fields.Item("U_DeliveryLocation").Value?.ToString() ?? "",
                UReplitId         : rs.Fields.Item("U_ReplitId").Value?.ToString() ?? "");
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ── C4 Invoice preflight SAP reads ──────────────────────────────────────────

    /// <summary>
    /// Reads ODLN header with all fields needed for invoice preflight.
    /// Returns null if not found.
    /// </summary>
    public SapReplitAPI.Models.ZoneFulfillment.OdlnForInvoice? GetOdlnForInvoice(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT DocEntry, DocNum, DocStatus, CANCELED,
                       CardCode, ISNULL(DocCur,'') AS DocCur, SlpCode,
                       ISNULL(U_ZoneRef,'')          AS U_ZoneRef,
                       ISNULL(U_DeliveryLocation,'') AS U_DeliveryLocation,
                       ISNULL(U_ReplitId,'')         AS U_ReplitId,
                       DocDueDate
                FROM   ODLN
                WHERE  DocEntry = {docEntry}");
            if (rs.EoF) return null;
            return new SapReplitAPI.Models.ZoneFulfillment.OdlnForInvoice(
                DocEntry         : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                DocNum           : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                DocStatus        : rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                Canceled         : rs.Fields.Item("CANCELED").Value?.ToString() ?? "",
                CardCode         : rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                DocCur           : rs.Fields.Item("DocCur").Value?.ToString() ?? "",
                SlpCode          : Convert.ToInt32(rs.Fields.Item("SlpCode").Value),
                UZoneRef         : rs.Fields.Item("U_ZoneRef").Value?.ToString() ?? "",
                UDeliveryLocation: rs.Fields.Item("U_DeliveryLocation").Value?.ToString() ?? "",
                UReplitId        : rs.Fields.Item("U_ReplitId").Value?.ToString() ?? "",
                DocDueDate       : Convert.ToDateTime(rs.Fields.Item("DocDueDate").Value));
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Returns all DLN1 lines from a non-cancelled ODLN where OpenQty > 0 (invoice-eligible).
    /// </summary>
    public List<SapReplitAPI.Models.ZoneFulfillment.Dln1InvoiceLine> GetDln1EligibleLines(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<SapReplitAPI.Models.ZoneFulfillment.Dln1InvoiceLine>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T1.LineNum, T1.BaseLine, T1.ItemCode,
                       ISNULL(T1.Dscription,'') AS Dscription,
                       T1.Quantity, T1.OpenQty,
                       ISNULL(T1.Price,0)       AS Price,
                       ISNULL(T1.Currency,'')   AS Currency,
                       T1.BaseType, T1.BaseEntry,
                       ISNULL(T1.WhsCode,'')    AS WhsCode
                FROM   ODLN T0
                JOIN   DLN1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T0.DocEntry = {docEntry}
                  AND  T0.CANCELED = N'N'
                  AND  T1.OpenQty  > 0");
            while (!rs.EoF)
            {
                result.Add(new SapReplitAPI.Models.ZoneFulfillment.Dln1InvoiceLine(
                    LineNum    : Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    BaseLine   : Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                    ItemCode   : rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Dscription : rs.Fields.Item("Dscription").Value?.ToString() ?? "",
                    Quantity   : Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    OpenQty    : Convert.ToDecimal(rs.Fields.Item("OpenQty").Value),
                    Price      : Convert.ToDecimal(rs.Fields.Item("Price").Value),
                    Currency   : rs.Fields.Item("Currency").Value?.ToString() ?? "",
                    BaseType   : Convert.ToInt32(rs.Fields.Item("BaseType").Value),
                    BaseEntry  : Convert.ToInt32(rs.Fields.Item("BaseEntry").Value),
                    WhsCode    : rs.Fields.Item("WhsCode").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// SAP-first invoice idempotency check: finds existing non-cancelled OINV rows
    /// whose INV1 lines have BaseType=15 (ODLN) and BaseEntry=deliveryDocEntry.
    /// Returns [] if none found — safe to proceed with OINV.Add() after gate passes.
    /// </summary>
    public List<SapReplitAPI.Models.ZoneFulfillment.SapInvoiceMatch> SearchActiveInvoicesByDelivery(int deliveryDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<SapReplitAPI.Models.ZoneFulfillment.SapInvoiceMatch>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT DISTINCT
                       T0.DocEntry, T0.DocNum, T0.DocStatus,
                       ISNULL(T0.CANCELED,'N') AS CANCELED
                FROM   OINV T0
                JOIN   INV1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T1.BaseType  = 15
                  AND  T1.BaseEntry = {deliveryDocEntry}
                  AND  ISNULL(T0.CANCELED,'N') = N'N'");
            while (!rs.EoF)
            {
                result.Add(new SapReplitAPI.Models.ZoneFulfillment.SapInvoiceMatch(
                    DocEntry : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum   : Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    DocStatus: rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                    Canceled : rs.Fields.Item("CANCELED").Value?.ToString() ?? "N"));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Creates an OINV from an ODLN for a Zone Fulfillment delivery.
    /// Sets base-document linkage (BaseType=15, BaseEntry, BaseLine per DLN1.LineNum)
    /// and ZF UDFs (U_ZoneRef, U_ReplitId, U_DeliveryLocation).
    /// Returns (DocEntry, DocNum) of the new OINV.
    /// Throws on SAP error.
    /// </summary>
    public (int DocEntry, int DocNum) CreateZoneFulfillmentInvoice(
        SapReplitAPI.Models.ZoneFulfillment.ZfInvoicePreflightResult preflight,
        string deliveryLocation)
    {
        var company = GetConnectedCompany();
        var odln    = preflight.Odln!;
        var lines   = preflight.EligibleLines;

        if (lines.Count == 0)
            throw new InvalidOperationException(
                $"No eligible DLN1 lines for ODLN {preflight.DeliveryDocEntry}.");

        var invoice = (Documents)company.GetBusinessObject(BoObjectTypes.oInvoices);
        invoice.CardCode    = odln.CardCode;
        invoice.DocDate     = DateTime.Today;
        invoice.DocDueDate  = odln.DocDueDate;
        invoice.DocCurrency = odln.DocCur;
        if (odln.SlpCode > 0)
            invoice.SalesPersonCode = odln.SlpCode;

        invoice.UserFields.Fields.Item("U_ZoneRef").Value          = "ZoneFulfillment";
        invoice.UserFields.Fields.Item("U_ReplitId").Value         = odln.UReplitId;
        invoice.UserFields.Fields.Item("U_DeliveryLocation").Value = deliveryLocation;

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) invoice.Lines.Add();
            invoice.Lines.BaseType  = 15;
            invoice.Lines.BaseEntry = preflight.DeliveryDocEntry;
            invoice.Lines.BaseLine  = lines[i].LineNum;
        }

        int rc = invoice.Add();
        if (rc != 0)
        {
            string err = company.GetLastErrorDescription();
            throw new InvalidOperationException(
                $"OINV.Add() failed for ODLN {preflight.DeliveryDocEntry}: rc={rc} — {err}");
        }

        int newDocEntry = int.Parse(company.GetNewObjectKey());

        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM OINV WHERE DocEntry = {newDocEntry}");
            int docNum = !rs.EoF ? Convert.ToInt32(rs.Fields.Item("DocNum").Value) : 0;
            return (newDocEntry, docNum);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Reads back OINV header + INV1 lines for a newly created ZF invoice.
    /// Returns null if not found.
    /// </summary>
    public SapReplitAPI.Models.ZoneFulfillment.OinvCreatedReadback? ReadZfOinv(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.DocEntry, T0.DocNum, T0.DocStatus,
                       T0.CardCode, CONVERT(varchar(10),T0.DocDate,120)    AS DocDate,
                       CONVERT(varchar(10),T0.DocDueDate,120) AS DocDueDate,
                       T0.DocTotal, ISNULL(T0.DocCur,'') AS DocCur,
                       ISNULL(T0.U_ZoneRef,'')           AS U_ZoneRef,
                       ISNULL(T0.U_ReplitId,'')          AS U_ReplitId,
                       ISNULL(T0.U_DeliveryLocation,'')  AS U_DeliveryLocation,
                       T1.LineNum, T1.ItemCode, T1.Quantity, T1.Price,
                       T1.BaseType, T1.BaseEntry, T1.BaseLine
                FROM   OINV T0
                JOIN   INV1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T0.DocEntry = {docEntry}
                ORDER  BY T1.LineNum");

            if (rs.EoF) return null;

            var header = new SapReplitAPI.Models.ZoneFulfillment.OinvCreatedReadback
            {
                DocEntry          = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                DocNum            = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                DocStatus         = rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                CardCode          = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                DocDate           = rs.Fields.Item("DocDate").Value?.ToString() ?? "",
                DocDueDate        = rs.Fields.Item("DocDueDate").Value?.ToString() ?? "",
                DocTotal          = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                DocCurrency       = rs.Fields.Item("DocCur").Value?.ToString() ?? "",
                UZoneRef          = rs.Fields.Item("U_ZoneRef").Value?.ToString(),
                UReplitId         = rs.Fields.Item("U_ReplitId").Value?.ToString(),
                UDeliveryLocation = rs.Fields.Item("U_DeliveryLocation").Value?.ToString(),
                Lines             = new()
            };

            while (!rs.EoF)
            {
                header.Lines.Add(new SapReplitAPI.Models.ZoneFulfillment.OinvLineReadback
                {
                    LineNum   = Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    ItemCode  = rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity  = Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    Price     = Convert.ToDecimal(rs.Fields.Item("Price").Value),
                    BaseType  = Convert.ToInt32(rs.Fields.Item("BaseType").Value),
                    BaseEntry = Convert.ToInt32(rs.Fields.Item("BaseEntry").Value),
                    BaseLine  = Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                });
                rs.MoveNext();
            }

            return header;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Returns current OITW stock snapshot for one item+warehouse. Diagnostic/read-only.</summary>
    public record OitwSnapshot(string ItemCode, string WhsCode, decimal OnHand, decimal IsCommited, decimal OnOrder);
    public OitwSnapshot? GetOitwSnapshot(string itemCode, string whsCode)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT ItemCode, WhsCode, OnHand, IsCommited, OnOrder
                FROM   OITW
                WHERE  ItemCode = N'{itemCode.Replace("'", "''")}'
                  AND  WhsCode  = N'{whsCode.Replace("'", "''")}'");
            if (rs.EoF) return null;
            return new OitwSnapshot(
                ItemCode    : rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                WhsCode     : rs.Fields.Item("WhsCode").Value?.ToString() ?? "",
                OnHand      : Convert.ToDecimal(rs.Fields.Item("OnHand").Value ?? 0m),
                IsCommited  : Convert.ToDecimal(rs.Fields.Item("IsCommited").Value ?? 0m),
                OnOrder     : Convert.ToDecimal(rs.Fields.Item("OnOrder").Value ?? 0m));
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Returns OIBQ bin allocations for one item+warehouse. Diagnostic/read-only.</summary>
    public record OibqRow(int BinAbsEntry, string BinCode, decimal OnHandQty);
    public List<OibqRow> GetOibqSnapshot(string itemCode, string whsCode)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<OibqRow>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.BinAbs, T1.BinCode, T0.OnHandQty
                FROM   OIBQ T0
                JOIN   OBIN T1 ON T1.AbsEntry = T0.BinAbs
                WHERE  T0.ItemCode = N'{itemCode.Replace("'", "''")}'
                  AND  T0.WhsCode  = N'{whsCode.Replace("'", "''")}'
                  AND  T0.OnHandQty <> 0
                ORDER  BY T0.OnHandQty DESC");
            while (!rs.EoF)
            {
                result.Add(new OibqRow(
                    BinAbsEntry : Convert.ToInt32(rs.Fields.Item("BinAbs").Value),
                    BinCode     : rs.Fields.Item("BinCode").Value?.ToString() ?? "",
                    OnHandQty   : Convert.ToDecimal(rs.Fields.Item("OnHandQty").Value ?? 0m)));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Returns all RDR1 lines for a sales order. Diagnostic/read-only.</summary>
    public record Rdr1Line(int LineNum, string ItemCode, decimal Quantity, decimal OpenQty, string LineStatus, string WhsCode);
    public List<Rdr1Line> GetRdr1AllLines(int soDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var result = new List<Rdr1Line>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT LineNum, ItemCode, Quantity, OpenQty, LineStatus, WhsCode
                FROM   RDR1
                WHERE  DocEntry = {soDocEntry}
                ORDER  BY LineNum");
            while (!rs.EoF)
            {
                result.Add(new Rdr1Line(
                    LineNum    : Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    ItemCode   : rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity   : Convert.ToDecimal(rs.Fields.Item("Quantity").Value ?? 0m),
                    OpenQty    : Convert.ToDecimal(rs.Fields.Item("OpenQty").Value ?? 0m),
                    LineStatus : rs.Fields.Item("LineStatus").Value?.ToString() ?? "",
                    WhsCode    : rs.Fields.Item("WhsCode").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    public record OpklHeader(int AbsEntry, string Status, string Canceled);
    public record Pkl1Row(int OrderEntry, int OrderLine, decimal RelQtty, decimal PickQtty, string PickStatus);
    public record Pkl2Row(int BinAbs, string BinCode, decimal PickQtty);

    /// <summary>Returns full OPKL+PKL1+PKL2 state for a pick list. Diagnostic/read-only.</summary>
    public (OpklHeader? Header, List<Pkl1Row> Lines, List<Pkl2Row> Bins) GetPickListFullState(int absEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            // OPKL header
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT AbsEntry, Status, ISNULL(Canceled,'N') AS Canceled
                FROM   OPKL WHERE AbsEntry = {absEntry}");
            OpklHeader? header = null;
            if (!rs.EoF)
                header = new OpklHeader(
                    AbsEntry : Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                    Status   : rs.Fields.Item("Status").Value?.ToString() ?? "",
                    Canceled : rs.Fields.Item("Canceled").Value?.ToString() ?? "N");
            Marshal.ReleaseComObject(rs); rs = null;

            // PKL1 lines
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT OrderEntry, OrderLine, RelQtty, PickQtty, PickStatus
                FROM   PKL1 WHERE AbsEntry = {absEntry} ORDER BY PickEntry");
            var lines = new List<Pkl1Row>();
            while (!rs.EoF)
            {
                lines.Add(new Pkl1Row(
                    OrderEntry : Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                    OrderLine  : Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                    RelQtty    : Convert.ToDecimal(rs.Fields.Item("RelQtty").Value ?? 0m),
                    PickQtty   : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value ?? 0m),
                    PickStatus : rs.Fields.Item("PickStatus").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            Marshal.ReleaseComObject(rs); rs = null;

            // PKL2 bin allocations — PKL2 has no RelQtty column; only AbsEntry,PickEntry,SnBEntry,BinAbs,PickQtty
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.BinAbs, T1.BinCode, T0.PickQtty
                FROM   PKL2 T0
                JOIN   OBIN T1 ON T1.AbsEntry = T0.BinAbs
                WHERE  T0.AbsEntry = {absEntry}
                ORDER  BY T0.BinAbs");
            var bins = new List<Pkl2Row>();
            while (!rs.EoF)
            {
                bins.Add(new Pkl2Row(
                    BinAbs   : Convert.ToInt32(rs.Fields.Item("BinAbs").Value),
                    BinCode  : rs.Fields.Item("BinCode").Value?.ToString() ?? "",
                    PickQtty : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value ?? 0m)));
                rs.MoveNext();
            }
            return (header, lines, bins);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Full OPKL+PKL1+PKL2 state enriched for reconciliation safety gates:
    ///   OPKL → Status, Canceled, U_ReplitId (identity ownership)
    ///   PKL1 → PickEntry, BaseObject, WhsCode (from RDR1 join), RelQtty, PickQtty, PickStatus
    ///   PKL2 → PickEntry, BinAbs, BinCode, PickQtty (per-line bin attribution)
    /// Diagnostic/read-only. Never modifies any SAP document.
    /// </summary>
    public (SapReplitAPI.Models.ZoneFulfillment.ZfOpklValidation? Header,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.ZfPkl1Validation> Lines,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.ZfPkl2Validation> Bins)
        GetZfPickListValidationState(int absEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            // OPKL header — include U_ReplitId for identity ownership check
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT AbsEntry, Status, ISNULL(Canceled,'N') AS Canceled,
                       ISNULL(U_ReplitId,'') AS UReplitId
                FROM   OPKL WHERE AbsEntry = {absEntry}");
            SapReplitAPI.Models.ZoneFulfillment.ZfOpklValidation? header = null;
            if (!rs.EoF)
            {
                string uRid = rs.Fields.Item("UReplitId").Value?.ToString() ?? "";
                header = new SapReplitAPI.Models.ZoneFulfillment.ZfOpklValidation(
                    AbsEntry  : Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                    Status    : rs.Fields.Item("Status").Value?.ToString() ?? "",
                    Canceled  : rs.Fields.Item("Canceled").Value?.ToString() ?? "N",
                    UReplitId : uRid.Length > 0 ? uRid : null);
            }
            Marshal.ReleaseComObject(rs); rs = null;

            // PKL1 lines — include PickEntry (for PKL2 attribution), BaseObject (identity),
            // WhsCode joined from RDR1 (warehouse validation).
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT P.PickEntry, P.OrderEntry, P.OrderLine, P.BaseObject,
                       P.RelQtty, P.PickQtty, P.PickStatus,
                       ISNULL(R.WhsCode,'') AS WhsCode
                FROM   PKL1 P
                LEFT JOIN RDR1 R ON R.DocEntry = P.OrderEntry AND R.LineNum = P.OrderLine
                WHERE  P.AbsEntry = {absEntry}
                ORDER BY P.PickEntry");
            var lines = new List<SapReplitAPI.Models.ZoneFulfillment.ZfPkl1Validation>();
            while (!rs.EoF)
            {
                lines.Add(new SapReplitAPI.Models.ZoneFulfillment.ZfPkl1Validation(
                    PickEntry  : Convert.ToInt32(rs.Fields.Item("PickEntry").Value),
                    OrderEntry : Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                    OrderLine  : Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                    BaseObject : Convert.ToInt32(rs.Fields.Item("BaseObject").Value),
                    WhsCode    : rs.Fields.Item("WhsCode").Value?.ToString() ?? "",
                    RelQtty    : Convert.ToDecimal(rs.Fields.Item("RelQtty").Value ?? 0m),
                    PickQtty   : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value ?? 0m),
                    PickStatus : rs.Fields.Item("PickStatus").Value?.ToString() ?? ""));
                rs.MoveNext();
            }
            Marshal.ReleaseComObject(rs); rs = null;

            // PKL2 bin allocations — include PickEntry so bins can be attributed per PKL1 line
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT T0.PickEntry, T0.BinAbs, T1.BinCode, T0.PickQtty
                FROM   PKL2 T0
                JOIN   OBIN T1 ON T1.AbsEntry = T0.BinAbs
                WHERE  T0.AbsEntry = {absEntry}
                ORDER  BY T0.PickEntry, T0.BinAbs");
            var bins = new List<SapReplitAPI.Models.ZoneFulfillment.ZfPkl2Validation>();
            while (!rs.EoF)
            {
                bins.Add(new SapReplitAPI.Models.ZoneFulfillment.ZfPkl2Validation(
                    PickEntry : Convert.ToInt32(rs.Fields.Item("PickEntry").Value),
                    BinAbs    : Convert.ToInt32(rs.Fields.Item("BinAbs").Value),
                    BinCode   : rs.Fields.Item("BinCode").Value?.ToString() ?? "",
                    PickQtty  : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value ?? 0m)));
                rs.MoveNext();
            }
            return (header, lines, bins);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Legacy single-delivery check. Returns first match only. Use FindZoneFulfillmentDeliveries for multi-delivery truth.</summary>
    public (int DocEntry, int DocNum)? FindZoneFulfillmentDelivery(string uReplitId)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT TOP 1 T0.DocEntry, T0.DocNum
                FROM   ODLN T0
                WHERE  T0.U_ReplitId = '{uReplitId.Replace("'", "''")}'
                  AND  T0.CANCELED   = N'N'");
            if (rs.EoF) return null;
            return (Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    Convert.ToInt32(rs.Fields.Item("DocNum").Value));
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Looks up OBIN.BinCode by AbsEntry. Returns null if not found.</summary>
    public string? GetBinCodeByAbsEntry(int binAbsEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT BinCode FROM OBIN WHERE AbsEntry = {binAbsEntry}");
            if (rs.EoF) return null;
            return rs.Fields.Item("BinCode").Value?.ToString();
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Reads RDR1.OpenQty for a specific (soDocEntry, soLineNum). Returns 0 if not found.</summary>
    public decimal GetRdr1OpenQty(int soDocEntry, int soLineNum)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT OpenQty FROM RDR1
                WHERE  DocEntry = {soDocEntry}
                  AND  LineNum  = {soLineNum}");
            if (rs.EoF) return 0m;
            return Convert.ToDecimal(rs.Fields.Item("OpenQty").Value);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Reads ORDR UDF state for zone-fulfillment traceability.</summary>
    public SoUdfState? GetOrdrUdfState(int soDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT U_ZoneRef, U_DeliveryLocation, U_ReplitId, DocStatus, CANCELED
                FROM   ORDR
                WHERE  DocEntry = {soDocEntry}");
            if (rs.EoF) return null;
            return new SoUdfState(
                UZoneRef:         rs.Fields.Item("U_ZoneRef").Value?.ToString(),
                UDeliveryLocation:rs.Fields.Item("U_DeliveryLocation").Value?.ToString(),
                UReplitId:        rs.Fields.Item("U_ReplitId").Value?.ToString(),
                DocStatus:        rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                Canceled:         rs.Fields.Item("CANCELED").Value?.ToString() ?? "N");
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Reads CardCode from ORDR for a given DocEntry.</summary>
    public string? GetOrdrCardCode(int soDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT CardCode FROM ORDR WHERE DocEntry = {soDocEntry}");
            return rs.EoF ? null : rs.Fields.Item("CardCode").Value?.ToString();
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Creates one Zone Fulfillment Delivery (ODLN) for one or more SO fragments.
    /// Each DeliveryLineSpec produces one DLN1 line. DLN line numbers are 0-based index order.
    /// Uses durable bin allocation from the pick list (NOT from OIBQ).
    /// Returns (Rc, DocEntry, DocNum, SapError); Rc=0 = success.
    /// </summary>
    public (int Rc, int DocEntry, int DocNum, string? SapError)
        CreateZoneFulfillmentDelivery(
            string                              cardCode,
            DateTime                            deliveryDate,
            string                              uReplitId,
            string                              deliveryLocation,
            IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.DeliveryLineSpec> lines)
    {
        if (lines.Count == 0)
            throw new ArgumentException("At least one DeliveryLineSpec is required.", nameof(lines));

        var company  = GetConnectedCompany();
        Documents delivery = null;
        Recordset rs = null;
        try
        {
            delivery = (Documents)company.GetBusinessObject(BoObjectTypes.oDeliveryNotes);

            delivery.CardCode                = cardCode;
            delivery.DocDate                 = deliveryDate;
            delivery.TaxDate                 = deliveryDate;
            delivery.DocDueDate              = deliveryDate;
            delivery.DocCurrency             = "TZS";
            delivery.BPL_IDAssignedToInvoice = 1;

            delivery.UserFields.Fields.Item("U_ZoneRef").Value          = "ZoneFulfillment";
            delivery.UserFields.Fields.Item("U_DeliveryLocation").Value = deliveryLocation;
            delivery.UserFields.Fields.Item("U_ReplitId").Value         = uReplitId;

            // Bug #3 fix: one DLN1 line per eligible fragment
            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                if (lineIdx > 0) delivery.Lines.Add();

                var spec = lines[lineIdx];
                delivery.Lines.BaseType      = 17;      // ORDR
                delivery.Lines.BaseEntry     = spec.SoDocEntry;
                delivery.Lines.BaseLine      = spec.SoLineNum;
                delivery.Lines.Quantity      = (double)spec.Qty;
                delivery.Lines.WarehouseCode = spec.WhsCode;

                // Durable bin allocation — from pick list PKL2, NOT from fresh OIBQ query.
                var bins = spec.DurableBins;
                for (int i = 0; i < bins.Count; i++)
                {
                    if (i > 0) delivery.Lines.BinAllocations.Add();
                    delivery.Lines.BinAllocations.BinAbsEntry   = bins[i].BinAbsEntry;
                    delivery.Lines.BinAllocations.Quantity       = (double)bins[i].Qty;
                    delivery.Lines.BinAllocations.BaseLineNumber = lineIdx;
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rc = delivery.Add();
            sw.Stop();

            if (rc != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogError(
                    "[ZF-DLV] ODLN.Add() failed [{Code}]: {Msg} Lines={LineCount} elapsed={Ms}ms",
                    errCode, errMsg, lines.Count, sw.ElapsedMilliseconds);
                return (rc, 0, 0, $"{errCode} - {errMsg}");
            }

            int docEntry = int.Parse(company.GetNewObjectKey());

            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM ODLN WHERE DocEntry = {docEntry}");
            int docNum = rs.EoF ? 0 : Convert.ToInt32(rs.Fields.Item("DocNum").Value);

            _logger.LogInformation(
                "[ZF-DLV] ODLN.Add() SUCCESS DocEntry={DocEntry} DocNum={DocNum} Lines={LineCount} elapsed={Ms}ms",
                docEntry, docNum, lines.Count, sw.ElapsedMilliseconds);

            return (0, docEntry, docNum, null);
        }
        finally
        {
            if (rs       != null) Marshal.ReleaseComObject(rs);
            if (delivery != null) Marshal.ReleaseComObject(delivery);
        }
    }

    /// <summary>
    /// Reads back ODLN header, DLN1 lines, and OIBD bin allocations after ODLN.Add().
    /// Returns null if DocEntry not found.
    /// </summary>
    public SapReplitAPI.Models.ZoneFulfillment.OdlnReadback? ReadBackZoneFulfillmentOdln(
        int docEntry, long elapsedMs = 0)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

            rs.DoQuery($@"
                SELECT DocEntry, DocNum, DocStatus, CANCELED, CardCode,
                       U_ZoneRef, U_DeliveryLocation, U_ReplitId
                FROM   ODLN
                WHERE  DocEntry = {docEntry}");
            if (rs.EoF) return null;

            var rb = new SapReplitAPI.Models.ZoneFulfillment.OdlnReadback
            {
                DocEntry          = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                DocNum            = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                DocStatus         = rs.Fields.Item("DocStatus").Value?.ToString() ?? "",
                Canceled          = rs.Fields.Item("CANCELED").Value?.ToString() ?? "",
                CardCode          = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                UZoneRef          = rs.Fields.Item("U_ZoneRef").Value?.ToString(),
                UDeliveryLocation = rs.Fields.Item("U_DeliveryLocation").Value?.ToString(),
                UReplitId         = rs.Fields.Item("U_ReplitId").Value?.ToString(),
                ElapsedMs         = elapsedMs
            };

            // DLN1 lines
            rs.DoQuery($@"
                SELECT LineNum, ItemCode, Quantity, WhsCode,
                       BaseType, BaseEntry, BaseLine, Dscription
                FROM   DLN1
                WHERE  DocEntry = {docEntry}");
            while (!rs.EoF)
            {
                rb.Lines.Add(new SapReplitAPI.Models.ZoneFulfillment.OdlnLineReadback
                {
                    LineNum    = Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    ItemCode   = rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Quantity   = Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    WhsCode    = rs.Fields.Item("WhsCode").Value?.ToString() ?? "",
                    BaseType   = Convert.ToInt32(rs.Fields.Item("BaseType").Value),
                    BaseEntry  = Convert.ToInt32(rs.Fields.Item("BaseEntry").Value),
                    BaseLine   = Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                    Dscription = rs.Fields.Item("Dscription").Value?.ToString()
                });
                rs.MoveNext();
            }

            // OIBD bin allocations — non-fatal: readback is verification only
            try
            {
                rs.DoQuery($@"
                    SELECT B.AbsEntry AS BinAbsEntry, B.BinCode, D.Quantity
                    FROM   OIBD D
                    JOIN   OBIN B ON D.BinAbs = B.AbsEntry
                    WHERE  D.AbsEntry = {docEntry}
                      AND  D.ObjType  = N'15'");
                while (!rs.EoF)
                {
                    rb.BinAllocations.Add(new SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc(
                        Convert.ToInt32(rs.Fields.Item("BinAbsEntry").Value),
                        rs.Fields.Item("BinCode").Value?.ToString()?.Trim() ?? "",
                        Convert.ToDecimal(rs.Fields.Item("Quantity").Value)));
                    rs.MoveNext();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ZF-DLV] ReadBack OIBD query failed for DocEntry={DocEntry} — bin allocations empty in readback", docEntry);
            }

            return rb;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// READ-ONLY diagnostic: probes candidate SAP tables to locate the correct
    /// delivery bin allocation table for a given ODLN DocEntry.
    /// Tries OIBD, IBD1, and a schema enumeration. Returns a summary of results.
    /// </summary>
    public object ProbeDeliveryBinTables(int docEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        var results = new System.Collections.Generic.Dictionary<string, object>();
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

            // Probe 1: OIBD with AbsEntry = docEntry (original query, no N prefix)
            ProbeQuery(rs, results, "OIBD_AbsEntry",
                $"SELECT TOP 5 * FROM OIBD WHERE AbsEntry = {docEntry}");

            // Probe 2: OIBD with DocEntry column
            ProbeQuery(rs, results, "OIBD_DocEntry",
                $"SELECT TOP 5 * FROM OIBD WHERE DocEntry = {docEntry}");

            // Probe 3: IBD1 with AbsEntry = docEntry
            ProbeQuery(rs, results, "IBD1_AbsEntry",
                $"SELECT TOP 5 * FROM IBD1 WHERE AbsEntry = {docEntry}");

            // Probe 4: IBD1 with DocEntry column
            ProbeQuery(rs, results, "IBD1_DocEntry",
                $"SELECT TOP 5 * FROM IBD1 WHERE DocEntry = {docEntry}");

            // Probe 5: IBD1 with ObjType = '15' filter
            ProbeQuery(rs, results, "IBD1_ObjType15",
                $"SELECT TOP 5 * FROM IBD1 WHERE AbsEntry = {docEntry} AND ObjType = '15'");

            // Probe 6: column list for OIBD (check it exists)
            ProbeQuery(rs, results, "OIBD_Schema",
                "SELECT TOP 0 * FROM OIBD");

            // Probe 7: column list for IBD1
            ProbeQuery(rs, results, "IBD1_Schema",
                "SELECT TOP 0 * FROM IBD1");

            // Probe 8: all tables with BIN in the name
            ProbeQuery(rs, results, "SysTables_BIN",
                "SELECT name FROM SYS.TABLES WHERE name LIKE '%BIN%' ORDER BY name");

            // Probe 9: all tables with IB in name (IBD1, OIBD, etc.)
            ProbeQuery(rs, results, "SysTables_IB",
                "SELECT name FROM SYS.TABLES WHERE name LIKE '%IB%' ORDER BY name");

            // Probe 10: OIBQ is confirmed to exist — check its columns for reference
            ProbeQuery(rs, results, "OIBQ_Schema",
                "SELECT TOP 0 * FROM OIBQ");

            // Probe 11: ABIN — possible Allocation BIN table
            ProbeQuery(rs, results, "ABIN_Schema",
                "SELECT TOP 0 * FROM ABIN");
            ProbeQuery(rs, results, "ABIN_DocEntry30504",
                $"SELECT TOP 5 * FROM ABIN WHERE DocEntry = {docEntry}");
            ProbeQuery(rs, results, "ABIN_AbsEntry30504",
                $"SELECT TOP 5 * FROM ABIN WHERE AbsEntry = {docEntry}");

            // Probe 12: OAIB — unknown IB table
            ProbeQuery(rs, results, "OAIB_Schema",
                "SELECT TOP 0 * FROM OAIB");
            ProbeQuery(rs, results, "OAIB_Sample",
                "SELECT TOP 3 * FROM OAIB");

            // Probe 13: full list of tables containing BIN (no TOP limit)
            ProbeQuery(rs, results, "SysTables_BIN_Full",
                "SELECT name FROM SYS.TABLES WHERE name LIKE '%BIN%' AND name NOT LIKE '@%' ORDER BY name");

            // ── Extended probes for Picker/OPKL/OINV design gate ──────────────

            // OUSR — SAP user table
            ProbeQuery(rs, results, "OUSR_All",
                "SELECT USERID, USER_CODE, U_NAME, LOCKED FROM OUSR ORDER BY USERID");

            // OPKL schema
            ProbeQuery(rs, results, "OPKL_Schema",
                "SELECT TOP 0 * FROM OPKL");

            // PKL1 schema
            ProbeQuery(rs, results, "PKL1_Schema",
                "SELECT TOP 0 * FROM PKL1");

            // OPKL rows for AbsEntry 8 and 9
            ProbeQuery(rs, results, "OPKL_8_9",
                "SELECT * FROM OPKL WHERE AbsEntry IN (8,9)");

            // PKL1 rows for AbsEntry 8 and 9
            ProbeQuery(rs, results, "PKL1_8_9",
                "SELECT * FROM PKL1 WHERE AbsEntry IN (8,9)");

            // OINV schema (check columns including UDFs)
            ProbeQuery(rs, results, "OINV_Schema",
                "SELECT TOP 0 * FROM OINV");

            // INV1 schema (invoice lines)
            ProbeQuery(rs, results, "INV1_Schema",
                "SELECT TOP 0 * FROM INV1");

            // Check OINV UDFs specifically
            ProbeQuery(rs, results, "OINV_UDF_Check",
                "SELECT name, max_length, is_nullable FROM SYS.COLUMNS WHERE OBJECT_ID = OBJECT_ID('OINV') AND name LIKE 'U_%' ORDER BY name");

            // ODLN UDFs for comparison
            ProbeQuery(rs, results, "ODLN_UDF_Check",
                "SELECT name, max_length, is_nullable FROM SYS.COLUMNS WHERE OBJECT_ID = OBJECT_ID('ODLN') AND name LIKE 'U_%' ORDER BY name");

            // Recent OINV for non-ZF deliveries (to see baseline invoice linkage)
            ProbeQuery(rs, results, "OINV_Recent_NonZF",
                "SELECT TOP 3 T0.DocEntry, T0.DocNum, T0.CardCode, T0.DocStatus, T0.BaseEntry, T0.DocDate FROM OINV T0 WHERE ISNULL(T0.U_ZoneRef,'') <> 'ZoneFulfillment' ORDER BY T0.DocEntry DESC");

            // INV1 lines for a recent invoice to verify base linkage
            ProbeQuery(rs, results, "INV1_Recent_Lines",
                "SELECT TOP 5 T0.DocEntry, T0.LineNum, T0.BaseType, T0.BaseEntry, T0.BaseLine FROM INV1 T0 WHERE T0.DocEntry = (SELECT TOP 1 DocEntry FROM OINV WHERE ISNULL(U_ZoneRef,'') <> 'ZoneFulfillment' ORDER BY DocEntry DESC)");

            // PKL2 schema (bin allocation for pick lists)
            ProbeQuery(rs, results, "PKL2_Schema",
                "SELECT TOP 0 * FROM PKL2");

            // PKL2 rows for AbsEntry 9
            ProbeQuery(rs, results, "PKL2_AbsEntry9",
                "SELECT * FROM PKL2 WHERE AbsEntry = 9");

            // ── Picker resolution gate ─────────────────────────────────────────
            // Resolve configured picker OUSR USERIDs (21,24,26,23)
            ProbeQuery(rs, results, "OUSR_Pickers",
                "SELECT USERID, USER_CODE, U_NAME, LOCKED FROM OUSR WHERE USERID IN (21,24,26,23) ORDER BY USERID");

            // Full OUSR (no row cap in SQL — caller capped by ProbeQuery 5-row limit, but useful for completeness check)
            ProbeQuery(rs, results, "OUSR_Count",
                "SELECT COUNT(*) AS TotalUsers FROM OUSR");

            // ── OINV targeted UDF check ────────────────────────────────────────
            ProbeQuery(rs, results, "OINV_UDF_ZoneRef",
                "SELECT name, max_length, is_nullable, column_id FROM SYS.COLUMNS WHERE OBJECT_ID = OBJECT_ID('OINV') AND name IN ('U_ReplitId','U_ZoneRef','U_DeliveryLocation') ORDER BY name");

            ProbeQuery(rs, results, "ODLN_UDF_ZoneRef",
                "SELECT name, max_length, is_nullable, column_id FROM SYS.COLUMNS WHERE OBJECT_ID = OBJECT_ID('ODLN') AND name IN ('U_ReplitId','U_ZoneRef','U_DeliveryLocation') ORDER BY name");

            // ── DLN1 lines for Delivery 30504 ────────────────────────────────
            ProbeQuery(rs, results, "DLN1_30504",
                "SELECT LineNum, ItemCode, Dscription, Quantity, OpenQty, WhsCode, BaseType, BaseEntry, BaseLine, TargetType, TrgetEntry, Price, LineTotal, Currency FROM DLN1 WHERE DocEntry = 30504 ORDER BY LineNum");

            // ODLN header for 30504
            ProbeQuery(rs, results, "ODLN_30504",
                "SELECT DocEntry, DocNum, CardCode, CardName, DocDate, DocDueDate, DocStatus, DocCur, DocTotal, SlpCode, U_ZoneRef, U_ReplitId, U_DeliveryLocation FROM ODLN WHERE DocEntry = 30504");

            // ── Payment terms for CUS001181 ───────────────────────────────────
            ProbeQuery(rs, results, "OCRD_CUS001181",
                "SELECT CardCode, CardName, GroupNum, PayTermsGrpCode FROM OCRD WHERE CardCode = 'CUS001181'");

            ProbeQuery(rs, results, "OCTG_All",
                "SELECT GroupNum, PymntGroup, ExtraMonth, ExtraDays FROM OCTG ORDER BY GroupNum");

            // DocDueDate on source SO 28433 and ODLN 30504 for comparison
            ProbeQuery(rs, results, "SO_28433_DueDate",
                "SELECT DocEntry, DocNum, CardCode, DocDueDate, DocDate FROM ORDR WHERE DocEntry = 28433");

            // OPKL OwnerCode→OUSR linkage verification: check UserSign on OPKL row we created vs OUSR
            ProbeQuery(rs, results, "OPKL_9_Full",
                "SELECT O.AbsEntry, O.Name, O.OwnerCode, O.UserSign, O.UserSign2, O.Status, O.U_ReplitId, U.USER_CODE, U.U_NAME FROM OPKL O LEFT JOIN OUSR U ON U.USERID = O.OwnerCode WHERE O.AbsEntry = 9");

            // ── OPKL timestamp / change-tracking column metadata ──────────────────
            // Priority order: UpdateDate + UpdateTS first (critical for delta predicate)
            ProbeQuery(rs, results, "OPKL_Timestamp_Cols",
                "SELECT name, column_id, system_type_id, max_length, is_nullable " +
                "FROM SYS.COLUMNS " +
                "WHERE OBJECT_ID = OBJECT_ID('OPKL') " +
                "  AND name IN ('UpdateDate','UpdateTS','CreateDate','CreateTS','UserSign','UserSign2') " +
                "ORDER BY CASE name WHEN 'UpdateDate' THEN 1 WHEN 'UpdateTS' THEN 2 WHEN 'CreateDate' THEN 3 WHEN 'CreateTS' THEN 4 ELSE 5 END");

            // ── OPKL live headers for AbsEntry 8 and 9 ───────────────────────────
            // NOTE: OPKL has no UpdateTS or CreateTS columns (confirmed by metadata probe)
            ProbeQuery(rs, results, "OPKL_8_9_Headers",
                "SELECT AbsEntry, Name, OwnerCode, Status, Canceled, PickDate, " +
                "       CreateDate, UpdateDate, U_ReplitId " +
                "FROM OPKL WHERE AbsEntry IN (8, 9) ORDER BY AbsEntry");

            // ── PKL1 lines for AbsEntry 8 and 9 ──────────────────────────────────
            ProbeQuery(rs, results, "PKL1_8_9_Lines",
                "SELECT AbsEntry, PickEntry, OrderEntry, OrderLine, BaseObject, " +
                "       RelQtty, PickQtty, PickStatus, PrevReleas " +
                "FROM PKL1 WHERE AbsEntry IN (8, 9) ORDER BY AbsEntry, PickEntry");

            // ── ODLN status for deliveries linked from OPKL 8 and 9 ──────────────
            // OPKL 9 links to ODLN 30504 (known). OPKL 8 — resolve via PKL1 OrderEntry
            ProbeQuery(rs, results, "ODLN_Linked_OPKL_8_9",
                "SELECT O.DocEntry, O.DocNum, O.DocStatus, O.CANCELED, O.CardCode, " +
                "       O.U_ZoneRef, O.U_ReplitId " +
                "FROM ODLN O " +
                "WHERE O.DocEntry IN (" +
                "    SELECT DISTINCT T.TrgetEntry FROM DLN1 T " +
                "    JOIN PKL1 P ON P.OrderEntry = T.BaseEntry AND P.OrderLine = T.BaseLine " +
                "    WHERE P.AbsEntry IN (8,9) AND T.BaseType = 17 AND T.TargetType = 15 AND T.TrgetEntry IS NOT NULL" +
                "    UNION " +
                "    SELECT 30504" +  // OPKL 9 known delivery
                ") ORDER BY O.DocEntry");

            // ── OPKL all timestamp-type columns (full list, to catch any we missed) ─
            ProbeQuery(rs, results, "OPKL_All_Date_Cols",
                "SELECT name, column_id, system_type_id " +
                "FROM SYS.COLUMNS " +
                "WHERE OBJECT_ID = OBJECT_ID('OPKL') " +
                "  AND system_type_id IN (40,41,42,43,58,61) " +  // date, time, datetime2, datetimeoffset, smalldatetime, datetime
                "ORDER BY column_id");
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
        return results;
    }

    private static void ProbeQuery(Recordset rs, System.Collections.Generic.Dictionary<string, object> results, string key, string sql)
    {
        try
        {
            rs.DoQuery(sql);
            if (rs.EoF)
            {
                results[key] = new { status = "OK_EMPTY", sql = sql };
                return;
            }
            var rows = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
            while (!rs.EoF && rows.Count < 5)
            {
                var row = new System.Collections.Generic.Dictionary<string, object?>();
                for (int i = 0; i < rs.Fields.Count; i++)
                    row[rs.Fields.Item(i).Name] = rs.Fields.Item(i).Value;
                rows.Add(row);
                rs.MoveNext();
            }
            results[key] = new { status = "OK_ROWS", rowCount = rows.Count, rows = rows, sql = sql };
        }
        catch (Exception ex)
        {
            results[key] = new { status = "ERROR", error = ex.Message, sql = sql };
        }
    }

    /// <summary>
    /// Reads PKL1 line state for a pick list + SO line combination.
    /// Returns null if the pick list line is not found.
    /// </summary>
    public Pkl1LineState? GetPkl1LineState(int pickListAbsEntry, int soDocEntry, int soLineNum)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT AbsEntry, OrderEntry, OrderLine, BaseObject, RelQtty, PickQtty, PickStatus
                FROM   PKL1
                WHERE  AbsEntry    = {pickListAbsEntry}
                  AND  OrderEntry  = {soDocEntry}
                  AND  OrderLine   = {soLineNum}");
            if (rs.EoF) return null;
            return new Pkl1LineState(
                AbsEntry:   Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                OrderEntry: Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                OrderLine:  Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                BaseObject: Convert.ToInt32(rs.Fields.Item("BaseObject").Value),
                RelQtty:    Convert.ToDecimal(rs.Fields.Item("RelQtty").Value),
                PickQtty:   Convert.ToDecimal(rs.Fields.Item("PickQtty").Value),
                PickStatus: rs.Fields.Item("PickStatus").Value?.ToString() ?? "");
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Reads bin allocations for a pick list line via DI API pl.Lines.BinAllocations.
    /// Returns all bin rows (multi-bin explicit).
    /// </summary>
    public List<BinPickAlloc> GetPickListBinAllocations(
        int pickListAbsEntry, int soDocEntry, int soLineNum)
    {
        var company = GetConnectedCompany();
        var result  = new List<BinPickAlloc>();
        dynamic pl  = null;
        try
        {
            pl = company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.oPickLists);
            if (pl.GetByKey(pickListAbsEntry) != true && pl.AbsEntry != pickListAbsEntry)
                return result;

            for (int i = 0; i < pl.Lines.Count; i++)
            {
                pl.Lines.SetCurrentLine(i);
                if (pl.Lines.OrderEntry != soDocEntry || pl.Lines.OrderRowID != soLineNum)
                    continue;

                for (int b = 0; b < pl.Lines.BinAllocations.Count; b++)
                {
                    pl.Lines.BinAllocations.SetCurrentLine(b);
                    int    binAbs  = (int)pl.Lines.BinAllocations.BinAbsEntry;
                    double qty     = (double)pl.Lines.BinAllocations.Quantity;
                    string binCode = GetBinCodeByAbsEntry(binAbs) ?? $"BIN-{binAbs}";
                    result.Add(new SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc(binAbs, binCode, (decimal)qty));
                }
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ZF-SAP] GetPickListBinAllocations failed AbsEntry={Abs}", pickListAbsEntry);
        }
        finally
        {
            if (pl != null)
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(pl); } catch { }
        }
        return result;
    }

    public Task<SapReplitAPI.Models.Cache.CachedDelivery?> GetDeliveryByDocEntryAsync(int docEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rsH = null;
        Recordset? rsL = null;
        try
        {
            rsH = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsH.DoQuery($@"
SELECT T0.DocEntry, T0.DocNum, T0.DocDate, T0.DocDueDate, T0.TaxDate,
       T0.DocStatus, T0.CANCELED,
       T0.CardCode, T0.CardName, T0.DocTotal, T0.DocCur,
       T0.SlpCode, T1.SlpName,
       T0.UserSign, T0.Comments,
       T0.CreateDate, T0.CreateTS, T0.UpdateDate, T0.UpdateTS,
       T0.BPLId, T0.U_ReplitId, T0.U_ZoneRef, T0.U_DeliveryLocation
FROM ODLN T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE T0.DocEntry = {docEntry}");

            if (rsH.EoF) return Task.FromResult<SapReplitAPI.Models.Cache.CachedDelivery?>(null);

            string canceled = rsH.Fields.Item("CANCELED").Value?.ToString() ?? "N";
            string docStatus = rsH.Fields.Item("DocStatus").Value?.ToString() ?? "";

            var d = new SapReplitAPI.Models.Cache.CachedDelivery
            {
                DocEntry    = Convert.ToInt32(rsH.Fields.Item("DocEntry").Value),
                DocNum      = Convert.ToInt32(rsH.Fields.Item("DocNum").Value),
                DocDate     = Convert.ToDateTime(rsH.Fields.Item("DocDate").Value),
                DocDueDate  = Convert.ToDateTime(rsH.Fields.Item("DocDueDate").Value),
                TaxDate     = Convert.ToDateTime(rsH.Fields.Item("TaxDate").Value),
                DocStatus   = docStatus,
                Canceled    = canceled,
                CardCode    = rsH.Fields.Item("CardCode").Value?.ToString() ?? "",
                CardName    = rsH.Fields.Item("CardName").Value?.ToString() ?? "",
                DocTotal    = Convert.ToDecimal(rsH.Fields.Item("DocTotal").Value),
                DocCur      = rsH.Fields.Item("DocCur").Value?.ToString() ?? "",
                SlpCode     = Convert.ToInt32(rsH.Fields.Item("SlpCode").Value),
                SlpName     = rsH.Fields.Item("SlpName").Value?.ToString() ?? "",
                UserSign    = Convert.ToInt32(rsH.Fields.Item("UserSign").Value),
                Comments    = rsH.Fields.Item("Comments").Value?.ToString() ?? "",
                CreateDate  = Convert.ToDateTime(rsH.Fields.Item("CreateDate").Value),
                CreateTS    = Convert.ToInt32(rsH.Fields.Item("CreateTS").Value),
                UpdateDate  = Convert.ToDateTime(rsH.Fields.Item("UpdateDate").Value),
                UpdateTS    = Convert.ToInt32(rsH.Fields.Item("UpdateTS").Value),
                BPLId            = Convert.ToInt32(rsH.Fields.Item("BPLId").Value),
                U_ReplitId       = rsH.Fields.Item("U_ReplitId").Value?.ToString(),
                ZoneRef          = rsH.Fields.Item("U_ZoneRef").Value?.ToString(),
                DeliveryLocation = rsH.Fields.Item("U_DeliveryLocation").Value?.ToString(),
                DocStatusDisplay = ComputeDeliveryStatusDisplay(canceled, docStatus),
                Lines       = new List<SapReplitAPI.Models.Cache.CachedDeliveryLine>()
            };

            rsL = (Recordset)_company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rsL.DoQuery($@"
SELECT LineNum, ItemCode, Dscription, Quantity, OpenQty, WhsCode,
       Price, LineTotal, Currency,
       BaseType, BaseEntry, BaseLine, TargetType, TrgetEntry
FROM DLN1
WHERE DocEntry = {docEntry}");

            while (!rsL.EoF)
            {
                d.Lines.Add(new SapReplitAPI.Models.Cache.CachedDeliveryLine
                {
                    DocEntry   = docEntry,
                    LineNum    = Convert.ToInt32(rsL.Fields.Item("LineNum").Value),
                    ItemCode   = rsL.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Dscription = rsL.Fields.Item("Dscription").Value?.ToString() ?? "",
                    Quantity   = Convert.ToDecimal(rsL.Fields.Item("Quantity").Value),
                    OpenQty    = Convert.ToDecimal(rsL.Fields.Item("OpenQty").Value),
                    WhsCode    = rsL.Fields.Item("WhsCode").Value?.ToString() ?? "",
                    Price      = Convert.ToDecimal(rsL.Fields.Item("Price").Value),
                    LineTotal  = Convert.ToDecimal(rsL.Fields.Item("LineTotal").Value),
                    Currency   = rsL.Fields.Item("Currency").Value?.ToString() ?? "",
                    BaseType   = Convert.ToInt32(rsL.Fields.Item("BaseType").Value),
                    BaseEntry  = Convert.ToInt32(rsL.Fields.Item("BaseEntry").Value),
                    BaseLine   = Convert.ToInt32(rsL.Fields.Item("BaseLine").Value),
                    TargetType = Convert.ToInt32(rsL.Fields.Item("TargetType").Value),
                    TrgetEntry = Convert.ToInt32(rsL.Fields.Item("TrgetEntry").Value)
                });
                rsL.MoveNext();
            }

            return Task.FromResult<SapReplitAPI.Models.Cache.CachedDelivery?>(d);
        }
        finally
        {
            if (rsH != null) Marshal.ReleaseComObject(rsH);
            if (rsL != null) Marshal.ReleaseComObject(rsL);
        }
    }

    // For a cancellation delivery (CANCELED='C'), find the original via DLN1.BaseEntry where BaseType=15.
    public Task<int?> GetOriginalDeliveryDocEntryAsync(int cancellationDocEntry, CancellationToken ct = default)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT TOP 1 BaseEntry
FROM DLN1
WHERE DocEntry  = {cancellationDocEntry}
  AND BaseType  = 15
  AND BaseEntry > 0");
            if (rs.EoF) return Task.FromResult<int?>(null);
            int baseEntry = Convert.ToInt32(rs.Fields.Item("BaseEntry").Value);
            return Task.FromResult<int?>(baseEntry > 0 ? baseEntry : null);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // Read ALL ODLN rows for a full or delta delivery sync.
    public List<SapReplitAPI.Models.Cache.CachedDelivery> GetDeliveryHeaders(DateTime? from = null, DateTime? to = null)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            // SAP stores UpdateDate (date-only) and UpdateTS (HHMMSS integer) in server local time (EAT = UTC+3).
            // Bug fix: whereClause was only populated when both from AND to were set; delta calls pass to=null,
            // so the filter was always empty and every run fetched all ODLN rows (full scan).
            // Fix: apply the from-side filter whenever from is provided, with or without to.
            // Also: convert UTC effectiveFrom to EAT before extracting date+time components.
            string whereClause = "";
            if (from.HasValue)
            {
                var tz        = TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");
                var localFrom = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from.Value, DateTimeKind.Utc), tz);
                var fromDate  = localFrom.ToString("yyyy-MM-dd");
                var fromTs    = localFrom.Hour * 10000 + localFrom.Minute * 100 + localFrom.Second;
                whereClause   = to.HasValue
                    ? $"WHERE (T0.UpdateDate > '{fromDate}' OR (T0.UpdateDate = '{fromDate}' AND T0.UpdateTS >= {fromTs})) AND T0.UpdateDate <= '{to.Value:yyyy-MM-dd}'"
                    : $"WHERE (T0.UpdateDate > '{fromDate}' OR (T0.UpdateDate = '{fromDate}' AND T0.UpdateTS >= {fromTs}))";
            }

            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT T0.DocEntry, T0.DocNum, T0.DocDate, T0.DocDueDate, T0.TaxDate,
       T0.DocStatus, T0.CANCELED,
       T0.CardCode, T0.CardName, T0.DocTotal, T0.DocCur,
       T0.SlpCode, T1.SlpName,
       T0.UserSign, T0.Comments,
       T0.CreateDate, T0.CreateTS, T0.UpdateDate, T0.UpdateTS,
       T0.BPLId, T0.U_ReplitId
FROM ODLN T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
{whereClause}
ORDER BY T0.DocEntry ASC");

            var list = new List<SapReplitAPI.Models.Cache.CachedDelivery>();
            while (!rs.EoF)
            {
                string canceled  = rs.Fields.Item("CANCELED").Value?.ToString() ?? "N";
                string docStatus = rs.Fields.Item("DocStatus").Value?.ToString() ?? "";
                list.Add(new SapReplitAPI.Models.Cache.CachedDelivery
                {
                    DocEntry    = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    DocNum      = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    DocDate     = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    DocDueDate  = Convert.ToDateTime(rs.Fields.Item("DocDueDate").Value),
                    TaxDate     = Convert.ToDateTime(rs.Fields.Item("TaxDate").Value),
                    DocStatus   = docStatus,
                    Canceled    = canceled,
                    CardCode    = rs.Fields.Item("CardCode").Value?.ToString() ?? "",
                    CardName    = rs.Fields.Item("CardName").Value?.ToString() ?? "",
                    DocTotal    = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                    DocCur      = rs.Fields.Item("DocCur").Value?.ToString() ?? "",
                    SlpCode     = Convert.ToInt32(rs.Fields.Item("SlpCode").Value),
                    SlpName     = rs.Fields.Item("SlpName").Value?.ToString() ?? "",
                    UserSign    = Convert.ToInt32(rs.Fields.Item("UserSign").Value),
                    Comments    = rs.Fields.Item("Comments").Value?.ToString() ?? "",
                    CreateDate  = Convert.ToDateTime(rs.Fields.Item("CreateDate").Value),
                    CreateTS    = Convert.ToInt32(rs.Fields.Item("CreateTS").Value),
                    UpdateDate  = Convert.ToDateTime(rs.Fields.Item("UpdateDate").Value),
                    UpdateTS    = Convert.ToInt32(rs.Fields.Item("UpdateTS").Value),
                    BPLId       = Convert.ToInt32(rs.Fields.Item("BPLId").Value),
                    U_ReplitId  = rs.Fields.Item("U_ReplitId").Value?.ToString(),
                    DocStatusDisplay = ComputeDeliveryStatusDisplay(canceled, docStatus)
                });
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    public List<SapReplitAPI.Models.Cache.CachedDeliveryLine> GetDeliveryLines(IEnumerable<int> docEntries)
    {
        var entries = docEntries.ToList();
        if (entries.Count == 0) return new();

        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            string inClause = string.Join(",", entries);
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT DocEntry, LineNum, ItemCode, Dscription, Quantity, OpenQty, WhsCode,
       Price, LineTotal, Currency,
       BaseType, BaseEntry, BaseLine, TargetType, TrgetEntry
FROM DLN1
WHERE DocEntry IN ({inClause})
ORDER BY DocEntry, LineNum");

            var list = new List<SapReplitAPI.Models.Cache.CachedDeliveryLine>();
            while (!rs.EoF)
            {
                list.Add(new SapReplitAPI.Models.Cache.CachedDeliveryLine
                {
                    DocEntry   = Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    LineNum    = Convert.ToInt32(rs.Fields.Item("LineNum").Value),
                    ItemCode   = rs.Fields.Item("ItemCode").Value?.ToString() ?? "",
                    Dscription = rs.Fields.Item("Dscription").Value?.ToString() ?? "",
                    Quantity   = Convert.ToDecimal(rs.Fields.Item("Quantity").Value),
                    OpenQty    = Convert.ToDecimal(rs.Fields.Item("OpenQty").Value),
                    WhsCode    = rs.Fields.Item("WhsCode").Value?.ToString() ?? "",
                    Price      = Convert.ToDecimal(rs.Fields.Item("Price").Value),
                    LineTotal  = Convert.ToDecimal(rs.Fields.Item("LineTotal").Value),
                    Currency   = rs.Fields.Item("Currency").Value?.ToString() ?? "",
                    BaseType   = Convert.ToInt32(rs.Fields.Item("BaseType").Value),
                    BaseEntry  = Convert.ToInt32(rs.Fields.Item("BaseEntry").Value),
                    BaseLine   = Convert.ToInt32(rs.Fields.Item("BaseLine").Value),
                    TargetType = Convert.ToInt32(rs.Fields.Item("TargetType").Value),
                    TrgetEntry = Convert.ToInt32(rs.Fields.Item("TrgetEntry").Value)
                });
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // Read item codes from an arbitrary line table for a single DocEntry.
    // tableAlias = e.g. "RDR1", "DLN1", "RDN1", "PDN1", "IGN1", "IGE1", "WTR1"
    public List<string> GetItemCodesFromLines(string lineTable, int docEntry)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DISTINCT ItemCode FROM {lineTable} WHERE DocEntry = {docEntry} AND ItemCode IS NOT NULL AND ItemCode <> ''");
            var list = new List<string>();
            while (!rs.EoF)
            {
                var ic = rs.Fields.Item("ItemCode").Value?.ToString();
                if (!string.IsNullOrWhiteSpace(ic)) list.Add(ic.Trim().ToUpperInvariant());
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // Read distinct delivery DocEntries referenced from a lines table via BaseType=15.
    // Used by 13/A (INV1), 16/A (RDN1).
    public List<int> GetBaseDeliveryDocEntries(string lineTable, int docEntry)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT DISTINCT BaseEntry
FROM {lineTable}
WHERE DocEntry  = {docEntry}
  AND BaseType  = 15
  AND BaseEntry > 0");
            var list = new List<int>();
            while (!rs.EoF)
            {
                list.Add(Convert.ToInt32(rs.Fields.Item("BaseEntry").Value));
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ─── Delivery Gate Diagnostics (read-only) ───────────────────────────────
    // Used exclusively by the controlled delivery gate to verify live SAP state
    // before any ODLN mutation is authorized.

    public ZfDeliveryGateDiagnostics GetDeliveryGateDiagnostics(int pickListAbsEntry, int soDocEntry, int soLineNum, string itemCode, string whsCode)
    {
        var company = GetConnectedCompany();
        var diag    = new ZfDeliveryGateDiagnostics();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);

            // 1. ODLN UDF column metadata
            rs.DoQuery(@"
                SELECT c.name, t.name AS sql_type,
                       c.is_nullable, c.max_length
                FROM   sys.columns c
                JOIN   sys.types   t ON c.user_type_id = t.user_type_id
                WHERE  c.object_id = OBJECT_ID('ODLN')
                  AND  c.name IN ('U_ReplitId','U_ZoneRef','U_DeliveryLocation')
                ORDER  BY c.name");
            while (!rs.EoF)
            {
                diag.OdlnUdfColumns.Add(new ZfUdfColumnInfo(
                    Name       : rs.Fields.Item("name").Value?.ToString() ?? "",
                    SqlType    : rs.Fields.Item("sql_type").Value?.ToString() ?? "",
                    IsNullable : Convert.ToInt32(rs.Fields.Item("is_nullable").Value) == 1,
                    MaxLength  : Convert.ToInt32(rs.Fields.Item("max_length").Value)));
                rs.MoveNext();
            }

            // 2. PKL-related table names in MOLAS_Live_2021
            rs.DoQuery(@"
                SELECT TABLE_NAME
                FROM   INFORMATION_SCHEMA.TABLES
                WHERE  TABLE_TYPE = 'BASE TABLE'
                  AND  (TABLE_NAME LIKE 'PKL%' OR TABLE_NAME LIKE 'OPKL%')
                ORDER  BY TABLE_NAME");
            while (!rs.EoF)
            {
                diag.PklTableNames.Add(rs.Fields.Item("TABLE_NAME").Value?.ToString() ?? "");
                rs.MoveNext();
            }

            // 3. sys.columns for each PKL* table found — look for bin-related columns
            if (diag.PklTableNames.Count > 0)
            {
                string tableList = string.Join(",", diag.PklTableNames.Select(n => $"'{n}'"));
                rs.DoQuery($@"
                    SELECT tb.name AS tbl, c.name AS col, t.name AS sql_type, c.max_length
                    FROM   sys.columns c
                    JOIN   sys.tables  tb ON c.object_id = tb.object_id
                    JOIN   sys.types   t  ON c.user_type_id = t.user_type_id
                    WHERE  tb.name IN ({tableList})
                      AND  (c.name LIKE '%Bin%' OR c.name LIKE '%Alloc%'
                            OR c.name LIKE '%Pick%' OR c.name LIKE '%Qty%'
                            OR c.name LIKE '%Abs%'  OR c.name LIKE '%Entry%'
                            OR c.name LIKE '%Line%' OR c.name LIKE '%Order%')
                    ORDER  BY tb.name, c.column_id");
                while (!rs.EoF)
                {
                    diag.PklColumnDetails.Add(new ZfTableColumnInfo(
                        Table    : rs.Fields.Item("tbl").Value?.ToString() ?? "",
                        Column   : rs.Fields.Item("col").Value?.ToString() ?? "",
                        SqlType  : rs.Fields.Item("sql_type").Value?.ToString() ?? "",
                        MaxLength: Convert.ToInt32(rs.Fields.Item("max_length").Value)));
                    rs.MoveNext();
                }
            }

            // 4. Direct PKL1 + any *BIN* or *ALLOC* rows for this pick list
            rs.DoQuery($@"
                SELECT AbsEntry, PickEntry, OrderEntry, OrderLine, PickQtty, RelQtty, PickStatus
                FROM   PKL1
                WHERE  AbsEntry = {pickListAbsEntry}");
            while (!rs.EoF)
            {
                diag.Pkl1Rows.Add(new ZfPkl1Row(
                    AbsEntry   : Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                    PickEntry  : Convert.ToInt32(rs.Fields.Item("PickEntry").Value),
                    OrderEntry : Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                    OrderLine  : Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                    PickQtty   : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value),
                    RelQtty    : Convert.ToDecimal(rs.Fields.Item("RelQtty").Value),
                    PickStatus : rs.Fields.Item("PickStatus").Value?.ToString() ?? ""));
                rs.MoveNext();
            }

            // 5. OIBQ current bin stock for this item/warehouse (post-pick state)
            rs.DoQuery($@"
                SELECT B.AbsEntry AS BinAbsEntry, B.BinCode, I.OnHandQty
                FROM   OIBQ I
                JOIN   OBIN B ON I.BinAbs = B.AbsEntry
                WHERE  I.ItemCode = N'{itemCode.Replace("'","''")}'
                  AND  I.WhsCode  = N'{whsCode.Replace("'","''")}' ");
            while (!rs.EoF)
            {
                diag.OibqRows.Add(new ZfOibqRow(
                    BinAbsEntry : Convert.ToInt32(rs.Fields.Item("BinAbsEntry").Value),
                    BinCode     : rs.Fields.Item("BinCode").Value?.ToString()?.Trim() ?? "",
                    OnHandQty   : Convert.ToDecimal(rs.Fields.Item("OnHandQty").Value)));
                rs.MoveNext();
            }

            // 6. RDR1.Dscription for the SO line — base-document copy check
            rs.DoQuery($@"
                SELECT Dscription, OpenQty, DocEntry, LineNum
                FROM   RDR1
                WHERE  DocEntry = {soDocEntry}
                  AND  LineNum  = {soLineNum}");
            if (!rs.EoF)
            {
                diag.Rdr1Dscription = rs.Fields.Item("Dscription").Value?.ToString();
                diag.Rdr1OpenQty    = Convert.ToDecimal(rs.Fields.Item("OpenQty").Value);
            }

            // 7. Multi-WHS ODLN evidence — find any existing delivery with >=2 distinct WhsCodes
            rs.DoQuery(@"
                SELECT TOP 5 L.DocEntry, COUNT(DISTINCT L.WhsCode) AS WshCount,
                       STUFF((SELECT DISTINCT ',' + L2.WhsCode FROM DLN1 L2
                              WHERE L2.DocEntry = L.DocEntry FOR XML PATH('')),1,1,'') AS WhsCodes
                FROM   DLN1 L
                GROUP  BY L.DocEntry
                HAVING COUNT(DISTINCT L.WhsCode) >= 2
                ORDER  BY L.DocEntry DESC");
            while (!rs.EoF)
            {
                diag.MultiWhsDeliveries.Add(new ZfMultiWhsDelivery(
                    DocEntry  : Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    WhsCount  : Convert.ToInt32(rs.Fields.Item("WshCount").Value),
                    WhsCodes  : rs.Fields.Item("WhsCodes").Value?.ToString() ?? ""));
                rs.MoveNext();
            }

            // 8. DI API pick list BinAllocations read — post-pick persisted state
            try
            {
                dynamic pl = company.GetBusinessObject(BoObjectTypes.oPickLists);
                object keyResult = pl.GetByKey(pickListAbsEntry);
                bool getKeyOk = keyResult is bool bk ? bk : (int)keyResult == 0;
                if (getKeyOk)
                {
                    int lineCount = (int)pl.Lines.Count;
                    for (int i = 0; i < lineCount; i++)
                    {
                        pl.Lines.SetCurrentLine(i);
                        int lineOrderEntry = (int)pl.Lines.OrderEntry;
                        if (lineOrderEntry != soDocEntry) continue;

                        int binCount = (int)pl.Lines.BinAllocations.Count;
                        diag.DiApiPickBinCount = binCount;
                        for (int j = 0; j < binCount; j++)
                        {
                            pl.Lines.BinAllocations.SetCurrentLine(j);
                            diag.DiApiPickBinRows.Add(new ZfDiApiBinRow(
                                BinAbsEntry : (int)pl.Lines.BinAllocations.BinAbsEntry,
                                Quantity    : (double)pl.Lines.BinAllocations.Quantity,
                                SerialNumber: ""));
                        }
                        break;
                    }
                    diag.DiApiPickListLoaded = true;
                }
                else
                {
                    diag.DiApiPickListError = company.GetLastErrorDescription();
                }
                System.Runtime.InteropServices.Marshal.ReleaseComObject(pl);
            }
            catch (Exception ex)
            {
                diag.DiApiPickListError = $"{ex.GetType().Name}: {ex.Message}";
            }

            // 9. PKL2 rows for this pick list
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery($@"
                    SELECT P.AbsEntry, O.Status AS OpklStatus, P.BinAbs,
                           B.BinCode, P.PickQtty
                    FROM   PKL2 P
                    JOIN   OBIN B ON B.AbsEntry = P.BinAbs
                    JOIN   OPKL O ON O.AbsEntry = P.AbsEntry
                    WHERE  P.AbsEntry = {pickListAbsEntry}
                    ORDER  BY P.BinAbs");
                while (!rs.EoF)
                {
                    diag.Pkl2OwnRows.Add(new ZfPkl2Row(
                        AbsEntry   : Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                        OpklStatus : rs.Fields.Item("OpklStatus").Value?.ToString() ?? "",
                        BinAbs     : Convert.ToInt32(rs.Fields.Item("BinAbs").Value),
                        BinCode    : rs.Fields.Item("BinCode").Value?.ToString() ?? "",
                        PickQtty   : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value ?? 0m)));
                    rs.MoveNext();
                }
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ZF-DIAG] PKL2 own-rows query failed for AbsEntry={Abs}", pickListAbsEntry);
            }

            // 10. Cross-pick-list bin conflicts: all PKL2 rows sharing any bin used by this pick list,
            //     joined to OPKL.Status so we can see which are still open/active.
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery($@"
                    SELECT P.AbsEntry, O.Status AS OpklStatus, P.BinAbs,
                           B.BinCode, P.PickQtty
                    FROM   PKL2 P
                    JOIN   OBIN B ON B.AbsEntry = P.BinAbs
                    JOIN   OPKL O ON O.AbsEntry = P.AbsEntry
                    WHERE  P.AbsEntry <> {pickListAbsEntry}
                      AND  P.BinAbs IN (
                               SELECT BinAbs FROM PKL2 WHERE AbsEntry = {pickListAbsEntry}
                           )
                    ORDER  BY P.AbsEntry, P.BinAbs");
                while (!rs.EoF)
                {
                    diag.Pkl2BinConflicts.Add(new ZfPkl2Row(
                        AbsEntry   : Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                        OpklStatus : rs.Fields.Item("OpklStatus").Value?.ToString() ?? "",
                        BinAbs     : Convert.ToInt32(rs.Fields.Item("BinAbs").Value),
                        BinCode    : rs.Fields.Item("BinCode").Value?.ToString() ?? "",
                        PickQtty   : Convert.ToDecimal(rs.Fields.Item("PickQtty").Value ?? 0m)));
                    rs.MoveNext();
                }
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ZF-DIAG] PKL2 bin-conflict query failed for AbsEntry={Abs}", pickListAbsEntry);
            }

            // 11. Bin commitment tables discovery + OBBQ check
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery(@"
                    SELECT TABLE_NAME
                    FROM   INFORMATION_SCHEMA.TABLES
                    WHERE  TABLE_TYPE = 'BASE TABLE'
                      AND  (TABLE_NAME LIKE '%BBQ%' OR TABLE_NAME LIKE '%BAV%'
                            OR TABLE_NAME LIKE '%COMMIT%' OR TABLE_NAME LIKE 'OIB%'
                            OR TABLE_NAME LIKE 'OBBQ%')
                    ORDER  BY TABLE_NAME");
                var binTables = new List<string>();
                while (!rs.EoF)
                {
                    binTables.Add(rs.Fields.Item("TABLE_NAME").Value?.ToString() ?? "");
                    rs.MoveNext();
                }
                diag.BinCommitTables = binTables;
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ZF-DIAG] BinCommitTables query failed");
            }

            // 12. All open OPKLs for this item/warehouse via PKL1 join
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery($@"
                    SELECT L.AbsEntry, O.Status AS OpklStatus,
                           ISNULL(O.Canceled,'N') AS Canceled,
                           L.OrderEntry, L.OrderLine, L.RelQtty, L.PickQtty, L.PickStatus,
                           R.ItemCode, R.WhsCode
                    FROM   PKL1 L
                    JOIN   OPKL O ON O.AbsEntry = L.AbsEntry
                    JOIN   RDR1 R ON R.DocEntry = L.OrderEntry AND R.LineNum = L.OrderLine
                    WHERE  R.ItemCode = N'{itemCode.Replace("'","''")}'
                      AND  R.WhsCode  = N'{whsCode.Replace("'","''")}'
                    ORDER  BY L.AbsEntry DESC");
                diag.AllOpklsForItem = new List<string>();
                while (!rs.EoF)
                {
                    string entry = $"AbsEntry={rs.Fields.Item("AbsEntry").Value} Status={rs.Fields.Item("OpklStatus").Value} Canceled={rs.Fields.Item("Canceled").Value} " +
                                   $"OrderEntry={rs.Fields.Item("OrderEntry").Value} PickStatus={rs.Fields.Item("PickStatus").Value} " +
                                   $"RelQtty={rs.Fields.Item("RelQtty").Value} PickQtty={rs.Fields.Item("PickQtty").Value}";
                    diag.AllOpklsForItem.Add(entry);
                    rs.MoveNext();
                }
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ZF-DIAG] AllOpklsForItem query failed");
                diag.AllOpklsForItem = new List<string> { $"Error: {ex.Message}" };
            }

            // 13. §1 OBBQ schema — discover all columns in OBBQ table
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery(@"
                    SELECT c.column_id, c.name, t.name AS sql_type, c.max_length, c.is_nullable
                    FROM   sys.columns c
                    JOIN   sys.types   t ON c.user_type_id = t.user_type_id
                    WHERE  c.object_id = OBJECT_ID('OBBQ')
                    ORDER  BY c.column_id");
                var schema = new List<string>();
                while (!rs.EoF)
                {
                    schema.Add($"col_id={rs.Fields.Item("column_id").Value} " +
                               $"name={rs.Fields.Item("name").Value} " +
                               $"type={rs.Fields.Item("sql_type").Value}({rs.Fields.Item("max_length").Value}) " +
                               $"nullable={rs.Fields.Item("is_nullable").Value}");
                    rs.MoveNext();
                }
                diag.ObbqSchema = schema;
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                diag.ObbqSchema = new List<string> { $"Error: {ex.Message}" };
            }

            // 14. §1 OBBQ live rows for the queried item/warehouse
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery($@"
                    SELECT TOP 50 *
                    FROM   OBBQ
                    WHERE  ItemCode = N'{itemCode.Replace("'", "''")}'
                      AND  WhsCode  = N'{whsCode.Replace("'", "''")}'");
                var rows = new List<string>();
                while (!rs.EoF)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int fi = 0; fi < rs.Fields.Count; fi++)
                    {
                        var fld = rs.Fields.Item(fi);
                        sb.Append($"{fld.Name}={fld.Value} | ");
                    }
                    rows.Add(sb.ToString().TrimEnd(' ', '|'));
                    rs.MoveNext();
                }
                diag.ObbqRows = rows.Count > 0 ? rows : new List<string> { "NO_ROWS" };
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                diag.ObbqRows = new List<string> { $"Error: {ex.Message}" };
            }

            // 15. §2 OPKL 6 and 7 attribution — PKL1 + PKL2 read-only
            try
            {
                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery(@"
                    SELECT L.AbsEntry, L.PickEntry, L.OrderEntry, L.OrderLine,
                           L.RelQtty, L.PickQtty, L.PickStatus,
                           O.Status AS OpklStatus, ISNULL(O.Canceled,'N') AS Canceled,
                           ISNULL(R.ItemCode,'') AS ItemCode, ISNULL(R.WhsCode,'') AS WhsCode
                    FROM   PKL1 L
                    JOIN   OPKL O  ON O.AbsEntry = L.AbsEntry
                    LEFT JOIN RDR1 R ON R.DocEntry = L.OrderEntry AND R.LineNum = L.OrderLine
                    WHERE  L.AbsEntry IN (6, 7)
                    ORDER  BY L.AbsEntry, L.PickEntry");
                var attr = new List<string>();
                while (!rs.EoF)
                {
                    attr.Add($"PKL1: AbsEntry={rs.Fields.Item("AbsEntry").Value} PickEntry={rs.Fields.Item("PickEntry").Value} " +
                             $"OpklStatus={rs.Fields.Item("OpklStatus").Value} Canceled={rs.Fields.Item("Canceled").Value} " +
                             $"OrderEntry={rs.Fields.Item("OrderEntry").Value} OrderLine={rs.Fields.Item("OrderLine").Value} " +
                             $"RelQtty={rs.Fields.Item("RelQtty").Value} PickQtty={rs.Fields.Item("PickQtty").Value} " +
                             $"PickStatus={rs.Fields.Item("PickStatus").Value} " +
                             $"ItemCode={rs.Fields.Item("ItemCode").Value} WhsCode={rs.Fields.Item("WhsCode").Value}");
                    rs.MoveNext();
                }
                Marshal.ReleaseComObject(rs); rs = null;

                rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery(@"
                    SELECT P.AbsEntry, P.BinAbs, B.BinCode, P.PickQtty
                    FROM   PKL2 P
                    JOIN   OBIN B ON B.AbsEntry = P.BinAbs
                    WHERE  P.AbsEntry IN (6, 7)
                    ORDER  BY P.AbsEntry, P.BinAbs");
                while (!rs.EoF)
                {
                    attr.Add($"PKL2: AbsEntry={rs.Fields.Item("AbsEntry").Value} " +
                             $"BinAbs={rs.Fields.Item("BinAbs").Value} BinCode={rs.Fields.Item("BinCode").Value} " +
                             $"PickQtty={rs.Fields.Item("PickQtty").Value}");
                    rs.MoveNext();
                }
                if (attr.Count == 0) attr.Add("NO_OPKL_6_OR_7_FOUND");
                diag.Opkl6And7Attribution = attr;
                Marshal.ReleaseComObject(rs); rs = null;
            }
            catch (Exception ex)
            {
                diag.Opkl6And7Attribution = new List<string> { $"Error: {ex.Message}" };
            }

            return diag;
        }
        finally
        {
            if (rs != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
        }
    }

    private static string ComputeDeliveryStatusDisplay(string canceled, string docStatus)
    {
        if (canceled == "C") return "Cancellation";
        if (canceled == "Y") return "Cancelled";
        if (canceled == "N" && docStatus == "O") return "Open";
        if (canceled == "N" && docStatus == "C") return "Closed";
        return docStatus;
    }

    // ─── Pick List cache reads (OPKL / PKL1 / PKL2 / OBIN / OUSR / RDR1 / ORDR) ──
    // All methods are read-only — no SAP mutations.

    /// <summary>
    /// Read OPKL headers. fromDate=null → full sync; fromDate set → delta (date-only, no UpdateTS).
    /// Cancelled rows are included intentionally — delta must track cancellation transitions.
    /// </summary>
    public List<SapReplitAPI.Models.Cache.CachedPickList> GetPickListHeaders(DateTime? fromDate = null)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            string where = string.Empty;
            if (fromDate.HasValue)
            {
                var tz        = TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");
                var localFrom = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(fromDate.Value, DateTimeKind.Utc), tz).Date.AddDays(-1);
                where = $"WHERE T0.UpdateDate >= '{localFrom:yyyy-MM-dd}'";
            }

            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT T0.AbsEntry, T0.Name, T0.OwnerCode,
       ISNULL(U.U_NAME, '')   AS OwnerName,
       T0.Status, T0.Canceled,
       ISNULL(T0.Remarks, '') AS Remarks,
       T0.PickDate, T0.CreateDate, T0.UpdateDate,
       T0.U_ReplitId,
       S.SlpCode,
       ISNULL(S.SlpName, '') AS SlpName,
       CASE WHEN (SELECT COUNT(DISTINCT O2.U_ZoneRef)
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_ZoneRef IS NOT NULL AND O2.U_ZoneRef <> '') = 1
            THEN (SELECT TOP 1 O2.U_ZoneRef
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_ZoneRef IS NOT NULL AND O2.U_ZoneRef <> '')
            ELSE NULL END AS ZoneRef,
       CASE WHEN (SELECT COUNT(DISTINCT O2.U_DeliveryLocation)
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_DeliveryLocation IS NOT NULL AND O2.U_DeliveryLocation <> '') = 1
            THEN (SELECT TOP 1 O2.U_DeliveryLocation
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_DeliveryLocation IS NOT NULL AND O2.U_DeliveryLocation <> '')
            ELSE NULL END AS DeliveryLocation
FROM OPKL T0
LEFT JOIN OUSR U ON U.USERID = T0.OwnerCode
LEFT JOIN ORDR FO ON FO.DocEntry = (
    SELECT TOP 1 OrderEntry FROM PKL1 WHERE AbsEntry = T0.AbsEntry ORDER BY PickEntry)
LEFT JOIN OSLP S ON S.SlpCode = FO.SlpCode
{where}
ORDER BY T0.AbsEntry ASC");

            var list = new List<SapReplitAPI.Models.Cache.CachedPickList>();
            var now  = DateTime.UtcNow;
            while (!rs.EoF)
            {
                var pickDateRaw   = rs.Fields.Item("PickDate").Value;
                var createDateRaw = rs.Fields.Item("CreateDate").Value;
                var updateDateRaw = rs.Fields.Item("UpdateDate").Value;
                list.Add(new SapReplitAPI.Models.Cache.CachedPickList
                {
                    AbsEntry     = Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                    Name         = rs.Fields.Item("Name").Value?.ToString() ?? string.Empty,
                    OwnerCode    = Convert.ToInt32(rs.Fields.Item("OwnerCode").Value),
                    OwnerName    = rs.Fields.Item("OwnerName").Value?.ToString() ?? string.Empty,
                    Status       = rs.Fields.Item("Status").Value?.ToString() ?? string.Empty,
                    Canceled     = rs.Fields.Item("Canceled").Value?.ToString() ?? "N",
                    Remarks      = rs.Fields.Item("Remarks").Value?.ToString() ?? string.Empty,
                    PickDate     = pickDateRaw   is DBNull or null ? DateTime.MinValue : Convert.ToDateTime(pickDateRaw),
                    CreateDate   = createDateRaw is DBNull or null ? DateTime.MinValue : Convert.ToDateTime(createDateRaw),
                    UpdateDate   = updateDateRaw is DBNull or null ? DateTime.MinValue : Convert.ToDateTime(updateDateRaw),
                    U_ReplitId   = rs.Fields.Item("U_ReplitId").Value?.ToString(),
                    SlpCode          = rs.Fields.Item("SlpCode").Value is DBNull or null
                                       ? null : (int?)Convert.ToInt32(rs.Fields.Item("SlpCode").Value),
                    SlpName          = rs.Fields.Item("SlpName").Value?.ToString() ?? string.Empty,
                    LastSyncedAt     = now,
                    ZoneRef          = rs.Fields.Item("ZoneRef").Value is DBNull or null
                                       ? null : rs.Fields.Item("ZoneRef").Value?.ToString(),
                    DeliveryLocation = rs.Fields.Item("DeliveryLocation").Value is DBNull or null
                                       ? null : rs.Fields.Item("DeliveryLocation").Value?.ToString()
                });
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Fetch a single OPKL header by AbsEntry. Returns null if not found in SAP.</summary>
    public SapReplitAPI.Models.Cache.CachedPickList? GetPickListHeaderByAbsEntry(int absEntry)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            Recordset rsObj = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs = rsObj;
            rsObj.DoQuery($@"
SELECT T0.AbsEntry, T0.Name, T0.OwnerCode,
       ISNULL(U.U_NAME, '')   AS OwnerName,
       T0.Status, T0.Canceled,
       ISNULL(T0.Remarks, '') AS Remarks,
       T0.PickDate, T0.CreateDate, T0.UpdateDate,
       T0.U_ReplitId,
       S.SlpCode,
       ISNULL(S.SlpName, '') AS SlpName,
       CASE WHEN (SELECT COUNT(DISTINCT O2.U_ZoneRef)
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_ZoneRef IS NOT NULL AND O2.U_ZoneRef <> '') = 1
            THEN (SELECT TOP 1 O2.U_ZoneRef
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_ZoneRef IS NOT NULL AND O2.U_ZoneRef <> '')
            ELSE NULL END AS ZoneRef,
       CASE WHEN (SELECT COUNT(DISTINCT O2.U_DeliveryLocation)
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_DeliveryLocation IS NOT NULL AND O2.U_DeliveryLocation <> '') = 1
            THEN (SELECT TOP 1 O2.U_DeliveryLocation
                  FROM PKL1 P2 JOIN ORDR O2 ON O2.DocEntry = P2.OrderEntry
                  WHERE P2.AbsEntry = T0.AbsEntry
                    AND O2.U_DeliveryLocation IS NOT NULL AND O2.U_DeliveryLocation <> '')
            ELSE NULL END AS DeliveryLocation
FROM OPKL T0
LEFT JOIN OUSR U ON U.USERID = T0.OwnerCode
LEFT JOIN ORDR FO ON FO.DocEntry = (
    SELECT TOP 1 OrderEntry FROM PKL1 WHERE AbsEntry = T0.AbsEntry ORDER BY PickEntry)
LEFT JOIN OSLP S ON S.SlpCode = FO.SlpCode
WHERE T0.AbsEntry = {absEntry}");

            if (rsObj.EoF) return null;

            var now           = DateTime.UtcNow;
            var pickDateRaw   = rsObj.Fields.Item("PickDate").Value;
            var createDateRaw = rsObj.Fields.Item("CreateDate").Value;
            var updateDateRaw = rsObj.Fields.Item("UpdateDate").Value;
            return new SapReplitAPI.Models.Cache.CachedPickList
            {
                AbsEntry     = Convert.ToInt32(rsObj.Fields.Item("AbsEntry").Value),
                Name         = rsObj.Fields.Item("Name").Value?.ToString() ?? string.Empty,
                OwnerCode    = Convert.ToInt32(rsObj.Fields.Item("OwnerCode").Value),
                OwnerName    = rsObj.Fields.Item("OwnerName").Value?.ToString() ?? string.Empty,
                Status       = rsObj.Fields.Item("Status").Value?.ToString() ?? string.Empty,
                Canceled     = rsObj.Fields.Item("Canceled").Value?.ToString() ?? "N",
                Remarks      = rsObj.Fields.Item("Remarks").Value?.ToString() ?? string.Empty,
                PickDate     = pickDateRaw   is DBNull or null ? DateTime.MinValue : Convert.ToDateTime(pickDateRaw),
                CreateDate   = createDateRaw is DBNull or null ? DateTime.MinValue : Convert.ToDateTime(createDateRaw),
                UpdateDate   = updateDateRaw is DBNull or null ? DateTime.MinValue : Convert.ToDateTime(updateDateRaw),
                U_ReplitId   = rsObj.Fields.Item("U_ReplitId").Value?.ToString(),
                SlpCode          = rsObj.Fields.Item("SlpCode").Value is DBNull or null
                                   ? null : (int?)Convert.ToInt32(rsObj.Fields.Item("SlpCode").Value),
                SlpName          = rsObj.Fields.Item("SlpName").Value?.ToString() ?? string.Empty,
                LastSyncedAt     = now,
                ZoneRef          = rsObj.Fields.Item("ZoneRef").Value is DBNull or null
                                   ? null : rsObj.Fields.Item("ZoneRef").Value?.ToString(),
                DeliveryLocation = rsObj.Fields.Item("DeliveryLocation").Value is DBNull or null
                                   ? null : rsObj.Fields.Item("DeliveryLocation").Value?.ToString()
            };
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Read PKL1 lines for the given OPKL AbsEntries, joined with RDR1 and ORDR for SO details.
    /// </summary>
    public List<SapReplitAPI.Models.Cache.CachedPickListLine> GetPickListLines(IEnumerable<int> absEntries)
    {
        var entries = absEntries.ToList();
        if (entries.Count == 0) return new();

        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            string inClause = string.Join(",", entries);
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT P.AbsEntry, P.PickEntry, P.OrderEntry, P.OrderLine,
       P.BaseObject, P.RelQtty, P.PickQtty, P.PickStatus, P.PrevReleas,
       ISNULL(R.ItemCode, '')    AS ItemCode,
       ISNULL(R.Dscription, '') AS Dscription,
       ISNULL(R.WhsCode, '')    AS WhsCode,
       O.DocNum                 AS SourceSoDocNum,
       O.U_ZoneRef              AS ZoneRef,
       O.U_DeliveryLocation     AS DeliveryLocation,
       O.U_ReplitId             AS U_ReplitId
FROM PKL1 P
LEFT JOIN RDR1 R ON R.DocEntry = P.OrderEntry AND R.LineNum = P.OrderLine
LEFT JOIN ORDR O ON O.DocEntry = P.OrderEntry
WHERE P.AbsEntry IN ({inClause})
ORDER BY P.AbsEntry, P.PickEntry");

            var list = new List<SapReplitAPI.Models.Cache.CachedPickListLine>();
            while (!rs.EoF)
            {
                var docNumRaw = rs.Fields.Item("SourceSoDocNum").Value;
                list.Add(new SapReplitAPI.Models.Cache.CachedPickListLine
                {
                    AbsEntry       = Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                    PickEntry      = Convert.ToInt32(rs.Fields.Item("PickEntry").Value),
                    OrderEntry     = Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                    OrderLine      = Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                    BaseObject     = Convert.ToInt32(rs.Fields.Item("BaseObject").Value),
                    RelQtty        = Convert.ToDecimal(rs.Fields.Item("RelQtty").Value),
                    PickQtty       = Convert.ToDecimal(rs.Fields.Item("PickQtty").Value),
                    PickStatus     = rs.Fields.Item("PickStatus").Value?.ToString() ?? string.Empty,
                    PrevReleas     = Convert.ToDecimal(rs.Fields.Item("PrevReleas").Value),
                    ItemCode       = rs.Fields.Item("ItemCode").Value?.ToString() ?? string.Empty,
                    Dscription     = rs.Fields.Item("Dscription").Value?.ToString() ?? string.Empty,
                    WhsCode         = rs.Fields.Item("WhsCode").Value?.ToString() ?? string.Empty,
                    SourceSoDocNum  = docNumRaw is DBNull or null ? null : (int?)Convert.ToInt32(docNumRaw),
                    ZoneRef         = rs.Fields.Item("ZoneRef").Value is DBNull or null
                                      ? null : rs.Fields.Item("ZoneRef").Value?.ToString(),
                    DeliveryLocation= rs.Fields.Item("DeliveryLocation").Value is DBNull or null
                                      ? null : rs.Fields.Item("DeliveryLocation").Value?.ToString(),
                    U_ReplitId      = rs.Fields.Item("U_ReplitId").Value is DBNull or null
                                      ? null : rs.Fields.Item("U_ReplitId").Value?.ToString()
                });
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Read PKL2 bin allocations for the given OPKL AbsEntries, joined with PKL1 (for OrderEntry/OrderLine)
    /// and OBIN (for BinCode/WhsCode).
    /// </summary>
    public List<SapReplitAPI.Models.Cache.CachedPickListBinAllocation> GetPickListBinAllocations(IEnumerable<int> absEntries)
    {
        var entries = absEntries.ToList();
        if (entries.Count == 0) return new();

        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            string inClause = string.Join(",", entries);
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT P2.AbsEntry, P2.Pkl2LinNum, P2.PickEntry,
       ISNULL(P1.OrderEntry, 0) AS OrderEntry,
       ISNULL(P1.OrderLine, 0)  AS OrderLine,
       ISNULL(P2.ItemCode, '')  AS ItemCode,
       ISNULL(B.WhsCode, '')    AS WhsCode,
       P2.BinAbs                AS BinAbsEntry,
       ISNULL(B.BinCode, '')    AS BinCode,
       P2.PickQtty, 0.0 AS RelQtty, 0.0 AS OpenCreQty,
       ISNULL(OP.Name, '')      AS PickListName,
       ISNULL(OP.Status, '')    AS PickListStatus,
       O.SlpCode,
       ISNULL(S.SlpName, '')    AS SlpName,
       O.U_ZoneRef              AS ZoneRef,
       O.U_DeliveryLocation     AS DeliveryLocation,
       O.U_ReplitId             AS U_ReplitId
FROM PKL2 P2
LEFT JOIN PKL1 P1 ON P1.AbsEntry = P2.AbsEntry AND P1.PickEntry = P2.PickEntry
LEFT JOIN OBIN B   ON B.AbsEntry  = P2.BinAbs
LEFT JOIN OPKL OP  ON OP.AbsEntry = P2.AbsEntry
LEFT JOIN ORDR O   ON O.DocEntry  = P1.OrderEntry
LEFT JOIN OSLP S   ON S.SlpCode   = O.SlpCode
WHERE P2.AbsEntry IN ({inClause})
ORDER BY P2.AbsEntry, P2.PickEntry, P2.Pkl2LinNum");

            var list = new List<SapReplitAPI.Models.Cache.CachedPickListBinAllocation>();
            while (!rs.EoF)
            {
                list.Add(new SapReplitAPI.Models.Cache.CachedPickListBinAllocation
                {
                    AbsEntry    = Convert.ToInt32(rs.Fields.Item("AbsEntry").Value),
                    Pkl2LinNum  = Convert.ToInt32(rs.Fields.Item("Pkl2LinNum").Value),
                    PickEntry   = Convert.ToInt32(rs.Fields.Item("PickEntry").Value),
                    OrderEntry  = Convert.ToInt32(rs.Fields.Item("OrderEntry").Value),
                    OrderLine   = Convert.ToInt32(rs.Fields.Item("OrderLine").Value),
                    ItemCode    = rs.Fields.Item("ItemCode").Value?.ToString() ?? string.Empty,
                    WhsCode     = rs.Fields.Item("WhsCode").Value?.ToString() ?? string.Empty,
                    BinAbsEntry = Convert.ToInt32(rs.Fields.Item("BinAbsEntry").Value),
                    BinCode     = rs.Fields.Item("BinCode").Value?.ToString() ?? string.Empty,
                    PickQtty      = Convert.ToDecimal(rs.Fields.Item("PickQtty").Value),
                    RelQtty       = Convert.ToDecimal(rs.Fields.Item("RelQtty").Value),
                    OpenCreQty    = Convert.ToDecimal(rs.Fields.Item("OpenCreQty").Value),
                    PickListName  = rs.Fields.Item("PickListName").Value?.ToString() ?? string.Empty,
                    PickListStatus= rs.Fields.Item("PickListStatus").Value?.ToString() ?? string.Empty,
                    SlpCode          = rs.Fields.Item("SlpCode").Value is DBNull or null
                                       ? null : (int?)Convert.ToInt32(rs.Fields.Item("SlpCode").Value),
                    SlpName          = rs.Fields.Item("SlpName").Value?.ToString() ?? string.Empty,
                    ZoneRef          = rs.Fields.Item("ZoneRef").Value is DBNull or null
                                       ? null : rs.Fields.Item("ZoneRef").Value?.ToString(),
                    DeliveryLocation = rs.Fields.Item("DeliveryLocation").Value is DBNull or null
                                       ? null : rs.Fields.Item("DeliveryLocation").Value?.ToString(),
                    U_ReplitId       = rs.Fields.Item("U_ReplitId").Value is DBNull or null
                                       ? null : rs.Fields.Item("U_ReplitId").Value?.ToString()
                });
                rs.MoveNext();
            }
            return list;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>Read raw OPKL + PKL1 + PKL2 row counts for cache verification.</summary>
    public (int OpklCount, int Pkl1Count, int Pkl2Count) GetPickListCounts()
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery("SELECT COUNT(*) AS C FROM OPKL");
            int opkl = rs.EoF ? 0 : Convert.ToInt32(rs.Fields.Item("C").Value);
            rs.DoQuery("SELECT COUNT(*) AS C FROM PKL1");
            int pkl1 = rs.EoF ? 0 : Convert.ToInt32(rs.Fields.Item("C").Value);
            rs.DoQuery("SELECT COUNT(*) AS C FROM PKL2");
            int pkl2 = rs.EoF ? 0 : Convert.ToInt32(rs.Fields.Item("C").Value);
            return (opkl, pkl1, pkl2);
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Read OUSR row for the given USERID (integer).
    /// Returns null if the user does not exist in SAP.
    /// Read-only — no DI API call, pure SQL Recordset.
    /// </summary>
    public SapUserRecord? GetPickerSapUser(int sapUserId)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT USERID, USER_CODE, U_NAME, LOCKED FROM OUSR WHERE USERID = {sapUserId}");
            if (rs.EoF) return null;
            return new SapUserRecord
            {
                UserId   = Convert.ToInt32(rs.Fields.Item("USERID").Value),
                UserCode = rs.Fields.Item("USER_CODE").Value?.ToString() ?? string.Empty,
                UserName = rs.Fields.Item("U_NAME").Value?.ToString() ?? string.Empty,
                Locked   = rs.Fields.Item("LOCKED").Value?.ToString() ?? "Y"
            };
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    // ── Offline Fulfillment V2 SAP methods ────────────────────────────────────
    // These methods mirror ZF patterns but use U_ZoneRef="OfflineFulfillment".
    // All are guarded by OfflineFulfillmentOptions.Enabled=false at the service layer.

    /// <summary>Public wrapper for ReadRdr1ForZf. Returns RDR1 lines for an ORDR.</summary>
    public List<SapReplitAPI.Models.ZoneFulfillment.Rdr1Line> ReadRdr1Lines(int docEntry)
    {
        var company = GetConnectedCompany();
        return ReadRdr1ForZf(company, docEntry);
    }

    /// <summary>
    /// Returns all PKL1.OrderLine values for the given OPKL (absEntry) that belong to the given ORDR.
    /// Used for OPKL completeness verification: expected WHS-group line set vs. actual SAP PKL1 lines.
    /// </summary>
    public List<int> GetPickListLineNums(int absEntry, int soDocEntry)
    {
        _ = GetConnectedCompany();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)_company!.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT P.OrderLine
                FROM   PKL1 P
                WHERE  P.AbsEntry   = {absEntry}
                  AND  P.OrderEntry = {soDocEntry}");
            var result = new List<int>();
            while (!rs.EoF)
            {
                result.Add(Convert.ToInt32(rs.Fields.Item("OrderLine").Value));
                rs.MoveNext();
            }
            return result;
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Creates an ORDR for Offline Fulfillment V2 recovery.
    /// Sets U_ZoneRef="OfflineFulfillment". One line per (ItemCode, WhsCode) group.
    /// Returns (0, 0, error) on SAP rejection; (DocEntry, DocNum, null) on success.
    /// </summary>
    public (int DocEntry, int? DocNum, string? Error) CreateOfflineRecoveryOrder(
        string   cardCode,
        DateTime docDate,
        DateTime deliveryDate,
        int?     slpCode,
        string   docCurrency,
        string   uReplitId,
        string   deliveryLocation,
        IReadOnlyList<(string ItemCode, string WhsCode, decimal Qty, decimal UnitPrice, string? Description, string? U_ItemName, string? U_Manufacturer)> lines)
    {
        if (lines.Count == 0)
            return (0, 0, "No lines to create ORDR.");

        var company = GetConnectedCompany();
        Documents order = null;
        try
        {
            order = (Documents)company.GetBusinessObject(BoObjectTypes.oOrders);
            order.CardCode                = cardCode;
            order.DocDate                 = docDate;
            order.TaxDate                 = docDate;
            order.DocDueDate              = deliveryDate;
            order.DocCurrency             = docCurrency;
            order.Series                  = 8;
            order.BPL_IDAssignedToInvoice = 1;
            if (slpCode.HasValue)
                order.SalesPersonCode = slpCode.Value;

            order.UserFields.Fields.Item("U_ZoneRef").Value          = "OfflineFulfillment";
            order.UserFields.Fields.Item("U_DeliveryLocation").Value = deliveryLocation;
            order.UserFields.Fields.Item("U_ReplitId").Value         = uReplitId;

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) order.Lines.Add();
                var line = lines[i];
                order.Lines.ItemCode        = line.ItemCode;
                order.Lines.Quantity        = (double)line.Qty;
                order.Lines.Price           = (double)line.UnitPrice;
                order.Lines.VatGroup        = "TZ";
                order.Lines.WarehouseCode   = line.WhsCode;
                order.Lines.ItemDescription = line.Description ?? line.ItemCode;
                if (!string.IsNullOrWhiteSpace(line.U_ItemName))
                    order.Lines.UserFields.Fields.Item("U_ItemName").Value = line.U_ItemName;
                if (!string.IsNullOrWhiteSpace(line.U_Manufacturer))
                    order.Lines.UserFields.Fields.Item("U_Manufacturer").Value = line.U_Manufacturer;
            }

            int rc = order.Add();
            if (rc != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogError("[OF-V2-SAP] ORDR.Add() failed [{Code}]: {Msg} uReplitId={Rid}", errCode, errMsg, uReplitId);
                return (0, 0, $"{errCode} - {errMsg}");
            }

            int docEntry = int.Parse(company.GetNewObjectKey());
            int? docNum  = GetDocNumForZf(company, docEntry);
            _logger.LogInformation("[OF-V2-SAP] ORDR.Add() SUCCESS DocEntry={DocEntry} DocNum={DocNum} uReplitId={Rid} lines={N}",
                docEntry, docNum, uReplitId, lines.Count);
            return (docEntry, docNum, null);
        }
        finally
        {
            if (order != null) Marshal.ReleaseComObject(order);
        }
    }

    /// <summary>
    /// Checks if an active ODLN with U_ZoneRef='OfflineFulfillment' exists for this offline order.
    /// Returns (DocEntry, DocNum) if found, null otherwise.
    /// </summary>
    public (int DocEntry, int DocNum)? FindOfflineRecoveryDelivery(string uReplitId, int soDocEntry)
    {
        var company = GetConnectedCompany();
        Recordset rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
                SELECT DISTINCT T0.DocEntry, T0.DocNum
                FROM   ODLN T0
                JOIN   DLN1 T1 ON T1.DocEntry = T0.DocEntry
                WHERE  T0.CANCELED   = N'N'
                  AND  T0.U_ZoneRef  = N'OfflineFulfillment'
                  AND  T0.U_ReplitId = N'{uReplitId.Replace("'", "''")}'
                  AND  T1.BaseType   = 17
                  AND  T1.BaseEntry  = {soDocEntry}");
            if (rs.EoF) return null;
            return (Convert.ToInt32(rs.Fields.Item("DocEntry").Value),
                    Convert.ToInt32(rs.Fields.Item("DocNum").Value));
        }
        finally
        {
            if (rs != null) Marshal.ReleaseComObject(rs);
        }
    }

    /// <summary>
    /// Creates an ODLN for Offline Fulfillment V2 recovery.
    /// Uses U_ZoneRef="OfflineFulfillment". Durable bins sourced from PKL2 (post-pick-replay).
    /// </summary>
    public (int Rc, int DocEntry, int DocNum, string? SapError) CreateOfflineRecoveryDelivery(
        string   cardCode,
        DateTime deliveryDate,
        string   uReplitId,
        string   deliveryLocation,
        IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.DeliveryLineSpec> lines)
    {
        if (lines.Count == 0)
            return (1, 0, 0, "No delivery lines.");

        var company  = GetConnectedCompany();
        Documents delivery = null;
        Recordset rs = null;
        try
        {
            delivery = (Documents)company.GetBusinessObject(BoObjectTypes.oDeliveryNotes);
            delivery.CardCode                = cardCode;
            delivery.DocDate                 = deliveryDate;
            delivery.TaxDate                 = deliveryDate;
            delivery.DocDueDate              = deliveryDate;
            delivery.DocCurrency             = "TZS";
            delivery.BPL_IDAssignedToInvoice = 1;

            delivery.UserFields.Fields.Item("U_ZoneRef").Value          = "OfflineFulfillment";
            delivery.UserFields.Fields.Item("U_DeliveryLocation").Value = deliveryLocation;
            delivery.UserFields.Fields.Item("U_ReplitId").Value         = uReplitId;

            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                if (lineIdx > 0) delivery.Lines.Add();
                var spec = lines[lineIdx];
                delivery.Lines.BaseType      = 17;   // ORDR
                delivery.Lines.BaseEntry     = spec.SoDocEntry;
                delivery.Lines.BaseLine      = spec.SoLineNum;
                delivery.Lines.Quantity      = (double)spec.Qty;
                delivery.Lines.WarehouseCode = spec.WhsCode;

                var bins = spec.DurableBins;
                for (int b = 0; b < bins.Count; b++)
                {
                    if (b > 0) delivery.Lines.BinAllocations.Add();
                    delivery.Lines.BinAllocations.BinAbsEntry   = bins[b].BinAbsEntry;
                    delivery.Lines.BinAllocations.Quantity       = (double)bins[b].Qty;
                    delivery.Lines.BinAllocations.BaseLineNumber = lineIdx;
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rc = delivery.Add();
            sw.Stop();

            if (rc != 0)
            {
                company.GetLastError(out int errCode, out string errMsg);
                _logger.LogError("[OF-V2-SAP] ODLN.Add() failed [{Code}]: {Msg} elapsed={Ms}ms", errCode, errMsg, sw.ElapsedMilliseconds);
                return (rc, 0, 0, $"{errCode} - {errMsg}");
            }

            int docEntry = int.Parse(company.GetNewObjectKey());
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM ODLN WHERE DocEntry = {docEntry}");
            int docNum = rs.EoF ? 0 : Convert.ToInt32(rs.Fields.Item("DocNum").Value);
            _logger.LogInformation("[OF-V2-SAP] ODLN.Add() SUCCESS DocEntry={DocEntry} DocNum={DocNum} elapsed={Ms}ms",
                docEntry, docNum, sw.ElapsedMilliseconds);
            return (0, docEntry, docNum, null);
        }
        finally
        {
            if (rs       != null) Marshal.ReleaseComObject(rs);
            if (delivery != null) Marshal.ReleaseComObject(delivery);
        }
    }

    /// <summary>
    /// Creates an OINV from an ODLN for Offline Fulfillment V2 recovery.
    /// Uses U_ZoneRef="OfflineFulfillment". Reads DLN1 lines internally.
    /// Throws on SAP error.
    /// </summary>
    public (int DocEntry, int DocNum) CreateOfflineRecoveryInvoice(
        int      deliveryDocEntry,
        string   cardCode,
        DateTime docDate,
        DateTime docDueDate,
        string   docCurrency,
        int?     slpCode,
        string   uReplitId,
        string   deliveryLocation)
    {
        var dlnLines = ReadDln1ByDocEntry(deliveryDocEntry);
        if (dlnLines.Count == 0)
            throw new InvalidOperationException($"No active DLN1 lines for ODLN {deliveryDocEntry}.");

        var company = GetConnectedCompany();
        Documents invoice = null;
        Recordset rs = null;
        try
        {
            invoice = (Documents)company.GetBusinessObject(BoObjectTypes.oInvoices);
            invoice.CardCode                = cardCode;
            invoice.DocDate                 = docDate;
            invoice.DocDueDate              = docDueDate;
            invoice.DocCurrency             = docCurrency;
            invoice.BPL_IDAssignedToInvoice = 1;
            if (slpCode.HasValue && slpCode.Value > 0)
                invoice.SalesPersonCode = slpCode.Value;

            invoice.UserFields.Fields.Item("U_ZoneRef").Value          = "OfflineFulfillment";
            invoice.UserFields.Fields.Item("U_ReplitId").Value         = uReplitId;
            invoice.UserFields.Fields.Item("U_DeliveryLocation").Value = deliveryLocation;

            for (int i = 0; i < dlnLines.Count; i++)
            {
                if (i > 0) invoice.Lines.Add();
                invoice.Lines.BaseType  = 15;   // ODLN
                invoice.Lines.BaseEntry = deliveryDocEntry;
                invoice.Lines.BaseLine  = dlnLines[i].DlnLineNum;
            }

            int rc = invoice.Add();
            if (rc != 0)
            {
                string err = company.GetLastErrorDescription();
                throw new InvalidOperationException($"OINV.Add() failed for ODLN {deliveryDocEntry}: rc={rc} — {err}");
            }

            int newDocEntry = int.Parse(company.GetNewObjectKey());
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($"SELECT DocNum FROM OINV WHERE DocEntry = {newDocEntry}");
            int docNum = !rs.EoF ? Convert.ToInt32(rs.Fields.Item("DocNum").Value) : 0;
            _logger.LogInformation("[OF-V2-SAP] OINV.Add() SUCCESS DocEntry={DocEntry} DocNum={DocNum} from ODLN={DlnEntry}",
                newDocEntry, docNum, deliveryDocEntry);
            return (newDocEntry, docNum);
        }
        finally
        {
            if (rs      != null) Marshal.ReleaseComObject(rs);
            if (invoice != null) Marshal.ReleaseComObject(invoice);
        }
    }

}

// ─── Delivery-gate diagnostic DTOs ─────────────────────────────────────────
public sealed class ZfDeliveryGateDiagnostics
{
    public List<ZfUdfColumnInfo>      OdlnUdfColumns       { get; } = new();
    public List<string>               PklTableNames         { get; } = new();
    public List<ZfTableColumnInfo>    PklColumnDetails      { get; } = new();
    public List<ZfPkl1Row>            Pkl1Rows              { get; } = new();
    public List<ZfOibqRow>            OibqRows              { get; } = new();
    public string?                    Rdr1Dscription        { get; set; }
    public decimal                    Rdr1OpenQty           { get; set; }
    public List<ZfMultiWhsDelivery>   MultiWhsDeliveries    { get; } = new();
    public bool                       DiApiPickListLoaded   { get; set; }
    public int                        DiApiPickBinCount     { get; set; }
    public List<ZfDiApiBinRow>        DiApiPickBinRows      { get; } = new();
    public string?                    DiApiPickListError    { get; set; }
    public List<ZfPkl2Row>            Pkl2OwnRows           { get; } = new();
    public List<ZfPkl2Row>            Pkl2BinConflicts      { get; } = new();
    public List<string>               BinCommitTables       { get; set; } = new();
    public List<string>               AllOpklsForItem       { get; set; } = new();
    // §1 OBBQ live truth
    public List<string>               ObbqSchema            { get; set; } = new();
    public List<string>               ObbqRows              { get; set; } = new();
    // §2 OPKL 6/7 attribution
    public List<string>               Opkl6And7Attribution  { get; set; } = new();
}
public sealed record ZfUdfColumnInfo(string Name, string SqlType, bool IsNullable, int MaxLength);
public sealed record ZfTableColumnInfo(string Table, string Column, string SqlType, int MaxLength);
public sealed record ZfPkl1Row(int AbsEntry, int PickEntry, int OrderEntry, int OrderLine, decimal PickQtty, decimal RelQtty, string PickStatus);
public sealed record ZfOibqRow(int BinAbsEntry, string BinCode, decimal OnHandQty);
public sealed record ZfMultiWhsDelivery(int DocEntry, int WhsCount, string WhsCodes);
public sealed record ZfDiApiBinRow(int BinAbsEntry, double Quantity, string SerialNumber);
public sealed record ZfPkl2Row(int AbsEntry, string OpklStatus, int BinAbs, string BinCode, decimal PickQtty);
