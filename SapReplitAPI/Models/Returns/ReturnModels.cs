namespace SapReplitAPI.Models.Returns;

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed class CreateReturnDto
{
    public int    ReturnRequestDocEntry { get; init; }
    public string AppRef                { get; init; } = string.Empty;
    public string? Comments             { get; init; }
    public IReadOnlyList<ReturnLineInput> Lines { get; init; } = [];
}

public sealed class ReturnLineInput
{
    public int     RrLineNum { get; init; }    // RRR1 line number to fulfill
    public decimal Quantity  { get; init; }
    public string  WhsCode   { get; init; } = string.Empty;
    public int?    BinAbs    { get; init; }    // OBIN.AbsEntry — optional bin override
}

// ── Responses ─────────────────────────────────────────────────────────────────

public sealed record ReturnResponseDto
{
    public int     DocEntry  { get; init; }
    public int     DocNum    { get; init; }
    public string  CardCode  { get; init; } = string.Empty;
    public string  CardName  { get; init; } = string.Empty;
    public string  Status    { get; init; } = string.Empty;
    public string? AppRef    { get; init; }
    public string? Comments  { get; init; }
    public string  DocDate   { get; init; } = string.Empty;
    public decimal DocTotal  { get; init; }
    public IReadOnlyList<ReturnLineDto> Lines { get; init; } = [];
}

public sealed class ReturnLineDto
{
    public int     LineNum    { get; init; }
    public string  ItemCode   { get; init; } = string.Empty;
    public string  Dscription { get; init; } = string.Empty;
    public decimal Quantity   { get; init; }
    public string  WhsCode    { get; init; } = string.Empty;
    public int     BaseType   { get; init; }
    public int     BaseEntry  { get; init; }
    public int     BaseLine   { get; init; }
}

public sealed class ListReturnsQuery
{
    public string? CardCode { get; init; }
    public int     Limit    { get; init; } = 50;
}
