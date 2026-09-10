using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SapReplitAPI.DTOs.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Produces a stable SHA-256 hex hash of the canonical commercial intent.
/// Fields included: CardCode, DocDate, DeliveryDate, DeliveryLocation, SlpCode,
///   and per-line: RequestLineId, ItemCode, RequestedQty, UnitPrice.
/// Excluded: Description, U_ItemName, U_Manufacturer (display-only).
/// Lines are sorted by RequestLineId before hashing so order never matters.
///
/// Backward-compatible origin: OriginWhsCode participates ONLY when non-null and non-whitespace.
/// Existing requests stored without OriginWhsCode produce the same hash as before this feature.
/// A meaningful OriginWhsCode (e.g. "004") creates a distinct hash from the same payload without it.
/// </summary>
public sealed class PayloadHashService
{
    public string Compute(CreateZoneFulfillmentOrderRequest req)
    {
        string? effectiveOrigin = string.IsNullOrWhiteSpace(req.OriginWhsCode)
            ? null
            : req.OriginWhsCode.Trim();

        var orderedLines = req.Lines
            .OrderBy(l => l.RequestLineId)
            .Select(l => new
            {
                requestLineId = l.RequestLineId,
                itemCode      = l.ItemCode,
                requestedQty  = l.RequestedQty,
                unitPrice     = l.UnitPrice
            })
            .ToArray();

        // Two distinct anonymous types so JSON serialisation never emits "originWhsCode": null
        // for legacy requests. Omitting the field entirely preserves historical SHA-256 output.
        string json;
        if (effectiveOrigin is null)
        {
            var canonical = new
            {
                cardCode         = req.CardCode,
                docDate          = req.DocDate.ToString("yyyy-MM-dd"),
                deliveryDate     = req.DeliveryDate.ToString("yyyy-MM-dd"),
                deliveryLocation = req.DeliveryLocation,
                slpCode          = req.SlpCode,
                lines            = orderedLines
            };
            json = JsonSerializer.Serialize(canonical);
        }
        else
        {
            var canonical = new
            {
                cardCode         = req.CardCode,
                docDate          = req.DocDate.ToString("yyyy-MM-dd"),
                deliveryDate     = req.DeliveryDate.ToString("yyyy-MM-dd"),
                deliveryLocation = req.DeliveryLocation,
                slpCode          = req.SlpCode,
                originWhsCode    = effectiveOrigin,
                lines            = orderedLines
            };
            json = JsonSerializer.Serialize(canonical);
        }

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
