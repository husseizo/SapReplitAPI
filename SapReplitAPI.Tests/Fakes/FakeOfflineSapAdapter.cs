using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;

namespace SapReplitAPI.Tests.Fakes;

/// <summary>
/// Deterministic fake adapter for Phase G tests.
///
/// Each stage can be independently configured via the Scenario* properties.
/// "AlreadyExists" simulates SAP-first verification returning an existing document
/// without creating a duplicate — proving the recovery service handles idempotency
/// at the SAP layer in addition to its own stage checkpointing.
/// </summary>
public sealed class FakeOfflineSapAdapter : IOfflineSapAdapter
{
    // ── Per-stage scenarios ───────────────────────────────────────────────────

    public FakeSapStageConfig OrderConfig      { get; set; } = FakeSapStageConfig.Success(10001, 10001);
    public FakeSapStageConfig PickListConfig   { get; set; } = FakeSapStageConfig.Success(0, 0);
    public FakeSapStageConfig PickReplayConfig { get; set; } = FakeSapStageConfig.PickSuccess();
    public FakeSapStageConfig DeliveryConfig   { get; set; } = FakeSapStageConfig.Success(20001, 20001);
    public FakeSapStageConfig InvoiceConfig    { get; set; } = FakeSapStageConfig.Success(30001, 30001);

    // ── Call-count tracking (verify idempotency) ──────────────────────────────

    public int OrderCallCount      { get; private set; }
    public int PickListCallCount   { get; private set; }
    public int PickReplayCallCount { get; private set; }
    public int DeliveryCallCount   { get; private set; }
    public int InvoiceCallCount    { get; private set; }

    // ── IOfflineSapAdapter ────────────────────────────────────────────────────

    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryOrderAsync(
        OfflineFulfillmentOrder order, CancellationToken ct)
    {
        OrderCallCount++;
        return Task.FromResult(Execute(OrderConfig));
    }

    public Task<string?> CreateOfflineRecoveryPickListsAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        PickListCallCount++;
        var (_, _, err) = Execute(PickListConfig);
        return Task.FromResult(err);
    }

    public Task<(string? Error, string? ReconciliationCode)> ReplayOfflinePicksAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        PickReplayCallCount++;
        if (PickReplayConfig.Scenario == FakeSapScenario.BinShortage)
            return Task.FromResult<(string?, string?)>(("SAP bin shortage", ReconciliationReasonCode.SapBinShortage));
        if (PickReplayConfig.Scenario == FakeSapScenario.BinNotFound)
            return Task.FromResult<(string?, string?)>(("Bin not found in SAP", ReconciliationReasonCode.BinNotFound));
        if (PickReplayConfig.Scenario == FakeSapScenario.Failure)
            return Task.FromResult<(string?, string?)>((PickReplayConfig.ErrorMessage, null));
        if (PickReplayConfig.Scenario == FakeSapScenario.ThrowException)
            throw new InvalidOperationException("Simulated crash in ReplayOfflinePicks");
        return Task.FromResult<(string?, string?)>((null, null)); // success
    }

    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryDeliveryAsync(
        int salesOrderDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct)
    {
        DeliveryCallCount++;
        return Task.FromResult(Execute(DeliveryConfig));
    }

    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryInvoiceAsync(
        int deliveryDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct)
    {
        InvoiceCallCount++;
        return Task.FromResult(Execute(InvoiceConfig));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (int, int, string?) Execute(FakeSapStageConfig cfg) =>
        cfg.Scenario switch
        {
            FakeSapScenario.Success or FakeSapScenario.AlreadyExists
                => (cfg.DocEntry, cfg.DocNum, null),
            FakeSapScenario.Failure
                => (0, 0, cfg.ErrorMessage ?? "SAP returned an error"),
            FakeSapScenario.CustomerInvalid
                => (0, 0, ReconciliationReasonCode.CustomerInvalid),
            FakeSapScenario.ThrowException
                => throw new InvalidOperationException("Simulated crash / unhandled SAP exception"),
            _ => (0, 0, "Unknown scenario")
        };
}

public sealed class FakeSapStageConfig
{
    public FakeSapScenario Scenario  { get; init; }
    public int             DocEntry  { get; init; }
    public int             DocNum    { get; init; }
    public string?         ErrorMessage { get; init; }

    public static FakeSapStageConfig Success(int docEntry, int docNum) =>
        new() { Scenario = FakeSapScenario.Success, DocEntry = docEntry, DocNum = docNum };

    /// <summary>
    /// SAP-first: SAP already has this document. Return existing DocEntry, no new doc created.
    /// </summary>
    public static FakeSapStageConfig AlreadyExists(int existingDocEntry, int existingDocNum) =>
        new() { Scenario = FakeSapScenario.AlreadyExists, DocEntry = existingDocEntry, DocNum = existingDocNum };

    public static FakeSapStageConfig Failure(string? msg = null) =>
        new() { Scenario = FakeSapScenario.Failure, ErrorMessage = msg ?? "SAP rejected the request" };

    public static FakeSapStageConfig CustomerInvalid() =>
        new() { Scenario = FakeSapScenario.CustomerInvalid };

    public static FakeSapStageConfig Crash() =>
        new() { Scenario = FakeSapScenario.ThrowException };

    public static FakeSapStageConfig PickSuccess() =>
        new() { Scenario = FakeSapScenario.Success };

    public static FakeSapStageConfig BinShortage() =>
        new() { Scenario = FakeSapScenario.BinShortage };

    public static FakeSapStageConfig BinNotFound() =>
        new() { Scenario = FakeSapScenario.BinNotFound };
}

public enum FakeSapScenario
{
    Success,
    AlreadyExists,
    Failure,
    ThrowException,
    BinShortage,
    BinNotFound,
    CustomerInvalid
}
