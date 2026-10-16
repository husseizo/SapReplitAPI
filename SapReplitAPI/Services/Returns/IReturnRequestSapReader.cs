using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services.Returns;

/// <summary>
/// Narrow seam around the three SAP reads the Return Request reconciliation safety net needs.
/// Exists ONLY to make ReturnRequestReconciliationService's orchestration (batch limits, time
/// budgets, cursor advancement, partial failures, restart recovery) testable end-to-end without a
/// live SAP DI API connection — SapService itself is NOT modified, has no other consumer routed
/// through this interface, and the real event-driven path (ReturnRequestEventHandler) continues
/// to call SapService directly, unaffected by this abstraction's existence.
/// </summary>
public interface IReturnRequestSapReader
{
    /// <summary>Candidate ORRR DocEntries changed since <paramref name="since"/> — see SapService.GetReturnRequestDocEntriesChangedSince.</summary>
    List<int> GetChangedDocEntries(DateTime since);

    /// <summary>Every ORRR DocEntry that has ever existed — see SapService.GetReturnRequestIdsForBackfill. Used only by the historical rotation job, never by the incremental job.</summary>
    List<int> GetAllDocEntries();

    /// <summary>Full current snapshot for one ORRR — see SapService.GetReturnRequestSnapshotByDocEntry. Null if the document no longer exists.</summary>
    (CachedReturnRequest header, List<CachedReturnRequestLine> lines)? GetSnapshot(int docEntry);
}

/// <summary>Real implementation — a thin pass-through to the existing, unmodified SapService.</summary>
public sealed class SapReturnRequestReader(SapService sap) : IReturnRequestSapReader
{
    public List<int> GetChangedDocEntries(DateTime since) => sap.GetReturnRequestDocEntriesChangedSince(since);
    public List<int> GetAllDocEntries() => sap.GetReturnRequestIdsForBackfill();
    public (CachedReturnRequest header, List<CachedReturnRequestLine> lines)? GetSnapshot(int docEntry)
        => sap.GetReturnRequestSnapshotByDocEntry(docEntry);
}
