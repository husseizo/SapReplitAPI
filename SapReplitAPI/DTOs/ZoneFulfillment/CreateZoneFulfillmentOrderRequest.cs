using System.ComponentModel.DataAnnotations;

namespace SapReplitAPI.DTOs.ZoneFulfillment;

/// <summary>
/// POST /api/zone-fulfillment/experimental/orders
/// Client must generate RequestId before calling. Same RequestId + same payload = idempotent.
/// Same RequestId + different payload = 409 IdempotencyConflict.
/// </summary>
public sealed class CreateZoneFulfillmentOrderRequest
{
    /// <summary>Client-generated idempotency key. Must be stable across retries.</summary>
    [Required]
    public Guid RequestId { get; init; }

    [Required, StringLength(50)]
    public string CardCode { get; init; } = "";

    [Required]
    public DateOnly DocDate { get; init; }

    [Required]
    public DateOnly DeliveryDate { get; init; }

    /// <summary>Zone name. Must match a row in ZoneWarehousePriority. Required — no default.</summary>
    [Required, StringLength(50)]
    public string DeliveryLocation { get; init; } = "";

    public int? SlpCode { get; init; }

    [Required, MinLength(1)]
    public List<CreateZFOrderLine> Lines { get; init; } = [];
}

public sealed class CreateZFOrderLine
{
    /// <summary>Client-supplied stable identity for this commercial line. Immutable.</summary>
    [Required]
    public Guid RequestLineId { get; init; }

    /// <summary>Position within the request; used for FIFO allocation ordering.</summary>
    [Required]
    public int LineSeq { get; init; }

    [Required, StringLength(50)]
    public string ItemCode { get; init; } = "";

    [Required, Range(0.000001, double.MaxValue)]
    public decimal RequestedQty { get; init; }

    [Required, Range(0, double.MaxValue)]
    public decimal UnitPrice { get; init; }

    [StringLength(200)]
    public string? Description { get; init; }

    [StringLength(200)]
    public string? U_ItemName { get; init; }

    [StringLength(50)]
    public string? U_Manufacturer { get; init; }
}
