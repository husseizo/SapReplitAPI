using System.Runtime.Versioning;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Production implementation of IZfWhsChangeSapReader.
/// Delegates to SapService COM-interop methods. Windows-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZfSapWhsChangeSapReader : IZfWhsChangeSapReader
{
    private readonly SapService _sap;

    public ZfSapWhsChangeSapReader(SapService sap) => _sap = sap;

    public Pkl1LineState? ReadPickListLine(int absEntry, int soDocEntry, int soLineNum)
        => _sap.ReadZoneFulfillmentPickListLine(absEntry, soDocEntry, soLineNum);

    public decimal GetPkl2PickQttyForLine(int absEntry, int soDocEntry, int soLineNum)
    {
        var (_, lines, bins) = _sap.GetZfPickListValidationState(absEntry);
        var pkl1 = lines.FirstOrDefault(l => l.OrderEntry == soDocEntry && l.OrderLine == soLineNum);
        if (pkl1 == null) return 0m;
        return bins.Where(b => b.PickEntry == pkl1.PickEntry).Sum(b => b.PickQtty);
    }
}
