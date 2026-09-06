using SapReplitAPI.Models.Offline;

namespace SapReplitAPI.Services.Offline;

/// <summary>
/// SAP mutation adapter for V2 offline recovery.
///
/// ZERO SAP MUTATIONS GUARD:
///   All methods in this class constitute Phase E SAP mutations.
///   They are unreachable during engineering/testing because
///   OfflineFulfillmentOptions.Enabled defaults to false.
///   Do not implement these methods until Phase E is separately authorized.
///
/// When Phase E is authorized:
///   Implement each method using the existing ZF SAP services as a pattern.
///   Each method must be idempotent (idempotent = safe to retry on crash).
/// </summary>
public sealed class OfflineSapAdapter
{
    private readonly SapService _sap;
    private readonly ILogger<OfflineSapAdapter> _log;

    public OfflineSapAdapter(SapService sap, ILogger<OfflineSapAdapter> log)
    {
        _sap = sap;
        _log = log;
    }

    // ── Phase E stubs — NOT yet implemented ───────────────────────────────────
    // All methods throw NotImplementedException.
    // RecoveryService is unreachable when Enabled=false.

    /// <summary>Creates SAP ORDR from offline order header + confirmed lines.</summary>
    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryOrderAsync(
        OfflineFulfillmentOrder order, CancellationToken ct)
    {
        _log.LogError("[OFFLINE-V2-SAP] CreateOfflineRecoveryOrderAsync called but Phase E is not yet implemented.");
        throw new NotImplementedException("Phase E: CreateOfflineRecoveryOrderAsync not yet implemented.");
    }

    /// <summary>Creates SAP OPKL per warehouse group from confirmed picks.</summary>
    public Task<string?> CreateOfflineRecoveryPickListsAsync(
        int salesOrderDocEntry,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        _log.LogError("[OFFLINE-V2-SAP] CreateOfflineRecoveryPickListsAsync called but Phase E is not yet implemented.");
        throw new NotImplementedException("Phase E: CreateOfflineRecoveryPickListsAsync not yet implemented.");
    }

    /// <summary>
    /// Replays confirmed physical picks into SAP.
    /// Returns (error, reconciliationReasonCode) if SAP disagrees with physical truth.
    /// Physical truth is NEVER altered — mismatch → ReconciliationRequired.
    /// </summary>
    public Task<(string? Error, string? ReconciliationCode)> ReplayOfflinePicksAsync(
        int salesOrderDocEntry,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        _log.LogError("[OFFLINE-V2-SAP] ReplayOfflinePicksAsync called but Phase E is not yet implemented.");
        throw new NotImplementedException("Phase E: ReplayOfflinePicksAsync not yet implemented.");
    }

    /// <summary>Creates SAP ODLN from sales order after picks are replayed.</summary>
    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryDeliveryAsync(
        int salesOrderDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct)
    {
        _log.LogError("[OFFLINE-V2-SAP] CreateOfflineRecoveryDeliveryAsync called but Phase E is not yet implemented.");
        throw new NotImplementedException("Phase E: CreateOfflineRecoveryDeliveryAsync not yet implemented.");
    }

    /// <summary>Creates SAP OINV from delivery after delivery is confirmed.</summary>
    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryInvoiceAsync(
        int deliveryDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct)
    {
        _log.LogError("[OFFLINE-V2-SAP] CreateOfflineRecoveryInvoiceAsync called but Phase E is not yet implemented.");
        throw new NotImplementedException("Phase E: CreateOfflineRecoveryInvoiceAsync not yet implemented.");
    }
}
