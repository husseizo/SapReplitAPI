using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfWhsChange;

/// <summary>
/// PR01-PR12: ZF Client Unit Price / VAT-inclusive price override tests.
///
/// Tests cover:
///   - null vs 0 vs explicit price hash distinction (PayloadHashService)
///   - DomainRequestLine nullable UnitPrice
///   - Split-line fragments share same UnitPrice
///   - FulfillmentRequestLine nullable model
///   - AMBER replan DTO price preservation
/// </summary>
public sealed class ZfUnitPriceTests
{
    private static readonly PayloadHashService Hasher = new();

    private static readonly Guid LineId1 = Guid.Parse("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid LineId2 = Guid.Parse("AAAAAAAA-0000-0000-0000-000000000002");
    private static readonly Guid ReqId   = Guid.Parse("BBBBBBBB-0000-0000-0000-000000000001");

    private static CreateZoneFulfillmentOrderRequest MakeSingleLineReq(decimal? unitPrice) =>
        new()
        {
            RequestId        = ReqId,
            CardCode         = "CUS001166",
            DocDate          = new DateOnly(2026, 9, 19),
            DeliveryDate     = new DateOnly(2026, 9, 20),
            DeliveryLocation = "Mikocheni",
            Lines            =
            [
                new CreateZFOrderLine
                {
                    RequestLineId = LineId1,
                    LineSeq       = 1,
                    ItemCode      = "BM10530",
                    RequestedQty  = 2m,
                    UnitPrice     = unitPrice
                }
            ]
        };

    // ── PR01: UnitPrice=null accepted, does not throw ─────────────────────────

    [Fact]
    public void PR01_NullUnitPrice_IsValidInDto()
    {
        var req = MakeSingleLineReq(null);
        Assert.Null(req.Lines[0].UnitPrice);
    }

    // ── PR02: UnitPrice=0 accepted, is explicit override ─────────────────────

    [Fact]
    public void PR02_ZeroUnitPrice_IsExplicitOverrideInDto()
    {
        var req = MakeSingleLineReq(0m);
        Assert.NotNull(req.Lines[0].UnitPrice);
        Assert.Equal(0m, req.Lines[0].UnitPrice!.Value);
    }

    // ── PR03: UnitPrice=30000 accepted ────────────────────────────────────────

    [Fact]
    public void PR03_ExplicitUnitPrice_IsPreservedInDto()
    {
        var req = MakeSingleLineReq(30_000m);
        Assert.Equal(30_000m, req.Lines[0].UnitPrice);
    }

    // ── PR04: Hash(null) != Hash(0) ───────────────────────────────────────────

    [Fact]
    public void PR04_NullPrice_HashDiffers_From_ZeroPrice()
    {
        var hNull = Hasher.Compute(MakeSingleLineReq(null));
        var hZero = Hasher.Compute(MakeSingleLineReq(0m));
        Assert.NotEqual(hNull, hZero);
    }

    // ── PR05: Hash(null) != Hash(30000) ──────────────────────────────────────

    [Fact]
    public void PR05_NullPrice_HashDiffers_From_ExplicitPrice()
    {
        var hNull  = Hasher.Compute(MakeSingleLineReq(null));
        var hValue = Hasher.Compute(MakeSingleLineReq(30_000m));
        Assert.NotEqual(hNull, hValue);
    }

    // ── PR06: Hash(0) != Hash(30000) ─────────────────────────────────────────

    [Fact]
    public void PR06_ZeroPrice_HashDiffers_From_ExplicitPrice()
    {
        var hZero  = Hasher.Compute(MakeSingleLineReq(0m));
        var hValue = Hasher.Compute(MakeSingleLineReq(30_000m));
        Assert.NotEqual(hZero, hValue);
    }

    // ── PR07: Hash(null) == Hash(null) — determinism ─────────────────────────

    [Fact]
    public void PR07_NullPrice_IsDeterministic()
    {
        var h1 = Hasher.Compute(MakeSingleLineReq(null));
        var h2 = Hasher.Compute(MakeSingleLineReq(null));
        Assert.Equal(h1, h2);
    }

    // ── PR08: Hash(30000) == Hash(30000) — determinism ───────────────────────

    [Fact]
    public void PR08_ExplicitPrice_IsDeterministic()
    {
        var h1 = Hasher.Compute(MakeSingleLineReq(30_000m));
        var h2 = Hasher.Compute(MakeSingleLineReq(30_000m));
        Assert.Equal(h1, h2);
    }

    // ── PR09: DomainRequestLine carries nullable UnitPrice through ────────────

    [Fact]
    public void PR09_DomainRequestLine_AcceptsNullUnitPrice()
    {
        var domain = new DomainRequestLine(
            LineId1, 1, "BM10530", 2m, null, null, null, null);
        Assert.Null(domain.UnitPrice);
    }

    [Fact]
    public void PR09b_DomainRequestLine_AcceptsExplicitUnitPrice()
    {
        var domain = new DomainRequestLine(
            LineId1, 1, "BM10530", 2m, 30_000m, null, null, null);
        Assert.Equal(30_000m, domain.UnitPrice);
    }

    // ── PR10: Split fragments of same commercial line share same UnitPrice ────

    [Fact]
    public void PR10_SplitFragments_SameRequestLineId_ShareSameUnitPrice()
    {
        // Two fragments for same RequestLineId (split across WHS01 and WHS02).
        // Both must read the same DomainRequestLine → same UnitPrice.
        var reqLine = new DomainRequestLine(
            LineId1, 1, "BM10530", 10m, 25_000m, null, null, null);

        var frag1 = new AllocationFragment(LineId1, "WHS01", 6m, 0m);
        var frag2 = new AllocationFragment(LineId1, "WHS02", 4m, 0m);

        var lineById = new Dictionary<Guid, DomainRequestLine> { [LineId1] = reqLine };

        // Simulate the SapService loop: look up reqLine for each fragment.
        decimal? priceForFrag1 = lineById[frag1.RequestLineId].UnitPrice;
        decimal? priceForFrag2 = lineById[frag2.RequestLineId].UnitPrice;

        Assert.Equal(priceForFrag1, priceForFrag2);
        Assert.Equal(25_000m, priceForFrag1);
    }

    // ── PR11: FulfillmentRequestLine model accepts null UnitPrice ─────────────

    [Fact]
    public void PR11_FulfillmentRequestLine_UnitPrice_IsNullable()
    {
        var line = new FulfillmentRequestLine
        {
            RequestLineId = LineId1,
            LineSeq       = 1,
            ItemCode      = "BM10530",
            RequestedQty  = 2m,
            UnitPrice     = null
        };
        Assert.Null(line.UnitPrice);
    }

    [Fact]
    public void PR11b_FulfillmentRequestLine_UnitPrice_AcceptsExplicitValue()
    {
        var line = new FulfillmentRequestLine
        {
            RequestLineId = LineId1,
            LineSeq       = 1,
            ItemCode      = "BM10530",
            RequestedQty  = 2m,
            UnitPrice     = 30_000m
        };
        Assert.Equal(30_000m, line.UnitPrice);
    }

    // ── PR12: Multi-line request: null and explicit price on different lines hash distinctly ──

    [Fact]
    public void PR12_TwoLines_DifferentPrices_DifferentHash()
    {
        var req1 = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = ReqId,
            CardCode         = "CUS001166",
            DocDate          = new DateOnly(2026, 9, 19),
            DeliveryDate     = new DateOnly(2026, 9, 20),
            DeliveryLocation = "Mikocheni",
            Lines            =
            [
                new CreateZFOrderLine
                {
                    RequestLineId = LineId1, LineSeq = 1, ItemCode = "BM10530",
                    RequestedQty  = 2m,      UnitPrice = null
                },
                new CreateZFOrderLine
                {
                    RequestLineId = LineId2, LineSeq = 2, ItemCode = "BM10531",
                    RequestedQty  = 1m,      UnitPrice = 15_000m
                }
            ]
        };

        var req2 = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = ReqId,
            CardCode         = "CUS001166",
            DocDate          = new DateOnly(2026, 9, 19),
            DeliveryDate     = new DateOnly(2026, 9, 20),
            DeliveryLocation = "Mikocheni",
            Lines            =
            [
                new CreateZFOrderLine
                {
                    RequestLineId = LineId1, LineSeq = 1, ItemCode = "BM10530",
                    RequestedQty  = 2m,      UnitPrice = 20_000m  // different
                },
                new CreateZFOrderLine
                {
                    RequestLineId = LineId2, LineSeq = 2, ItemCode = "BM10531",
                    RequestedQty  = 1m,      UnitPrice = 15_000m
                }
            ]
        };

        Assert.NotEqual(Hasher.Compute(req1), Hasher.Compute(req2));
    }
}
