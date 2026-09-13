using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Minimal SAP abstraction for reading a single OPKL header by AbsEntry.
/// Implemented by SapPickListHeaderAdapter (production) and fakes in tests.
/// </summary>
public interface IPickListSapHeaderReader
{
    CachedPickList? GetPickListHeaderByAbsEntry(int absEntry);
}
