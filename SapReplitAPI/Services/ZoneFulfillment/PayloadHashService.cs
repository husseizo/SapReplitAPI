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
/// </summary>
public sealed class PayloadHashService
{
    public string Compute(CreateZoneFulfillmentOrderRequest req)
    {
        var canonical = new
        {
            cardCode         = req.CardCode,
            docDate          = req.DocDate.ToString("yyyy-MM-dd"),
            deliveryDate     = req.DeliveryDate.ToString("yyyy-MM-dd"),
            deliveryLocation = req.DeliveryLocation,
            slpCode          = req.SlpCode,
            lines            = req.Lines
                .OrderBy(l => l.RequestLineId)
                .Select(l => new
                {
                    requestLineId = l.RequestLineId,
                    itemCode      = l.ItemCode,
                    requestedQty  = l.RequestedQty,
                    unitPrice     = l.UnitPrice
                })
                .ToArray()
        };

        string json  = JsonSerializer.Serialize(canonical);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
