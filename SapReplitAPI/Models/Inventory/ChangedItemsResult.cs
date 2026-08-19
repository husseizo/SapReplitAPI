namespace SapReplitAPI.Models.Inventory;

/// <summary>
/// Per-source item-code counts returned by GetChangedItemCodes,
/// used for structured delta-detection logging.
/// </summary>
public record ChangedItemsResult(
    HashSet<string> ItemCodes,
    int OinmCount,
    int OrdrCount,
    int OitmCount
)
{
    public int TotalDistinct => ItemCodes.Count;
}
