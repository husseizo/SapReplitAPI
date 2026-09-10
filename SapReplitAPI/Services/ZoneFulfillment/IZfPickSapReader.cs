using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Read-only SAP surface used exclusively by ZoneFulfillmentPickReconciliationService.
/// Extracted as interface so the reconciliation logic can be unit-tested without COM interop.
/// </summary>
public interface IZfPickSapReader
{
    (ZfOpklValidation? Header,
     IReadOnlyList<ZfPkl1Validation> Lines,
     IReadOnlyList<ZfPkl2Validation> Bins)
        GetPickListValidationState(int absEntry);
}
