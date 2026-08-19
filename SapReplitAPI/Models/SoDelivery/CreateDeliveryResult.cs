namespace SapReplitAPI.Models.SoDelivery;

public enum DeliveryFailureType
{
    None,
    NoOpenLines,
    InsufficientStock,
    SapError,
}

/// <summary>One bin allocation entry for a single SO line: which bin code and how many units.</summary>
public sealed record LineBinAlloc(string BinCode, decimal Qty);

public class CreateDeliveryResult
{
    public bool    Success          { get; set; }
    public int?    DeliveryDocEntry { get; set; }
    public int?    DeliveryDocNum   { get; set; }
    public string? SapErrorCode     { get; set; }
    public string? SapErrorMessage  { get; set; }
    public DeliveryFailureType FailureType { get; set; } = DeliveryFailureType.None;

    /// <summary>
    /// Bin allocations keyed by SO line number (RDR1.LineNum).
    /// Only populated on success when the warehouse has bin management enabled.
    /// Lines with no bin requirement are absent from this dict.
    /// </summary>
    public Dictionary<int, List<LineBinAlloc>> LineBinAllocations { get; set; } = new();
}
