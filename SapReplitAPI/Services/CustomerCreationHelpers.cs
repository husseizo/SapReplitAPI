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
    ///   - Retry ONLY when SAP reported -2035 AND a subsequent OCRD check confirms the
    ///     candidate CardCode now exists (concurrent creation race).
    ///   - Any other error code: immediate failure — do not cycle CardCodes.
    ///   - -2035 from address/phone/other table: cardCodeNowExists will be false → no retry.
    /// </summary>
    internal static bool ShouldRetryOnCardCodeCollision(int sapErrorCode, bool cardCodeNowExistsInOcrd)
        => sapErrorCode == -2035 && cardCodeNowExistsInOcrd;
}
