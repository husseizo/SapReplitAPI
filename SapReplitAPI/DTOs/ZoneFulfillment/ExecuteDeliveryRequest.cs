using System.ComponentModel.DataAnnotations;

namespace SapReplitAPI.DTOs.ZoneFulfillment;

/// <summary>
/// POST .../orders/{requestId}/delivery
/// Phase C: mutation is disabled — ODLN.Add() requires separate explicit authorization.
/// </summary>
public sealed class ExecuteDeliveryRequest
{
    [Required, Range(1, int.MaxValue, ErrorMessage = "PickListAbsEntry must be positive.")]
    public int PickListAbsEntry { get; init; }
}
