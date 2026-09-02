using System.ComponentModel.DataAnnotations;

namespace SapReplitAPI.DTOs.ZoneFulfillment;

/// <summary>
/// POST .../orders/{requestId}/pick-lists/{absEntry}/pick
/// Desired-state: DesiredPickedQty is the target final value, not a delta.
/// C-SAP-03 constraint: DesiredPickedQty must equal ReleasedQty (full-pick only).
/// </summary>
public sealed class ExecutePickRequest
{
    [Required, Range(0.000001, double.MaxValue, ErrorMessage = "DesiredPickedQty must be positive.")]
    public decimal DesiredPickedQty { get; init; }
}
