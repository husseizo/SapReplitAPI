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
///
/// PickListConfig scenarios:
///   Success / AlreadyExists         → (null, null) — OPKL stage succeeds
///   PickerMappingInvalid            → (error, PICKER_MAPPING_INVALID)
///   OpklFragmentMismatch            → (error, PICKLIST_FRAGMENT_MISMATCH)
///   Failure                         → (error, SAP_PREFLIGHT_FAILED)
///   ThrowException                  → throws
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

    public Task<(string? Error, string? ReconciliationCode)> CreateOfflineRecoveryPickListsAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        PickListCallCount++;
        if (PickListConfig.Scenario == FakeSapScenario.ThrowException)
            throw new InvalidOperationException("Simulated crash in CreateOfflineRecoveryPickLists");

        (string? error, string? code) = PickListConfig.Scenario switch
        {
            FakeSapScenario.Success or FakeSapScenario.AlreadyExists
                => (null, null),
            FakeSapScenario.PickerMappingInvalid
                => (PickListConfig.ErrorMessage ?? "No picker configured for warehouse",
                    ReconciliationReasonCode.PickerMappingInvalid),
            FakeSapScenario.OpklFragmentMismatch
                => (PickListConfig.ErrorMessage ?? "Existing OPKL has incomplete line set",
                    ReconciliationReasonCode.PicklistFragmentMismatch),
            FakeSapScenario.Failure
                => (PickListConfig.ErrorMessage ?? "SAP rejected the request",
                    ReconciliationReasonCode.SapPreflightFailed),
            _ => ("Unknown scenario", null)
        };
        return Task.FromResult((error, code));
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

    /// <summary>SAP-first: existing document found; return without creating.</summary>
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

    /// <summary>No valid picker configured for the warehouse → ReconciliationRequired PICKER_MAPPING_INVALID.</summary>
    public static FakeSapStageConfig PickerMappingInvalid(string? msg = null) =>
        new() { Scenario = FakeSapScenario.PickerMappingInvalid, ErrorMessage = msg };

    /// <summary>Existing OPKL found but has wrong/partial line set → ReconciliationRequired PICKLIST_FRAGMENT_MISMATCH.</summary>
    public static FakeSapStageConfig OpklFragmentMismatch(string? msg = null) =>
        new() { Scenario = FakeSapScenario.OpklFragmentMismatch, ErrorMessage = msg };
}

public enum FakeSapScenario
{
    Success,
    AlreadyExists,
    Failure,
    ThrowException,
    BinShortage,
    BinNotFound,
    CustomerInvalid,
    PickerMappingInvalid,
    OpklFragmentMismatch
}
