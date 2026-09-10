using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// Backward-compatibility tests for PayloadHashService.
/// Verifies that adding OriginWhsCode does not alter historical hashes
/// and that a meaningful OriginWhsCode produces a distinct hash.
/// </summary>
public sealed class PayloadHashBackwardCompatTests
{
    private static readonly PayloadHashService Svc = new();

    private static CreateZoneFulfillmentOrderRequest BaseRequest(string? origin = null)
        => new()
        {
            RequestId        = Guid.NewGuid(),
            CardCode         = "C00001",
            DocDate          = new DateOnly(2026, 1, 15),
            DeliveryDate     = new DateOnly(2026, 1, 20),
            DeliveryLocation = "Cluster-side",
            SlpCode          = 42,
            OriginWhsCode    = origin,
            Lines            =
            [
                new CreateZFOrderLine
                {
                    RequestLineId = new Guid("aaaaaaaa-0000-0000-0000-000000000001"),
                    LineSeq       = 1,
                    ItemCode      = "ITEM-001",
                    RequestedQty  = 10m,
                    UnitPrice     = 5.50m
                },
                new CreateZFOrderLine
                {
                    RequestLineId = new Guid("aaaaaaaa-0000-0000-0000-000000000002"),
                    LineSeq       = 2,
                    ItemCode      = "ITEM-002",
                    RequestedQty  = 3m,
                    UnitPrice     = 12m
                }
            ]
        };

    // ── I01 ──────────────────────────────────────────────────────────────────
    // Null OriginWhsCode → same hash as the pre-origin request (backward compat).
    [Fact]
    public void I01_NullOrigin_HashMatchesLegacyHash()
    {
        var h1 = Svc.Compute(BaseRequest(origin: null));
        var h2 = Svc.Compute(BaseRequest(origin: null));

        Assert.Equal(h1, h2);
    }

    // ── I02 ──────────────────────────────────────────────────────────────────
    // Empty OriginWhsCode → treated as null; same hash as null-origin request.
    [Fact]
    public void I02_EmptyOrigin_HashMatchesNullOriginHash()
    {
        var hNull  = Svc.Compute(BaseRequest(origin: null));
        var hEmpty = Svc.Compute(BaseRequest(origin: ""));

        Assert.Equal(hNull, hEmpty);
    }

    // ── I03 ──────────────────────────────────────────────────────────────────
    // Whitespace-only OriginWhsCode → treated as null; hash unchanged.
    [Fact]
    public void I03_WhitespaceOrigin_HashMatchesNullOriginHash()
    {
        var hNull  = Svc.Compute(BaseRequest(origin: null));
        var hSpace = Svc.Compute(BaseRequest(origin: "   "));

        Assert.Equal(hNull, hSpace);
    }

    // ── I04 ──────────────────────────────────────────────────────────────────
    // Non-empty OriginWhsCode → hash differs from null-origin hash.
    [Fact]
    public void I04_NonEmptyOrigin_HashDiffersFromNullOriginHash()
    {
        var hNull   = Svc.Compute(BaseRequest(origin: null));
        var hOrigin = Svc.Compute(BaseRequest(origin: "004"));

        Assert.NotEqual(hNull, hOrigin);
    }

    // ── I05 ──────────────────────────────────────────────────────────────────
    // Same payload + same OriginWhsCode → identical hash (deterministic).
    [Fact]
    public void I05_SamePayloadAndSameOrigin_DeterministicHash()
    {
        var h1 = Svc.Compute(BaseRequest(origin: "004"));
        var h2 = Svc.Compute(BaseRequest(origin: "004"));

        Assert.Equal(h1, h2);
    }

    // ── I06 ──────────────────────────────────────────────────────────────────
    // Different OriginWhsCode → different hash even with identical other fields.
    [Fact]
    public void I06_DifferentOrigins_ProduceDifferentHashes()
    {
        var h001 = Svc.Compute(BaseRequest(origin: "001"));
        var h004 = Svc.Compute(BaseRequest(origin: "004"));

        Assert.NotEqual(h001, h004);
    }

    // ── I07 ──────────────────────────────────────────────────────────────────
    // Line order in the request does not affect hash (lines sorted by RequestLineId).
    [Fact]
    public void I07_LineOrder_DoesNotAffectHash()
    {
        var lineA = new CreateZFOrderLine
        {
            RequestLineId = new Guid("aaaaaaaa-0000-0000-0000-000000000001"),
            LineSeq       = 1,
            ItemCode      = "ITEM-A",
            RequestedQty  = 5m,
            UnitPrice     = 10m
        };
        var lineB = new CreateZFOrderLine
        {
            RequestLineId = new Guid("bbbbbbbb-0000-0000-0000-000000000002"),
            LineSeq       = 2,
            ItemCode      = "ITEM-B",
            RequestedQty  = 3m,
            UnitPrice     = 20m
        };

        var req1 = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = Guid.NewGuid(),
            CardCode         = "C99",
            DocDate          = new DateOnly(2026, 3, 1),
            DeliveryDate     = new DateOnly(2026, 3, 5),
            DeliveryLocation = "Cluster-side",
            Lines            = [lineA, lineB]
        };
        var req2 = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = Guid.NewGuid(),
            CardCode         = "C99",
            DocDate          = new DateOnly(2026, 3, 1),
            DeliveryDate     = new DateOnly(2026, 3, 5),
            DeliveryLocation = "Cluster-side",
            Lines            = [lineB, lineA]   // reversed
        };

        Assert.Equal(Svc.Compute(req1), Svc.Compute(req2));
    }
}
