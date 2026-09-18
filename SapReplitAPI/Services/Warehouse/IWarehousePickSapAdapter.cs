using SapReplitAPI.Models.Warehouse;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.Warehouse;

/// <summary>
/// Thin SAP capability surface used by the Warehouse App picking flow.
/// Wraps only the two SAP operations needed: bin-candidate query and pick execution.
/// Isolated so tests can inject a fake without touching SapService.
/// </summary>
public interface IWarehousePickSapAdapter
{
    /// <summary>
    /// Returns live bin candidates for <paramref name="itemCode"/> in <paramref name="whsCode"/>.
    /// Filters: OBIN.Disabled = 'N', OBIN.WhsCode = whsCode, OIBQ.OnHandQty > 0.
    /// Sorted by available qty descending.
    /// </summary>
    List<BinCandidateDto> QueryBinCandidates(string itemCode, string whsCode);

    /// <summary>
    /// Reads the live SAP state of one PKL1 line for the pre-confirm stale-state gate.
    /// Queries OPKL (Status, Canceled) and PKL1 (PickQtty, RelQtty, PickStatus).
    /// Returns null when the pick entry no longer exists in SAP.
    /// </summary>
    LiveSapPickState? ReadLiveSapPickState(int absEntry, int pickEntry);

    /// <summary>
    /// Executes one SAP DI API pick list Update() for a single PKL1 line.
    /// Delegates to UpdateZoneFulfillmentPickList (existing, production-proven).
    /// </summary>
    (int Rc, string? SapError, Pkl1LineState? PostState) ExecutePick(
        int                              absEntry,
        int                              soDocEntry,
        int                              soLineNum,
        double                           desiredPickedQty,
        IReadOnlyList<BinPickAlloc>      binAllocs,
        string                           itemCode,
        string                           whsCode);
}
