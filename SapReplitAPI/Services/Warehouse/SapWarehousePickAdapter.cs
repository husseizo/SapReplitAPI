using SapReplitAPI.Models.Warehouse;
using SapReplitAPI.Models.ZoneFulfillment;
using System.Runtime.Versioning;

namespace SapReplitAPI.Services.Warehouse;

/// <summary>
/// Production implementation of <see cref="IWarehousePickSapAdapter"/>.
/// Wraps <see cref="SapService"/> — which is Windows-only (COM).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SapWarehousePickAdapter : IWarehousePickSapAdapter
{
    private readonly SapService _sap;

    public SapWarehousePickAdapter(SapService sap) => _sap = sap;

    public List<BinCandidateDto> QueryBinCandidates(string itemCode, string whsCode)
        => _sap.QueryWarehouseBinCandidates(itemCode, whsCode);

    public (int Rc, string? SapError, Pkl1LineState? PostState) ExecutePick(
        int absEntry, int soDocEntry, int soLineNum,
        double desiredPickedQty, IReadOnlyList<BinPickAlloc> binAllocs,
        string itemCode, string whsCode)
        => _sap.UpdateZoneFulfillmentPickList(
            absEntry, soDocEntry, soLineNum, desiredPickedQty, binAllocs, itemCode, whsCode);
}
