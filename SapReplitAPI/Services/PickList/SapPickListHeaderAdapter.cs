using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Production implementation of IPickListSapHeaderReader.
/// Delegates directly to SapService.GetPickListHeaderByAbsEntry.
/// </summary>
public sealed class SapPickListHeaderAdapter : IPickListSapHeaderReader
{
    private readonly SapService _sap;

    public SapPickListHeaderAdapter(SapService sap) => _sap = sap;

    public CachedPickList? GetPickListHeaderByAbsEntry(int absEntry)
        => _sap.GetPickListHeaderByAbsEntry(absEntry);
}
