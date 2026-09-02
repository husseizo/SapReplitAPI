namespace SapReplitAPI.Models.ZoneFulfillment;

/// <summary>No active+default PickerAssignment exists for the requested WhsCode.</summary>
public sealed class PickerAssignmentNotFoundException : Exception
{
    public string WhsCode { get; }
    public PickerAssignmentNotFoundException(string whsCode)
        : base($"No active default picker assignment found for warehouse '{whsCode}'.")
        => WhsCode = whsCode;
}

/// <summary>
/// A PickerAssignment was found but is invalid (e.g., IsActive=false, SapUserId is null,
/// or multiple active+default rows exist for the same WhsCode).
/// </summary>
public sealed class PickerAssignmentInvalidException : Exception
{
    public string WhsCode { get; }
    public PickerAssignmentInvalidException(string whsCode, string reason)
        : base($"Picker assignment for warehouse '{whsCode}' is invalid: {reason}.")
        => WhsCode = whsCode;
}

/// <summary>
/// The SapUserId resolved from PickerAssignment could not be validated in OUSR
/// (user not found, or LOCKED != 'N').
/// </summary>
public sealed class PickerSapUserUnavailableException : Exception
{
    public int    SapUserId { get; }
    public string WhsCode   { get; }
    public PickerSapUserUnavailableException(string whsCode, int sapUserId, string reason)
        : base($"SAP user {sapUserId} for warehouse '{whsCode}' is unavailable: {reason}.")
    {
        WhsCode   = whsCode;
        SapUserId = sapUserId;
    }
}
