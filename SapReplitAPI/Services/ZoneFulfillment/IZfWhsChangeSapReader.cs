using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// SAP read-only gate checks for ZF warehouse reassignment.
/// Abstracted so tests can inject a fake without COM interop.
/// </summary>
public interface IZfWhsChangeSapReader
{
    /// <summary>
    /// Returns the PKL1 line state for a specific OPKL + SO line.
    /// Returns null when the line does not exist in SAP.
    /// </summary>
    Pkl1LineState? ReadPickListLine(int absEntry, int soDocEntry, int soLineNum);

    /// <summary>
    /// Returns the total PKL2 PickQtty for a specific OPKL line
    /// identified by (absEntry, soDocEntry, soLineNum).
    /// Returns 0 when no PKL2 rows exist (no bin allocations picked yet).
    /// </summary>
    decimal GetPkl2PickQttyForLine(int absEntry, int soDocEntry, int soLineNum);
}
