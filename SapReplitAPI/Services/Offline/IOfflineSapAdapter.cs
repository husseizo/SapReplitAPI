using SapReplitAPI.Models.Offline;

namespace SapReplitAPI.Services.Offline;

/// <summary>
/// SAP mutation contract for V2 offline recovery.
///
/// SAP-FIRST CONTRACT:
///   Every Create* method must check whether the SAP document already exists
///   (keyed on OfflineId UDF or BaseDoc reference) before creating a new one.
///   Return the existing DocEntry when found. NEVER duplicate a SAP document.
///
/// PHYSICAL TRUTH CONTRACT:
///   ReplayOfflinePicksAsync must validate that SAP's actual bin availability
///   matches the recorded physical picks. Disagreement → return ReconciliationCode.
///   The adapter MUST NOT silently reallocate to a different bin.
/// </summary>
public interface IOfflineSapAdapter
{
    /// <summary>
    /// SAP-first: checks for existing ORDR with matching OfflineId UDF before creating.
    /// Returns (DocEntry, DocNum, null) on success or existing-doc found.
    /// Returns (0, 0, errorMessage) on SAP rejection or connectivity failure.
    /// </summary>
    Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryOrderAsync(
        OfflineFulfillmentOrder order, CancellationToken ct);

    /// <summary>
    /// SAP-first per warehouse: checks for existing OPKL per WhsCode group before creating.
    /// One OPKL is created per unique warehouse. Returns null on success, error string on failure.
    /// </summary>
    Task<string?> CreateOfflineRecoveryPickListsAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct);

    /// <summary>
    /// Replays confirmed physical picks into SAP using desired-state semantics.
    /// If SAP bin availability disagrees with recorded physical truth →
    ///   return (errorMessage, ReconciliationReasonCode).
    /// MUST NOT silently reallocate to a different bin.
    /// </summary>
    Task<(string? Error, string? ReconciliationCode)> ReplayOfflinePicksAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct);

    /// <summary>
    /// SAP-first: checks for existing ODLN linked to SalesOrderDocEntry before creating.
    /// Returns (DocEntry, DocNum, null) on success or existing-doc found.
    /// </summary>
    Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryDeliveryAsync(
        int salesOrderDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct);

    /// <summary>
    /// SAP-first: checks for existing OINV linked to DeliveryDocEntry before creating.
    /// Returns (DocEntry, DocNum, null) on success or existing-doc found.
    /// </summary>
    Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryInvoiceAsync(
        int deliveryDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct);
}
