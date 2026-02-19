#pragma warning disable CA1416 // Possible null argument

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SAPbobsCOM;

public class TodayOrderCacheService
{
    private readonly CacheDbContext _db;
    private readonly SapService _sap; // <-- inject SapService, not Company
    private readonly ILogger<TodayOrderCacheService> _logger;

    public TodayOrderCacheService(CacheDbContext db, SapService sap, ILogger<TodayOrderCacheService> logger)
    {
        _db = db;
        _sap = sap;
        _logger = logger;
    }

    public async Task RefreshTodayOrdersFromSAP()
    {
        Recordset? rs = null;
        Recordset? rsLines = null;

        try
        {
            var company = typeof(SapService)
                .GetMethod("GetConnectedCompany", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(_sap, Array.Empty<object>()) as Company;
            // ^ uses your existing private GetConnectedCompany() safely via reflection
            // If you prefer, expose GetConnectedCompany() as internal/public and call directly.

            var today = DateTime.Today;
            string todayStr = today.ToString("yyyy-MM-dd");

            _logger.LogInformation("🧹 Clearing previous today's orders from cache...");
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"TodayOrderLines\"");
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"TodayOrderHeaders\"");

            // STEP 1: Headers
            var headers = new List<CachedTodayOrder>();
            var docEntryList = new List<int>();

            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            string headerQuery = $@"
SELECT O.DocEntry, O.DocNum, O.CardName, O.DocDate, O.DocTotal, O.DocStatus, O.SlpCode, 
       O.CANCELED, S.SlpName
FROM ORDR O
LEFT JOIN OSLP S ON O.SlpCode = S.SlpCode
WHERE CAST(O.DocDate AS DATE) = '{todayStr}'";

            _logger.LogDebug("📄 Running header query:\n{Query}", headerQuery);
            rs.DoQuery(headerQuery);

            while (!rs.EoF)
            {
                int docEntry = Convert.ToInt32(rs.Fields.Item("DocEntry").Value);

                string docStatus = rs.Fields.Item("DocStatus").Value is DBNull
                    ? "O" : rs.Fields.Item("DocStatus").Value.ToString();

                // Handle N/Y/C and lower-case variants defensively
                string canceledRaw = rs.Fields.Item("CANCELED").Value is DBNull
                    ? "N" : rs.Fields.Item("CANCELED").Value.ToString();
                string canceled = (canceledRaw ?? "N").Trim().ToUpperInvariant();

                string status = (canceled == "Y")
                    ? "Cancelled"
                    : (string.Equals(docStatus, "C", StringComparison.OrdinalIgnoreCase) ? "Delivered" : "Open");

                headers.Add(new CachedTodayOrder
                {
                    DocEntry = docEntry,
                    DocNum = Convert.ToInt32(rs.Fields.Item("DocNum").Value),
                    CardName = rs.Fields.Item("CardName").Value is DBNull ? "" : rs.Fields.Item("CardName").Value.ToString(),
                    DocDate = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                    OrderValue = Convert.ToDecimal(rs.Fields.Item("DocTotal").Value),
                    Status = status,
                    SlpCode = rs.Fields.Item("SlpCode").Value is DBNull ? null : Convert.ToInt32(rs.Fields.Item("SlpCode").Value),
                    SlpName = rs.Fields.Item("SlpName").Value is DBNull ? "" : rs.Fields.Item("SlpName").Value.ToString(),
                    Cancelled = canceled == "Y"
                });

                docEntryList.Add(docEntry);
                rs.MoveNext();
            }

            await _db.TodayOrderHeaders.AddRangeAsync(headers);
            await _db.SaveChangesAsync();
            _logger.LogInformation("✅ Cached {Count} today's order headers.", headers.Count);

            // STEP 2: Lines
            if (docEntryList.Count > 0)
            {
                rsLines = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                string lineQuery = $@"
SELECT R.DocEntry, R.ItemCode, R.Dscription, R.Quantity, R.Price, R.WhsCode, R.DocDate,
       R.U_ItemName, R.U_Manufacturer
FROM RDR1 R
WHERE R.DocEntry IN ({string.Join(",", docEntryList)})";

                _logger.LogDebug("📄 Running line query:\n{Query}", lineQuery);
                rsLines.DoQuery(lineQuery);

                var lines = new List<CachedTodayOrderLine>();

                while (!rsLines.EoF)
                {
                    lines.Add(new CachedTodayOrderLine
                    {
                        DocEntry = Convert.ToInt32(rsLines.Fields.Item("DocEntry").Value),
                        ItemCode = rsLines.Fields.Item("ItemCode").Value is DBNull ? "" : rsLines.Fields.Item("ItemCode").Value.ToString(),
                        Dscription = rsLines.Fields.Item("Dscription").Value is DBNull ? "" : rsLines.Fields.Item("Dscription").Value.ToString(),
                        Quantity = Convert.ToDecimal(rsLines.Fields.Item("Quantity").Value),
                        Price = Convert.ToDecimal(rsLines.Fields.Item("Price").Value),
                        WhsCode = rsLines.Fields.Item("WhsCode").Value is DBNull ? "" : rsLines.Fields.Item("WhsCode").Value.ToString(),
                        U_ItemName = rsLines.Fields.Item("U_ItemName").Value is DBNull ? "" : rsLines.Fields.Item("U_ItemName").Value.ToString(),
                        U_Manufacturer = rsLines.Fields.Item("U_Manufacturer").Value is DBNull ? "" : rsLines.Fields.Item("U_Manufacturer").Value.ToString(),
                        DocDate = Convert.ToDateTime(rsLines.Fields.Item("DocDate").Value)
                    });

                    rsLines.MoveNext();
                }

                await _db.TodayOrderLines.AddRangeAsync(lines);
                await _db.SaveChangesAsync();
                _logger.LogInformation("✅ Cached {Count} today's order lines.", lines.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to refresh today's orders from SAP.");
        }
        finally
        {
            if (rs != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rs);
            if (rsLines != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rsLines);
        }
    }

    private string SafeToString(object val) => val is DBNull ? "" : val?.ToString() ?? "";
}