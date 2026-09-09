using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Thin adapter that wraps SapService.GetZfPickListValidationState
/// and exposes it through the IZfPickSapReader interface.
/// Registered Scoped — SapService is itself Scoped.
/// </summary>
public sealed class SapZfPickAdapter : IZfPickSapReader
{
    private readonly SapService _sap;

    public SapZfPickAdapter(SapService sap) => _sap = sap;

    public (ZfOpklValidation? Header,
            IReadOnlyList<ZfPkl1Validation> Lines,
            IReadOnlyList<ZfPkl2Validation> Bins)
        GetPickListValidationState(int absEntry)
        => _sap.GetZfPickListValidationState(absEntry);
}
