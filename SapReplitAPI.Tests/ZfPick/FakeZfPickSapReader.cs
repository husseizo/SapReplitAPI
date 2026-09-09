using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Tests.ZfPick;

/// <summary>
/// Deterministic fake for IZfPickSapReader.
/// Per-OPKL AbsEntry responses are registered via Add().
/// Unregistered AbsEntry returns (null, empty, empty) — OPKL not found.
/// </summary>
public sealed class FakeZfPickSapReader : IZfPickSapReader
{
    private readonly Dictionary<int,
        (ZfOpklValidation? Header,
         List<ZfPkl1Validation> Lines,
         List<ZfPkl2Validation> Bins)> _responses = new();

    public int CallCount { get; private set; }

    public void Register(
        int absEntry,
        ZfOpklValidation? header,
        List<ZfPkl1Validation>? lines = null,
        List<ZfPkl2Validation>? bins  = null)
    {
        _responses[absEntry] = (header, lines ?? [], bins ?? []);
    }

    public (ZfOpklValidation? Header,
            IReadOnlyList<ZfPkl1Validation> Lines,
            IReadOnlyList<ZfPkl2Validation> Bins)
        GetPickListValidationState(int absEntry)
    {
        CallCount++;
        if (_responses.TryGetValue(absEntry, out var r))
            return (r.Header, r.Lines, r.Bins);
        return (null, [], []);
    }

    // ── Factory helpers ───────────────────────────────────────────────────────

    public static ZfOpklValidation ValidOpkl(int absEntry, string uReplitId) =>
        new(absEntry, Status: "Y", Canceled: "N", UReplitId: uReplitId);

    public static ZfOpklValidation OpenOpkl(int absEntry, string uReplitId) =>
        new(absEntry, Status: "O", Canceled: "N", UReplitId: uReplitId);

    public static ZfOpklValidation CanceledOpkl(int absEntry, string uReplitId) =>
        new(absEntry, Status: "Y", Canceled: "Y", UReplitId: uReplitId);

    public static ZfPkl1Validation ValidLine(
        int     pickEntry,
        int     orderEntry,
        int     orderLine,
        string  whsCode,
        decimal relQtty,
        decimal pickQtty,
        string  pickStatus = "Y",
        int     baseObject = 17)
        => new(pickEntry, orderEntry, orderLine, baseObject, whsCode, relQtty, pickQtty, pickStatus);
}
