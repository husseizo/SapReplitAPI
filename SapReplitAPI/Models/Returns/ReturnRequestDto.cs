using System.Collections.Generic;

namespace SapReplitAPI.Models.Returns;

/// <summary>
/// Data transfer object for ORRR (Return Request) + RRR1 (Return Request Lines).
/// Maps SAP ORRR header fields and RRR1 line details.
/// </summary>
public class ReturnRequestDto
{
    public int DocEntry { get; set; }        // ORRR.DocEntry
    public int DocNum { get; set; }          // ORRR.DocNum
    public string? CardCode { get; set; }    // ORRR.CardCode (customer)
    public string? CardName { get; set; }    // ORRR.CardName
    public DateTime DocDate { get; set; }    // ORRR.DocDate
    public string? DocStatus { get; set; }   // O=Open, C=Closed
    public string? Canceled { get; set; }    // Y/N — cancellation flag
    public decimal DocTotal { get; set; }    // ORRR.DocTotal (informational)
    public string? Comments { get; set; }    // ORRR.Comments
    public string? U_AppRef { get; set; }    // User-defined field — app reference
    public string? U_ReplitId { get; set; }  // User-defined field — Replit ID
    public List<ReturnRequestLineDto> Lines { get; set; } = new();
}

/// <summary>
/// RRR1 (Return Request Line) data.
/// </summary>
public class ReturnRequestLineDto
{
    public int LineNum { get; set; }       // RRR1.LineNum — line index
    public int BaseType { get; set; }      // 13=OINV (invoice), 15=ODLN (delivery)
    public int BaseEntry { get; set; }     // OINV.DocEntry or ODLN.DocEntry
    public int BaseLine { get; set; }      // INV1.LineNum or DLN1.LineNum
    public string? ItemCode { get; set; }  // RRR1.ItemCode
    public string? Dscription { get; set; }
    public decimal Quantity { get; set; }  // RRR1.Quantity — total qty being returned
    public decimal OpenQty { get; set; }   // RRR1.OpenQty — qty not yet credited
    public string? WhsCode { get; set; }   // RRR1.WhsCode — warehouse
    public string? LineStatus { get; set; } // O=Open, C=Closed
}

/// <summary>
/// Request to create a new return request.
/// </summary>
public class CreateReturnRequestDto
{
    public int InvoiceDocEntry { get; set; }  // OINV.DocEntry
    public string? AppRef { get; set; }       // Idempotency key
    public string? Comments { get; set; }
    public List<CreateReturnRequestLineDto> Lines { get; set; } = new();
}

public class CreateReturnRequestLineDto
{
    public int LineNum { get; set; }    // INV1.LineNum
    public decimal Quantity { get; set; }
    public string? Reason { get; set; }
}
