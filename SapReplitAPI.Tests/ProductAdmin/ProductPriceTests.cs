using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapReplitAPI.Filters;
using SapReplitAPI.Models;
using SapReplitAPI.Models.ProductAdmin;
using SapReplitAPI.Services.ProductAdmin;
using Xunit;

namespace SapReplitAPI.Tests.ProductAdmin;

/// <summary>
/// PA01–PA45: Product Price Administration — Phase 1.
/// All tests are in-memory: no SQL, no SAP COM, no production mutations.
/// Uses FakeSapPriceAdapter, FakeAuditRepository, TestableZfProductAdminService.
/// </summary>
public sealed class ProductPriceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static TestableZfProductAdminService MakeService(
        FakeSapPriceAdapter? sap = null,
        FakeAuditRepository? audit = null,
        string sqliteResult = "OK",
        string neonResult = "OK")
    {
        sap ??= new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        audit ??= new FakeAuditRepository();
        return new TestableZfProductAdminService(sap, audit, NullLogger<ZfProductAdminService>.Instance)
        {
            SqliteResult = sqliteResult,
            NeonResult   = neonResult,
        };
    }

    private static UpdateProductPriceRequest Req(
        decimal price = 150m, decimal? expected = null,
        string by = "tester", string reason = "unit-test") =>
        new() { Price = price, ExpectedCurrentPrice = expected, RequestedBy = by, Reason = reason };

    // ── PA01–PA03: ZfAdminKeyAuthFilter ──────────────────────────────────────

    [Fact]
    public async Task PA01_ZfAdminFilter_NullOrEmptyConfig_Returns401()
    {
        var filter = new ZfAdminKeyAuthFilter(
            Options.Create(new ZfAdminSettings { AdminApiKey = string.Empty }));
        var ctx = MakeActionContext();
        await filter.OnActionExecutionAsync(ctx,
            () => throw new InvalidOperationException("should not reach next()"));
        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task PA02_ZfAdminFilter_WrongKey_Returns401()
    {
        var filter = new ZfAdminKeyAuthFilter(
            Options.Create(new ZfAdminSettings { AdminApiKey = "correct-key" }));
        var ctx = MakeActionContext(adminKey: "wrong-key");
        await filter.OnActionExecutionAsync(ctx,
            () => throw new InvalidOperationException("should not reach next()"));
        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task PA03_ZfAdminFilter_CorrectKey_PassesToNext()
    {
        var filter = new ZfAdminKeyAuthFilter(
            Options.Create(new ZfAdminSettings { AdminApiKey = "correct-key" }));
        bool nextCalled = false;
        var ctx = MakeActionContext(adminKey: "correct-key");
        await filter.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), null!));
        });
        Assert.True(nextCalled);
    }

    // ── PA04–PA05: ApiKeyAuthFilter ───────────────────────────────────────────

    [Fact]
    public async Task PA04_ApiKeyFilter_MissingKey_Returns401()
    {
        var filter = new ApiKeyAuthFilter(
            Options.Create(new ApiSecuritySettings { ApiKey = "op-key" }));
        var ctx = MakeActionContext(); // no X-API-Key header
        await filter.OnActionExecutionAsync(ctx,
            () => throw new InvalidOperationException("should not reach next()"));
        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task PA05_ApiKeyFilter_CorrectKey_PassesToNext()
    {
        var filter = new ApiKeyAuthFilter(
            Options.Create(new ApiSecuritySettings { ApiKey = "op-key" }));
        bool nextCalled = false;
        var ctx = MakeActionContext(apiKey: "op-key");
        await filter.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), null!));
        });
        Assert.True(nextCalled);
    }

    // ── PA06–PA10: PriceListNum validation ────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task PA06_ValidPriceLists_PL1Through5_NotValidationFailed(int pl)
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var svc = MakeService(sap);
        var result = await svc.UpdatePriceAsync("ITEM001", pl, Req());
        Assert.NotEqual(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task PA07_InvalidPriceLists_PL0_PL6_ReturnValidationFailed(int pl)
    {
        var svc = MakeService();
        var result = await svc.UpdatePriceAsync("ITEM001", pl, Req());
        Assert.Equal(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    // ── PA11–PA15: Field validation ────────────────────────────────────────────

    [Fact]
    public async Task PA11_EmptyItemCode_Returns_ValidationFailed()
    {
        var result = await MakeService().UpdatePriceAsync("", 1, Req());
        Assert.Equal(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA12_NegativePrice_Returns_ValidationFailed()
    {
        var result = await MakeService().UpdatePriceAsync("ITEM001", 1, Req(price: -0.01m));
        Assert.Equal(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA13_ZeroPrice_IsValid_FreePriceItem()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 0m, Currency = "TZS" }, UpdateActual = 0m };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(price: 0m));
        Assert.NotEqual(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA14_EmptyRequestedBy_Returns_ValidationFailed()
    {
        var result = await MakeService().UpdatePriceAsync("ITEM001", 1, Req(by: ""));
        Assert.Equal(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA15_PriceAboveMax_Returns_ValidationFailed()
    {
        var result = await MakeService().UpdatePriceAsync("ITEM001", 1, Req(price: 1_000_000_000m));
        Assert.Equal(PriceUpdateResult.ValidationFailed, result.ResultCode);
    }

    // ── PA16–PA18: Concurrency ────────────────────────────────────────────────

    [Fact]
    public async Task PA16_ExpectedPriceMismatch_Returns_ConcurrencyConflict()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(expected: 50m));
        Assert.Equal(PriceUpdateResult.ConcurrencyConflict, result.ResultCode);
    }

    [Fact]
    public async Task PA17_ExpectedPriceWithinTolerance_Proceeds()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateActual = 150m,
        };
        // expected=100.003 vs actual=100.000 — within 0.005 tolerance
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(expected: 100.003m));
        Assert.NotEqual(PriceUpdateResult.ConcurrencyConflict, result.ResultCode);
    }

    [Fact]
    public async Task PA18_NoExpectedPrice_SkipsConcurrencyCheck()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateActual = 150m,
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(expected: null));
        Assert.NotEqual(PriceUpdateResult.ConcurrencyConflict, result.ResultCode);
    }

    // ── PA19–PA20: SAP read errors ────────────────────────────────────────────

    [Fact]
    public async Task PA19_SapReadReturnsNull_Returns_PriceListNotFound()
    {
        var sap = new FakeSapPriceAdapter { ReadResult = null };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req());
        Assert.Equal(PriceUpdateResult.PriceListNotFound, result.ResultCode);
    }

    [Fact]
    public async Task PA20_SapReadThrows_Returns_SapUpdateFailed()
    {
        var sap = new FakeSapPriceAdapter { ReadThrows = true };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req());
        Assert.Equal(PriceUpdateResult.SapUpdateFailed, result.ResultCode);
    }

    // ── PA21–PA25: SAP write + readback ──────────────────────────────────────

    [Fact]
    public async Task PA21_SapWriteSucceeds_ReadbackMatches_Returns_Success()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateActual = 150m,
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.Success, result.ResultCode);
        Assert.True(result.SapUpdated);
        Assert.Equal(150m, result.ActualSapPrice);
    }

    [Fact]
    public async Task PA22_SapWriteReturnsErrorCode_Returns_SapUpdateFailed()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateRc    = -1,
            UpdateError = "license expired",
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.SapUpdateFailed, result.ResultCode);
        Assert.False(result.SapUpdated);
    }

    [Fact]
    public async Task PA23_SapWriteThrows_Returns_SapUpdateFailed()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult   = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateThrows = true,
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.SapUpdateFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA24_SapWriteRc0_ReadbackMismatch_Returns_SapWriteVerificationFailed()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateRc    = 0,
            UpdateActual = 99m,   // requested 150 — got back 99
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.SapWriteVerificationFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA25_SapWriteRc0_ReadbackNull_Returns_SapWriteVerificationFailed()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateRc    = 0,
            UpdateActual = null,  // readback row vanished
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.SapWriteVerificationFailed, result.ResultCode);
    }

    // ── PA26–PA28: CacheSyncWarning ───────────────────────────────────────────

    [Fact]
    public async Task PA26_SapOk_SqliteFails_Returns_CacheSyncWarning()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var result = await MakeService(sap, sqliteResult: "FAILED").UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.CacheSyncWarning, result.ResultCode);
        Assert.True(result.SapUpdated);
    }

    [Fact]
    public async Task PA27_SapOk_NeonFails_Returns_CacheSyncWarning()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var result = await MakeService(sap, neonResult: "FAILED").UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.CacheSyncWarning, result.ResultCode);
    }

    [Fact]
    public async Task PA28_SapOk_BothCacheFail_Returns_CacheSyncWarning()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var result = await MakeService(sap, sqliteResult: "FAILED", neonResult: "FAILED")
            .UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.CacheSyncWarning, result.ResultCode);
    }

    // ── PA29–PA30: Success path ───────────────────────────────────────────────

    [Fact]
    public async Task PA29_SuccessPath_OldAndActualPricePreserved()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(100m, result.OldPrice);
        Assert.Equal(150m, result.ActualSapPrice);
        Assert.Equal("TZS", result.Currency);
    }

    [Fact]
    public async Task PA30_SuccessPath_BothCacheUpdatedFlagsTrue()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.True(result.SqliteUpdated);
        Assert.True(result.NeonUpdated);
    }

    // ── PA31–PA35: Bulk idempotency ───────────────────────────────────────────

    [Fact]
    public async Task PA31_Bulk_SuccessReplay_Returns_AlreadyCompleted_SapNotCalled()
    {
        var audit = new FakeAuditRepository();
        audit.SeedCompleted(Guid.Parse("11111111-0000-0000-0000-000000000001"),
            "ITEM001", 1, PriceUpdateResult.Success, actualPriceAfter: 150m);

        var sap = new FakeSapPriceAdapter { ReadResult = null }; // would fail if called
        var svc = MakeService(sap, audit);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId  = Guid.Parse("11111111-0000-0000-0000-000000000001"),
            RequestedBy = "tester",
            Updates    = new List<BulkPriceUpdateItem>
                { new() { ItemCode = "ITEM001", PriceListNum = 1, Price = 150m } },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(PriceUpdateResult.AlreadyCompleted, result.Results[0].ResultCode);
        Assert.Equal(0, sap.ReadCallCount); // SAP NOT called
    }

    [Fact]
    public async Task PA32_Bulk_CacheSyncWarningReplay_Returns_AlreadyCompleted_SapNotCalled()
    {
        var audit = new FakeAuditRepository();
        audit.SeedCompleted(Guid.Parse("22222222-0000-0000-0000-000000000001"),
            "ITEM002", 2, PriceUpdateResult.CacheSyncWarning, actualPriceAfter: 200m);

        var sap = new FakeSapPriceAdapter { ReadResult = null };
        var svc = MakeService(sap, audit);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId  = Guid.Parse("22222222-0000-0000-0000-000000000001"),
            RequestedBy = "tester",
            Updates    = new List<BulkPriceUpdateItem>
                { new() { ItemCode = "ITEM002", PriceListNum = 2, Price = 200m } },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(PriceUpdateResult.AlreadyCompleted, result.Results[0].ResultCode);
        Assert.Equal(0, sap.ReadCallCount); // SAP NOT called
    }

    [Fact]
    public async Task PA33_Bulk_VerificationFailedReplay_Returns_VerificationFailed_SapNotCalled()
    {
        var audit = new FakeAuditRepository();
        audit.SeedCompleted(Guid.Parse("33333333-0000-0000-0000-000000000001"),
            "ITEM003", 1, PriceUpdateResult.SapWriteVerificationFailed, actualPriceAfter: null);

        var sap = new FakeSapPriceAdapter { ReadResult = null };
        var svc = MakeService(sap, audit);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId  = Guid.Parse("33333333-0000-0000-0000-000000000001"),
            RequestedBy = "tester",
            Updates    = new List<BulkPriceUpdateItem>
                { new() { ItemCode = "ITEM003", PriceListNum = 1, Price = 150m } },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(PriceUpdateResult.SapWriteVerificationFailed, result.Results[0].ResultCode);
        Assert.Equal(0, sap.ReadCallCount); // SAP NOT called
    }

    [Fact]
    public async Task PA34_Bulk_ValidationFailedReplay_AllowsRetry_SapCalled()
    {
        var audit = new FakeAuditRepository();
        // Prior run had validation failure — SAP was NOT mutated — retry is safe
        audit.SeedCompleted(Guid.Parse("44444444-0000-0000-0000-000000000001"),
            "ITEM004", 1, PriceUpdateResult.ValidationFailed, actualPriceAfter: null);

        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateActual = 150m,
        };
        var svc = MakeService(sap, audit);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId  = Guid.Parse("44444444-0000-0000-0000-000000000001"),
            RequestedBy = "tester",
            Updates    = new List<BulkPriceUpdateItem>
                { new() { ItemCode = "ITEM004", PriceListNum = 1, Price = 150m } },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(PriceUpdateResult.Success, result.Results[0].ResultCode);
        Assert.True(sap.ReadCallCount > 0); // SAP WAS called
    }

    [Fact]
    public async Task PA35_BulkTwoSucceeds_CountsCorrect()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var svc = MakeService(sap);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId  = Guid.NewGuid(),
            RequestedBy = "tester",
            Updates    = new List<BulkPriceUpdateItem>
            {
                new() { ItemCode = "ITEM001", PriceListNum = 1, Price = 150m },
                new() { ItemCode = "ITEM002", PriceListNum = 2, Price = 150m }, // same price as UpdateActual
            },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);
    }

    // ── PA36–PA40: Audit entries ──────────────────────────────────────────────

    [Fact]
    public async Task PA36_AuditEntry_InsertedBeforeSapWrite()
    {
        var audit = new FakeAuditRepository();
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        await MakeService(sap, audit).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.NotEmpty(audit.Entries);
    }

    [Fact]
    public async Task PA37_AuditTerminal_SuccessResult_Recorded()
    {
        var audit = new FakeAuditRepository();
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        await MakeService(sap, audit).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.Success, audit.Entries.Single().Result);
    }

    [Fact]
    public async Task PA38_AuditTerminal_SapUpdateFailed_Recorded()
    {
        var audit = new FakeAuditRepository();
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateRc = -1 };
        await MakeService(sap, audit).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.SapUpdateFailed, audit.Entries.Single().Result);
    }

    [Fact]
    public async Task PA39_AuditTerminal_SapWriteVerificationFailed_Recorded()
    {
        var audit = new FakeAuditRepository();
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateRc    = 0,
            UpdateActual = 99m,  // mismatch
        };
        await MakeService(sap, audit).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.Equal(PriceUpdateResult.SapWriteVerificationFailed, audit.Entries.Single().Result);
    }

    [Fact]
    public async Task PA40_AuditId_ReturnedInResponse_MatchesEntry()
    {
        var audit = new FakeAuditRepository();
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" }, UpdateActual = 150m };
        var result = await MakeService(sap, audit).UpdatePriceAsync("ITEM001", 1, Req(150m));
        Assert.True(result.AuditId > 0);
        Assert.Equal(result.AuditId, audit.Entries.Single().Id);
    }

    // ── PA41–PA45: ISapPriceAdapter abstraction ───────────────────────────────

    [Fact]
    public async Task PA41_FakeAdapter_AllowsTestsWithoutLiveSap()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult     = new SapCurrentPrice { Price = 50m, Currency = "USD" },
            UpdateActual   = 75m,
            UpdateCurrency = "USD",  // must match read currency
        };
        var result = await MakeService(sap).UpdatePriceAsync("TEST", 3, Req(75m));
        Assert.Equal(PriceUpdateResult.Success, result.ResultCode);
        Assert.Equal("USD", result.Currency);
    }

    [Fact]
    public void PA42_FakeAdapter_ReturnsConfiguredPrice()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 999.99m, Currency = "TZS" } };
        var price = sap.ReadItemPrice("ANY", 1);
        Assert.Equal(999.99m, price!.Price);
    }

    [Fact]
    public void PA43_FakeAdapter_SimulatesWriteFailure()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateRc    = -5,
            UpdateError = "locked",
        };
        var (rc, err, _, _) = sap.UpdateItemPrice("ITEM", 1, 200m);
        Assert.Equal(-5, rc);
        Assert.Equal("locked", err);
    }

    [Fact]
    public async Task PA44_FakeAdapter_SimulatesReadbackMismatch_LeadsToVerificationFailed()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult  = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateActual = 50m,   // requested 200 — clearly different
        };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req(200m));
        Assert.Equal(PriceUpdateResult.SapWriteVerificationFailed, result.ResultCode);
    }

    [Fact]
    public async Task PA45_FakeAdapter_ReadThrows_ServiceHandlesGracefully()
    {
        var sap = new FakeSapPriceAdapter { ReadThrows = true };
        var result = await MakeService(sap).UpdatePriceAsync("ITEM001", 1, Req());
        // Service returns SapUpdateFailed — does not propagate the exception.
        Assert.Equal(PriceUpdateResult.SapUpdateFailed, result.ResultCode);
    }

    // ── ActionExecutingContext factory ────────────────────────────────────────

    private static ActionExecutingContext MakeActionContext(
        string? adminKey = null, string? apiKey = null)
    {
        var httpCtx = new DefaultHttpContext();
        if (adminKey is not null) httpCtx.Request.Headers["X-ZF-Admin-Key"] = adminKey;
        if (apiKey is not null)   httpCtx.Request.Headers["X-API-Key"]       = apiKey;

        var actionCtx = new ActionContext(
            httpCtx,
            new RouteData(),
            new ActionDescriptor());

        return new ActionExecutingContext(
            actionCtx,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: null!);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

public sealed class FakeSapPriceAdapter : ISapPriceAdapter
{
    public SapCurrentPrice? ReadResult   { get; set; }
    public bool             ReadThrows   { get; set; }
    public int              ReadCallCount { get; private set; }

    public int     UpdateRc        { get; set; } = 0;
    public string  UpdateError     { get; set; } = string.Empty;
    public decimal? UpdateActual   { get; set; }
    public string  UpdateCurrency  { get; set; } = "TZS";
    public bool    UpdateThrows    { get; set; }
    public int     UpdateCallCount { get; private set; }

    public SapCurrentPrice? ReadItemPrice(string itemCode, int priceListNum)
    {
        ReadCallCount++;
        if (ReadThrows) throw new InvalidOperationException("Fake SAP not available.");
        return ReadResult;
    }

    public (int rc, string sapError, decimal? actualPriceAfter, string currency) UpdateItemPrice(
        string itemCode, int priceListNum, decimal newPrice)
    {
        UpdateCallCount++;
        if (UpdateThrows) throw new InvalidOperationException("Fake SAP write failed.");
        return (UpdateRc, UpdateError, UpdateActual, UpdateCurrency);
    }
}

public sealed class FakeAuditRepository : ProductPriceAuditRepository
{
    private long _nextId = 1;
    private readonly List<ProductPriceAuditEntry> _seeded = new();

    public List<ProductPriceAuditEntry> Entries { get; } = new();

    public void SeedCompleted(Guid batchId, string itemCode, int priceListNum,
        string result, decimal? actualPriceAfter)
    {
        _seeded.Add(new ProductPriceAuditEntry
        {
            Id             = _nextId++,
            BatchRequestId = batchId,
            ItemCode       = itemCode,
            PriceListNum   = priceListNum,
            Result         = result,
            ActualPriceAfter = actualPriceAfter,
            RequestedAtUtc = DateTime.UtcNow,
        });
    }

    public override Task<long> InsertAsync(
        ProductPriceAuditEntry entry, CancellationToken ct = default)
    {
        entry.Id     = _nextId++;
        entry.Result = "Pending";
        Entries.Add(entry);
        return Task.FromResult(entry.Id);
    }

    public override Task SetTerminalAsync(
        long id, string result, decimal? actualPriceAfter,
        int? sapErrorCode, string? sapErrorMessage,
        string? sqliteSyncResult, string? neonSyncResult,
        DateTime executedAtUtc, CancellationToken ct = default,
        decimal? oldPrice = null, string? currency = null)
    {
        var e = Entries.Find(e => e.Id == id);
        if (e is not null)
        {
            e.Result          = result;
            e.ActualPriceAfter = actualPriceAfter;
            if (oldPrice is not null) e.OldPrice = oldPrice;
            if (currency is not null) e.Currency = currency;
            e.SapErrorCode    = sapErrorCode;
            e.SapErrorMessage = sapErrorMessage;
            e.SqliteSyncResult = sqliteSyncResult;
            e.NeonSyncResult  = neonSyncResult;
            e.ExecutedAtUtc   = executedAtUtc;
        }
        return Task.CompletedTask;
    }

    public override Task<ProductPriceAuditEntry?> FindCompletedAsync(
        Guid batchRequestId, string itemCode, int priceListNum,
        CancellationToken ct = default)
    {
        var found = _seeded.FirstOrDefault(e =>
            e.BatchRequestId == batchRequestId &&
            string.Equals(e.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase) &&
            e.PriceListNum == priceListNum &&
            e.Result != "Pending");
        return Task.FromResult(found);
    }
}

public sealed class TestableZfProductAdminService : ZfProductAdminService
{
    public string SqliteResult { get; set; } = "OK";
    public string NeonResult   { get; set; } = "OK";

    public TestableZfProductAdminService(
        ISapPriceAdapter sap,
        ProductPriceAuditRepository audit,
        ILogger<ZfProductAdminService> log)
        : base(sap, null!, null!, audit, null!, log) { }

    protected override Task<string> UpdateSqlitePriceAsync(
        string itemCode, int priceListNum, decimal price, CancellationToken ct)
        => Task.FromResult(SqliteResult);

    protected override Task<string> UpdateNeonPriceAsync(
        string itemCode, int priceListNum, decimal price, CancellationToken ct)
        => Task.FromResult(NeonResult);
}
