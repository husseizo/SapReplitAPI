namespace SapReplitAPI.Models.Returns;

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed class CreateReturnRequestDto
{
    public int    InvoiceDocEntry { get; init; }
    public string AppRef          { get; init; } = string.Empty;
    public string? Comments       { get; init; }
    public IReadOnlyList<ReturnRequestLineInput> Lines { get; init; } = [];
}

public sealed class ReturnRequestLineInput
{
    public int      LineNum  { get; init; }
    public decimal  Quantity { get; init; }
    public string?  Reason   { get; init; }
}

// ── Responses ─────────────────────────────────────────────────────────────────

public sealed record ReturnRequestDto
{
    public int     DocEntry  { get; init; }
    public int     DocNum    { get; init; }
    public string  CardCode  { get; init; } = string.Empty;
    public string  CardName  { get; init; } = string.Empty;
    public string  Status    { get; init; } = string.Empty;  // "O" | "C"
    public string? AppRef    { get; init; }
    public string? ReplitId  { get; init; }
    public string? Comments  { get; init; }
    public string  DocDate   { get; init; } = string.Empty;
    public decimal DocTotal  { get; init; }
    public bool    Canceled  { get; init; }
    public IReadOnlyList<ReturnRequestLineDto> Lines { get; init; } = [];
}

public sealed class ReturnRequestLineDto
{
    public int     LineNum    { get; init; }
    public string  ItemCode   { get; init; } = string.Empty;
    public string  Dscription { get; init; } = string.Empty;
    public decimal Quantity   { get; init; }
    public decimal OpenQty    { get; init; }
    public string  WhsCode    { get; init; } = string.Empty;
    public string  LineStatus { get; init; } = string.Empty;  // "O" | "C"
    public int     BaseType   { get; init; }
    public int     BaseEntry  { get; init; }
    public int     BaseLine   { get; init; }
}

public sealed class ListReturnRequestsQuery
{
    public string? CardCode { get; init; }
    public string? Status   { get; init; }   // "O" | "C"
    public int     Limit    { get; init; } = 50;
}
