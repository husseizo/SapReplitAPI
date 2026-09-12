namespace SapReplitAPI.Services;

/// <summary>
/// Pure, stateless helpers for customer creation logic.
/// Extracted so they can be unit-tested without SAP COM dependencies.
/// </summary>
internal static class CustomerCreationHelpers
{
    /// <summary>
    /// Formats a 0-based max numeric suffix into the next CUS CardCode.
    /// </summary>
    internal static string FormatNextCardCode(int currentMax)
        => $"CUS{(currentMax + 1).ToString("D6")}";

    /// <summary>
    /// Picks the effective billing address: prefers Address, falls back to Address1.
    /// </summary>
    internal static string SelectAddress(string? address, string? address1)
        => !string.IsNullOrWhiteSpace(address) ? address.Trim()
         : !string.IsNullOrWhiteSpace(address1) ? address1.Trim()
         : string.Empty;

    /// <summary>
    /// Determines whether a failed bp.Add() should trigger a retry with the next CardCode.
    ///
    /// Rules (Phase 7):
    ///   - Retry when SAP reported -2035 AND either:
    ///       (a) OCRD now has the candidate (concurrent creation race), OR
    ///       (b) CRD1 has an orphaned row for the candidate (prior failed Add() not fully rolled back).
    ///   - Any other error code: immediate failure — do not cycle CardCodes.
    /// </summary>
    internal static bool ShouldRetryOnCardCodeCollision(int sapErrorCode, bool cardCodeNowExistsInOcrd, bool cardCodeNowExistsInCrd1 = false)
        => sapErrorCode == -2035 && (cardCodeNowExistsInOcrd || cardCodeNowExistsInCrd1);

    /// <summary>Parses the numeric suffix from a CUS-prefixed CardCode (e.g. "CUS001360" → 1360).</summary>
    internal static int ParseCardCodeNum(string cardCode)
        => cardCode.Length > 3 && int.TryParse(cardCode.Substring(3), out int n) ? n : 0;
}
