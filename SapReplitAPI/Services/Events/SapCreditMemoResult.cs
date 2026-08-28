namespace SapReplitAPI.Services.Events;

/// <summary>
/// Minimal projection of an ORIN (A/R Credit Memo) row needed by CreditMemoEventHandler.
/// DocNum — for CheckOinmAsync physical-inventory probe.
/// Lines  — BaseEntry/BaseType pairs from RIN1 used to find affected OINV DocEntries.
/// </summary>
public sealed record SapCreditMemoResult(
    int DocEntry,
    int DocNum,
    IReadOnlyList<(int BaseEntry, int BaseType)> Lines
);
