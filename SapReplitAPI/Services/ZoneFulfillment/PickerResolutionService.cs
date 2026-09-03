using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Resolves the SAP picker for a given warehouse.
/// Fail-closed: any of the 6 listed conditions throws before OPKL.Add() can proceed.
///
/// Fail-closed conditions:
///   1. No active+default PickerAssignment for WhsCode → PickerAssignmentNotFoundException
///   2. IsActive = false                               → PickerAssignmentInvalidException
///   3. SapUserId is null                              → PickerAssignmentInvalidException
///   4. Multiple active+default rows                   → PickerAssignmentInvalidException
///   5. OUSR row not found for SapUserId               → PickerSapUserUnavailableException
///   6. OUSR.LOCKED != 'N'                             → PickerSapUserUnavailableException
/// </summary>
public sealed class PickerResolutionService
{
    private readonly ZoneFulfillmentRepository _repo;
    private readonly SapService                _sap;
    private readonly ILogger<PickerResolutionService> _log;

    public PickerResolutionService(
        ZoneFulfillmentRepository       repo,
        SapService                      sap,
        ILogger<PickerResolutionService> log)
    {
        _repo = repo;
        _sap  = sap;
        _log  = log;
    }

    /// <summary>
    /// Resolves and validates the picker for the warehouse.
    /// Throws on any failure condition — never returns a partially valid result.
    /// </summary>
    public async Task<PickerResolutionResult> ResolveAsync(
        string whsCode, CancellationToken ct = default)
    {
        // Conditions 1–4: repository enforces them via exceptions
        var assignment = await _repo.GetPickerAssignmentAsync(whsCode, ct);

        // SapUserId guaranteed non-null here (repo already checked)
        var sapUserId = assignment.SapUserId!.Value;

        // Condition 5: OUSR row must exist
        var sapUser = _sap.GetPickerSapUser(sapUserId);
        if (sapUser is null)
            throw new PickerSapUserUnavailableException(whsCode, sapUserId,
                "user not found in OUSR");

        // Condition 6: user must not be locked
        if (sapUser.Locked != "N")
            throw new PickerSapUserUnavailableException(whsCode, sapUserId,
                $"LOCKED='{sapUser.Locked}'");

        _log.LogInformation(
            "[PickerResolution] Resolved picker for {WhsCode}: USERID={UserId} ({UserCode})",
            whsCode, sapUser.UserId, sapUser.UserCode);

        return new PickerResolutionResult
        {
            Assignment = assignment,
            SapUser    = sapUser
        };
    }
}
