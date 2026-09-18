using SapReplitAPI.Models.Warehouse;

namespace SapReplitAPI.Services.Warehouse;

/// <summary>
/// Pure static validation logic for picker bin selections.
/// No SAP calls. No DI. Fully unit-testable.
/// </summary>
public static class PickBinValidator
{
    /// <summary>
    /// Validate one pick line's bin selection against a freshly-loaded candidate list.
    ///
    /// Rules:
    ///   - At least one bin must be selected.
    ///   - Each selected bin must appear in <paramref name="candidates"/> (wrong-WHS and
    ///     disabled bins are absent from candidates so they are implicitly rejected).
    ///   - Each selected bin's requested qty must be &gt; 0.
    ///   - Each selected bin's requested qty must not exceed the candidate's available qty.
    ///   - Partial pick is allowed: sum(selections) &lt; releasedQty is valid.
    ///   - But sum(selections) must equal <paramref name="desiredPickedQty"/>.
    ///
    /// Returns null if the selection is valid; otherwise an error message.
    /// </summary>
    public static string? ValidateLine(
        decimal                            desiredPickedQty,
        IReadOnlyList<BinCandidateDto>     candidates,
        IReadOnlyList<BinAllocationRequestDto> selections)
    {
        if (selections.Count == 0)
            return "No bins selected.";

        if (desiredPickedQty <= 0)
            return $"desiredPickedQty must be > 0 (got {desiredPickedQty}).";

        // Build a lookup from BinAbsEntry → available qty
        var candidateMap = candidates.ToDictionary(c => c.BinAbsEntry, c => c.AvailableQty);

        foreach (var sel in selections)
        {
            if (sel.Qty <= 0)
                return $"BinAbsEntry {sel.BinAbsEntry}: requested qty must be > 0 (got {sel.Qty}).";

            if (!candidateMap.TryGetValue(sel.BinAbsEntry, out decimal available))
                return $"BinAbsEntry {sel.BinAbsEntry} is not a valid candidate for this line " +
                       $"(wrong warehouse, disabled, or no stock).";

            if (sel.Qty > available)
                return $"BinAbsEntry {sel.BinAbsEntry}: requested {sel.Qty} but only {available} available.";
        }

        decimal total = selections.Sum(s => s.Qty);
        if (total != desiredPickedQty)
            return $"Selected bin totals {total} but desiredPickedQty is {desiredPickedQty}.";

        return null;
    }
}
