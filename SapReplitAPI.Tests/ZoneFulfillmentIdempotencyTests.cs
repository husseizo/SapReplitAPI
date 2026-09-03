using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests;

/// <summary>
/// C-IDEM-01 through C-IDEM-07: idempotency contract tests.
/// Tests PayloadHashService determinism (the foundation of idempotency) and
/// U_ReplitId construction — both are pure functions, no mocks needed.
/// Orchestration-level idempotency (same RequestId → same/conflicting state)
/// is verified via integration tests against a real database (C-SAP-01/02/03).
/// </summary>
public sealed class ZoneFulfillmentIdempotencyTests
{
    private static readonly PayloadHashService Hasher = new();

    private static CreateZoneFulfillmentOrderRequest MakeReq(
        Guid requestId,
        string cardCode = "CUS001166",
        string location = "Mikocheni-side",
        int? slp = null)
    {
        return new CreateZoneFulfillmentOrderRequest
        {
            RequestId        = requestId,
            CardCode         = cardCode,
            DocDate          = new DateOnly(2026, 8, 31),
            DeliveryDate     = new DateOnly(2026, 9, 1),
            DeliveryLocation = location,
            SlpCode          = slp,
            Lines =
            [
                new CreateZFOrderLine
                {
                    RequestLineId = Guid.Parse("11111111-0000-0000-0000-000000000001"),
                    LineSeq       = 1,
                    ItemCode      = "BM10530",
                    RequestedQty  = 2m,
                    UnitPrice     = 100m
                }
            ]
        };
    }

    // ── C-IDEM-01: Same payload → same hash (determinism) ─────────────────────

    [Fact]
    public void C_IDEM_01_SamePayload_SameHash()
    {
        var rid = Guid.NewGuid();
        var req1 = MakeReq(rid);
        var req2 = MakeReq(rid);

        string h1 = Hasher.Compute(req1);
        string h2 = Hasher.Compute(req2);

        Assert.Equal(h1, h2);
    }

    // ── C-IDEM-02: Different CardCode → different hash ─────────────────────────

    [Fact]
    public void C_IDEM_02_DifferentCardCode_DifferentHash()
    {
        var rid  = Guid.NewGuid();
        var req1 = MakeReq(rid, cardCode: "CUS001166");
        var req2 = MakeReq(rid, cardCode: "CUS000001");

        Assert.NotEqual(Hasher.Compute(req1), Hasher.Compute(req2));
    }

    // ── C-IDEM-03: Different RequestedQty → different hash ────────────────────

    [Fact]
    public void C_IDEM_03_DifferentQty_DifferentHash()
    {
        var rid = Guid.NewGuid();
        var req1 = MakeReq(rid);
        var req2 = MakeReq(rid);
        req2.Lines[0] = new CreateZFOrderLine
        {
            RequestLineId = req1.Lines[0].RequestLineId,
            LineSeq       = req1.Lines[0].LineSeq,
            ItemCode      = req1.Lines[0].ItemCode,
            RequestedQty  = 3m,                        // changed
            UnitPrice     = req1.Lines[0].UnitPrice
        };

        Assert.NotEqual(Hasher.Compute(req1), Hasher.Compute(req2));
    }

    // ── C-IDEM-04: Line order doesn't affect hash (sorted by RequestLineId) ───

    [Fact]
    public void C_IDEM_04_LineOrder_DoesNotAffect_Hash()
    {
        var rid  = Guid.NewGuid();
        var lid1 = Guid.Parse("AAAAAAAA-0000-0000-0000-000000000001");
        var lid2 = Guid.Parse("BBBBBBBB-0000-0000-0000-000000000002");

        var req1 = MakeReq(rid);
        req1.Lines.Clear();
        req1.Lines.Add(new CreateZFOrderLine
            { RequestLineId = lid1, LineSeq = 1, ItemCode = "ITEM-A", RequestedQty = 1m, UnitPrice = 10m });
        req1.Lines.Add(new CreateZFOrderLine
            { RequestLineId = lid2, LineSeq = 2, ItemCode = "ITEM-B", RequestedQty = 2m, UnitPrice = 20m });

        var req2 = MakeReq(rid);
        req2.Lines.Clear();
        req2.Lines.Add(new CreateZFOrderLine
            { RequestLineId = lid2, LineSeq = 2, ItemCode = "ITEM-B", RequestedQty = 2m, UnitPrice = 20m });
        req2.Lines.Add(new CreateZFOrderLine
            { RequestLineId = lid1, LineSeq = 1, ItemCode = "ITEM-A", RequestedQty = 1m, UnitPrice = 10m });

        Assert.Equal(Hasher.Compute(req1), Hasher.Compute(req2));
    }

    // ── C-IDEM-05: Description/U_ItemName excluded from hash ──────────────────

    [Fact]
    public void C_IDEM_05_DisplayOnlyFields_ExcludedFrom_Hash()
    {
        var rid = Guid.NewGuid();
        var req1 = MakeReq(rid);
        var req2 = MakeReq(rid);
        req2.Lines[0] = new CreateZFOrderLine
        {
            RequestLineId  = req1.Lines[0].RequestLineId,
            LineSeq        = req1.Lines[0].LineSeq,
            ItemCode       = req1.Lines[0].ItemCode,
            RequestedQty   = req1.Lines[0].RequestedQty,
            UnitPrice      = req1.Lines[0].UnitPrice,
            Description    = "CHANGED DESCRIPTION",
            U_ItemName     = "CHANGED NAME",
            U_Manufacturer = "CHANGED MFG"
        };

        Assert.Equal(Hasher.Compute(req1), Hasher.Compute(req2));
    }

    // ── C-IDEM-06: Different RequestId → different U_ReplitId ─────────────────

    [Fact]
    public void C_IDEM_06_DifferentRequestId_DifferentReplitId()
    {
        string r1 = ZoneFulfillmentSapOrderService.BuildReplitId(Guid.NewGuid());
        string r2 = ZoneFulfillmentSapOrderService.BuildReplitId(Guid.NewGuid());
        Assert.NotEqual(r1, r2);
    }

    // ── C-IDEM-07: U_ReplitId is deterministic and within EditSize=50 ─────────

    [Fact]
    public void C_IDEM_07_ReplitId_Deterministic_And_LengthOk()
    {
        var rid   = Guid.Parse("12345678-9ABC-DEF0-1234-56789ABCDEF0");
        string r1 = ZoneFulfillmentSapOrderService.BuildReplitId(rid);
        string r2 = ZoneFulfillmentSapOrderService.BuildReplitId(rid);

        Assert.Equal(r1, r2);
        Assert.StartsWith("ZF-", r1);
        Assert.True(r1.Length <= 50, $"U_ReplitId length {r1.Length} exceeds EditSize=50");
        Assert.Equal(35, r1.Length);  // "ZF-" (3) + 32 hex chars
    }
}
