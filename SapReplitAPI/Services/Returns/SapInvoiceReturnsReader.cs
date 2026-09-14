using System.Runtime.InteropServices;
using SAPbobsCOM;
using SapReplitAPI.Models.Returns;

namespace SapReplitAPI.Services.Returns;

public static class SapInvoiceReturnsReader
{
    public static List<InvoiceReturnsDto> Read(Company company, string cardCode, string status)
    {
        var invoices = new Dictionary<int, InvoiceReturnsDto>();
        Recordset? rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            // DI API Recordset has no parameter API. Escape the only text literal; status is validated.
            rs.DoQuery(InvoiceReturnsSql.Quantities +
                $" WHERE H.CardCode = N'{cardCode.Replace("'", "''")}' ORDER BY H.DocEntry, L.LineNum");
            while (!rs.EoF)
            {
                int Int(string name) => Convert.ToInt32(rs.Fields.Item(name).Value);
                decimal Dec(string name) => Convert.ToDecimal(rs.Fields.Item(name).Value);
                string Str(string name) => Convert.ToString(rs.Fields.Item(name).Value)?.Trim() ?? string.Empty;
                var docEntry = Int("DocEntry");
                if (!invoices.TryGetValue(docEntry, out var invoice))
                {
                    invoice = new InvoiceReturnsDto
                    {
                        DocEntry = docEntry, DocNum = Int("DocNum"),
                        DocDate = Convert.ToDateTime(rs.Fields.Item("DocDate").Value),
                        CardCode = Str("CardCode"), CardName = Str("CardName"),
                        DocStatus = Str("DocStatus"), Canceled = Str("CANCELED"),
                        DocTotal = Dec("DocTotal"), PaidToDate = Dec("PaidToDate"), BalanceDue = Dec("BalanceDue")
                    };
                    invoices.Add(docEntry, invoice);
                }
                invoice.Lines.Add(new InvoiceReturnsLineDto
                {
                    InvoiceDocEntry = docEntry, InvoiceLineNum = Int("LineNum"),
                    ItemCode = Str("ItemCode"), Description = Str("Dscription"),
                    InvoicedQty = Dec("Quantity"), ReturnedQty = Dec("ReturnedQty"),
                    PendingReturnQty = Dec("PendingQty"), UnitPrice = Dec("Price"), WhsCode = Str("WhsCode")
                });
                rs.MoveNext();
            }
        }
        finally { if (rs != null) Marshal.ReleaseComObject(rs); }

        // "open" means available for a new return claim, including paid/closed invoices.
        return invoices.Values.Where(i => status == "all" ||
            (i.Canceled == "N" && i.Lines.Any(l => l.ReturnableQty > 0))).ToList();
    }

    public static List<int> CreditMemoIds(Company company)
    {
        Recordset? rs = null;
        try
        {
            rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery("SELECT DocEntry FROM ORIN ORDER BY DocEntry");
            var ids = new List<int>();
            while (!rs.EoF) { ids.Add(Convert.ToInt32(rs.Fields.Item("DocEntry").Value)); rs.MoveNext(); }
            return ids;
        }
        finally { if (rs != null) Marshal.ReleaseComObject(rs); }
    }
}
