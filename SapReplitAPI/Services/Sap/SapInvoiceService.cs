using SAPbobsCOM;
using SapReplitAPI.DTOs.Dashboard;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Services;
using System.Runtime.InteropServices;

public class SapInvoiceService
{
    private readonly ILogger<SapInvoiceService> _logger;
    private readonly InvoiceLifecycleStatusService _invoiceLifecycleStatusService;

    public SapInvoiceService(
        ILogger<SapInvoiceService> logger,
        InvoiceLifecycleStatusService invoiceLifecycleStatusService)
    {
        _logger = logger;
        _invoiceLifecycleStatusService = invoiceLifecycleStatusService;
    }

    public async Task<List<DetailedInvoiceReportRow>> GetDetailedInvoiceStatusReportAsync(
        SAPbobsCOM.Company company,
        string salesName,
        DateTime docDate)
    {
        return await Task.Run(() =>
        {
            var result = new List<DetailedInvoiceReportRow>();
            SAPbobsCOM.Recordset? rs = null;

            var docDateFormatted = docDate.ToString("yyyy-MM-dd");
            var safeSalesName = salesName.Replace("'", "''");

            const string excludedCardCode = "CUS000625";
            var headerRows = new List<InvoiceReportHeaderRow>();

            var query = $@"
SELECT
    T0.DocEntry,
    T0.DocNum,
    T0.DocDate,
    T0.CardName,
    T0.DocTotal,
    ISNULL(T0.U_ReinvoicedFrom, '') AS ReinvoicedFrom,
    T1.SlpCode,
    T1.SlpName
FROM OINV T0
LEFT JOIN OSLP T1 ON T0.SlpCode = T1.SlpCode
WHERE
    T1.SlpName = '{safeSalesName}' AND
    CAST(T0.DocDate AS DATE) = '{docDateFormatted}' AND
    T0.CardCode <> '{excludedCardCode}'
ORDER BY T0.DocEntry";

            try
            {
                _logger.LogInformation(
                    "Fetching canonical invoice status report for {SalesName} on {DocDate}",
                    salesName,
                    docDate.Date);

                rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.BoRecordset);
                rs.DoQuery(query);

                while (!rs.EoF)
                {
                    headerRows.Add(new InvoiceReportHeaderRow
                    {
                        DocEntry = GetInt(rs, "DocEntry"),
                        DocNum = GetInt(rs, "DocNum"),
                        DocDate = GetNullableDateTime(rs, "DocDate"),
                        CardName = GetString(rs, "CardName"),
                        DocTotal = GetDecimal(rs, "DocTotal"),
                        ReinvoicedFrom = GetString(rs, "ReinvoicedFrom"),
                        SlpCode = GetInt(rs, "SlpCode"),
                        SlpName = GetString(rs, "SlpName")
                    });

                    rs.MoveNext();
                }

                if (headerRows.Count == 0)
                    return result;

                var lifecycleResults = _invoiceLifecycleStatusService.GetLifecycleStatusResults(
                    company,
                    headerRows.Select(x => x.DocEntry));

                foreach (var row in headerRows)
                {
                    if (!lifecycleResults.TryGetValue(row.DocEntry, out var lifecycle))
                    {
                        lifecycle = new InvoiceLifecycleStatusResult
                        {
                            DocEntry = row.DocEntry,
                            DocNum = row.DocNum,
                            Status = InvoiceLifecycleStatus.Unknown,
                            InvoiceStatus = "Unknown",
                            PaymentsStatus = "Unknown",
                            CancellationStatus = "Unknown"
                        };
                    }

                    result.Add(new DetailedInvoiceReportRow
                    {
                        SalesName = row.SlpName,
                        PostingDate = row.DocDate,
                        InvoiceNo = row.DocNum.ToString(),
                        ReinvoicedFrom = row.ReinvoicedFrom,
                        InvoiceStatus = lifecycle.InvoiceStatus,
                        PaidDate = lifecycle.LatestPaymentDate,
                        Customer = row.CardName,
                        CashSales = _invoiceLifecycleStatusService.IsCollectedStatus(lifecycle.Status) ? row.DocTotal : 0m,
                        CreditSales = _invoiceLifecycleStatusService.IsOutstandingStatus(lifecycle.Status) ? row.DocTotal : 0m,
                        ReturnedCashInvoice =
                            lifecycle.Status == InvoiceLifecycleStatus.CancellationDocument ||
                            lifecycle.Status == InvoiceLifecycleStatus.CanceledInvoice ||
                            lifecycle.Status == InvoiceLifecycleStatus.ReplacedOrReinvoiced
                                ? row.DocTotal
                                : 0m,
                        PaymentsStatus = lifecycle.PaymentsStatus,
                        CancellationStatus = lifecycle.CancellationStatus,
                        SlpCode = row.SlpCode
                    });
                }

                _logger.LogInformation(
                    "Retrieved {RowCount} canonical invoice status rows for {SalesName} on {DocDate}",
                    result.Count,
                    salesName,
                    docDate.Date);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed canonical invoice status report for {SalesName} on {DocDate}",
                    salesName,
                    docDate.Date);
                throw;
            }
            finally
            {
                if (rs != null)
                    Marshal.ReleaseComObject(rs);
            }
        });
    }

    private static string GetString(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? string.Empty
            : Convert.ToString(rs.Fields.Item(fieldName).Value)?.Trim() ?? string.Empty;
    }

    private static int GetInt(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? 0
            : Convert.ToInt32(rs.Fields.Item(fieldName).Value);
    }

    private static decimal GetDecimal(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? 0m
            : Convert.ToDecimal(rs.Fields.Item(fieldName).Value);
    }

    private static DateTime? GetNullableDateTime(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? null
            : Convert.ToDateTime(rs.Fields.Item(fieldName).Value);
    }

    private sealed class InvoiceReportHeaderRow
    {
        public int DocEntry { get; set; }
        public int DocNum { get; set; }
        public DateTime? DocDate { get; set; }
        public string CardName { get; set; } = string.Empty;
        public decimal DocTotal { get; set; }
        public string ReinvoicedFrom { get; set; } = string.Empty;
        public int SlpCode { get; set; }
        public string SlpName { get; set; } = string.Empty;
    }
}
