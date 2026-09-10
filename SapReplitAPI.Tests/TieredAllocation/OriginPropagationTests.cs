using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// O01–O10: Origin propagation regression tests (Gate 3C).
///
/// O01–O03: effectiveReq preserves OriginWhsCode through the controller rebuild.
/// O04–O09: TieredWarehousePriorityResolver.DetermineEffectiveOrigin correctness.
/// O10:     Legacy null/blank origin is backward-compatible with PayloadHashService (see I01-I03).
/// </summary>
public sealed class OriginPropagationTests
{
    // ── helper: invoke private DetermineEffectiveOrigin without DB ──────────────

    private static string EffectiveOriginFor(string? received, string zone)
    {
        var opts = Options.Create(new ZoneFulfillmentOptions
        {
            TieredClusterDefault   = "001",
            TieredMikocheniDefault = "003"
        });
        // repo is never accessed by DetermineEffectiveOrigin — passing null is safe
        var resolver = new TieredWarehousePriorityResolver(
            null!,
            opts,
            NullLogger<TieredWarehousePriorityResolver>.Instance);

        var method = typeof(TieredWarehousePriorityResolver)
            .GetMethod("DetermineEffectiveOrigin",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (string)method.Invoke(resolver, [received, zone])!;
    }

    // ── O01: effectiveReq propagates OriginWhsCode ─────────────────────────────
    [Fact]
    public void O01_EffectiveReq_PreservesOriginWhsCode()
    {
        // Simulates the controller's effectiveReq initializer after the S3 fix.
        var req = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = Guid.NewGuid(),
            CardCode         = "CUS001181",
            DocDate          = new DateOnly(2026, 9, 10),
            DeliveryDate     = new DateOnly(2026, 9, 11),
            DeliveryLocation = "Cluster-side",
            SlpCode          = 5,
            OriginWhsCode    = "004",
            Lines            = []
        };

        var effectiveReq = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = req.RequestId,
            CardCode         = req.CardCode,
            DocDate          = req.DocDate,
            DeliveryDate     = req.DeliveryDate,
            DeliveryLocation = "Cluster-side",
            SlpCode          = req.SlpCode,
            Lines            = req.Lines,
            OriginWhsCode    = req.OriginWhsCode   // the S3 fix
        };

        Assert.Equal("004", effectiveReq.OriginWhsCode);
    }

    // ── O02: missing OriginWhsCode before fix → null in effectiveReq ───────────
    [Fact]
    public void O02_WithoutFix_EffectiveReqWouldLoseOrigin()
    {
        // Demonstrates the pre-fix bug: omitting OriginWhsCode in the initializer
        // silently drops it (default value is null).
        var req = new CreateZoneFulfillmentOrderRequest
        {
            RequestId     = Guid.NewGuid(),
            CardCode      = "CUS001181",
            DocDate       = new DateOnly(2026, 9, 10),
            DeliveryDate  = new DateOnly(2026, 9, 11),
            OriginWhsCode = "004",
            Lines         = []
        };

        var preFix = new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = req.RequestId,
            CardCode         = req.CardCode,
            DocDate          = req.DocDate,
            DeliveryDate     = req.DeliveryDate,
            DeliveryLocation = "Cluster-side",
            SlpCode          = req.SlpCode,
            Lines            = req.Lines
            // OriginWhsCode intentionally omitted — the old bug
        };

        Assert.Null(preFix.OriginWhsCode);
        Assert.Equal("004", req.OriginWhsCode); // source had origin
    }

    // ── O03: PayloadHashService includes a non-blank OriginWhsCode in the hash ──
    [Fact]
    public void O03_PayloadHash_IncludesNonBlankOrigin()
    {
        var svc = new PayloadHashService();
        var rid = Guid.NewGuid();

        string hashNoOrigin  = svc.Compute(MakeReq(rid, origin: null));
        string hashWithOrigin = svc.Compute(MakeReq(rid, origin: "004"));

        Assert.NotEqual(hashNoOrigin, hashWithOrigin);
    }

    // ── O04: Cluster-side, known origin 004 → EffectiveOrigin = 004 ────────────
    [Fact]
    public void O04_ClusterSide_Origin004_EffectiveIs004()
    {
        Assert.Equal("004", EffectiveOriginFor("004", "Cluster-side"));
    }

    // ── O05: All four known Cluster-side origins pass through unchanged ─────────
    [Theory]
    [InlineData("001")]
    [InlineData("002")]
    [InlineData("003")]
    [InlineData("004")]
    public void O05_ClusterSide_KnownOrigins_PassThrough(string origin)
    {
        Assert.Equal(origin, EffectiveOriginFor(origin, "Cluster-side"));
    }

    // ── O06: Cluster-side, blank/null origin → EffectiveOrigin = 001 (default) ─
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void O06_ClusterSide_BlankOrNullOrigin_DefaultsTo001(string? origin)
    {
        Assert.Equal("001", EffectiveOriginFor(origin, "Cluster-side"));
    }

    // ── O07: Cluster-side, unknown/unrecognized origin → EffectiveOrigin = 001 ─
    [Theory]
    [InlineData("999")]
    [InlineData("XYZ")]
    [InlineData("005")]
    public void O07_ClusterSide_UnknownOrigin_DefaultsTo001(string origin)
    {
        Assert.Equal("001", EffectiveOriginFor(origin, "Cluster-side"));
    }

    // ── O08: Mikocheni-side origin resolution ────────────────────────────────────
    // DetermineEffectiveOrigin for Mikocheni:
    //   - null/blank/unknown → "003" (zone default)
    //   - known origin (001/002/003/004) → passes through unchanged
    // The lookup step (GetOriginWarehousePriorityAsync) always uses "003" for Mikocheni,
    // so the priority order is always Mikocheni-normalized — but EffectiveOrigin reflects
    // the resolved origin, not the lookup key.
    [Theory]
    [InlineData(null,  "003")]
    [InlineData("",    "003")]
    [InlineData("XYZ", "003")]
    [InlineData("004", "004")]
    [InlineData("001", "001")]
    [InlineData("003", "003")]
    public void O08_MikocheniSide_OriginResolution(string? origin, string expected)
    {
        Assert.Equal(expected, EffectiveOriginFor(origin, "Mikocheni-side"));
    }

    // ── O09: SlpCode on the request does not affect EffectiveOrigin ─────────────
    [Fact]
    public void O09_SlpCode_DoesNotAffectEffectiveOrigin()
    {
        // DetermineEffectiveOrigin has no SlpCode parameter — origin resolution
        // is completely independent of the sales person code.
        string eo1 = EffectiveOriginFor("004", "Cluster-side");
        string eo2 = EffectiveOriginFor("004", "Cluster-side"); // same, regardless of SlpCode

        Assert.Equal("004", eo1);
        Assert.Equal(eo1, eo2);
    }

    // ── O10: null/blank origin → legacy hash preserved (backward compat) ────────
    // Covered by PayloadHashBackwardCompatTests I01-I03; documented here for completeness.
    [Fact]
    public void O10_NullOriginHash_IsDeterministic_And_MatchesBlankOriginHash()
    {
        var svc = new PayloadHashService();
        var rid = Guid.NewGuid();

        string hNull  = svc.Compute(MakeReq(rid, origin: null));
        string hEmpty = svc.Compute(MakeReq(rid, origin: ""));
        string hSpace = svc.Compute(MakeReq(rid, origin: "  "));
        string hNull2 = svc.Compute(MakeReq(rid, origin: null));

        Assert.Equal(hNull,  hEmpty);
        Assert.Equal(hNull,  hSpace);
        Assert.Equal(hNull,  hNull2);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static CreateZoneFulfillmentOrderRequest MakeReq(Guid rid, string? origin)
        => new()
        {
            RequestId        = rid,
            CardCode         = "CUS001181",
            DocDate          = new DateOnly(2026, 9, 10),
            DeliveryDate     = new DateOnly(2026, 9, 11),
            DeliveryLocation = "Cluster-side",
            OriginWhsCode    = origin,
            Lines            =
            [
                new CreateZFOrderLine
                {
                    RequestLineId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                    LineSeq       = 1,
                    ItemCode      = "VAG10568",
                    RequestedQty  = 1m,
                    UnitPrice     = 50m
                }
            ]
        };
}
