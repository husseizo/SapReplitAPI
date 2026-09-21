namespace SapReplitAPI.Models;

/// <summary>
/// Settings for the ZF Operations Console admin endpoints.
/// Bound from ZfAdmin: config section (env var: ZfAdmin__AdminApiKey).
/// Intentionally separate from ApiSecuritySettings — different key, different scope.
/// </summary>
public sealed class ZfAdminSettings
{
    public const string Section = "ZfAdmin";

    /// <summary>
    /// Key callers must send in the X-ZF-Admin-Key header on all /api/zf-admin/ requests.
    /// Leave empty in development to disable the check (all requests allowed).
    /// Never commit a real value.
    /// </summary>
    public string AdminApiKey { get; set; } = string.Empty;
}
