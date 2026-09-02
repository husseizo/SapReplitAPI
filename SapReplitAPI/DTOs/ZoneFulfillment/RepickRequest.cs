using System.ComponentModel.DataAnnotations;

namespace SapReplitAPI.DTOs.ZoneFulfillment;

/// <summary>
/// POST .../orders/{requestId}/pick-lists/repick
/// Specifies which SO line to repick (must have a closed historical OPKL and open RDR1 OpenQty).
/// </summary>
public sealed class RepickRequest
{
    [Required]
    public int SoLineNum { get; init; }
}
