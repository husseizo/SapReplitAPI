using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// C10 + C11: Thin wrapper around SapService's zone-fulfillment SAP methods.
/// Provides fragment ordering, U_ReplitId construction, and RDR1 reconciliation.
/// All COM operations are delegated to SapService (the canonical COM gateway).
/// Registered Scoped.
/// </summary>
public sealed class ZoneFulfillmentSapOrderService
{
    private readonly SapService _sap;
    private readonly ILogger<ZoneFulfillmentSapOrderService> _log;

    public ZoneFulfillmentSapOrderService(SapService sap, ILogger<ZoneFulfillmentSapOrderService> log)
    {
        _sap = sap;
        _log = log;
    }

    // ── U_ReplitId ─────────────────────────────────────────────────────────────

    public static string BuildReplitId(Guid requestId) =>
        "ZF-" + requestId.ToString("N").ToUpperInvariant();   // 35 chars ≤ EditSize=50

    // ── Idempotency: check for existing ORDR ───────────────────────────────────

    public (int DocEntry, int DocNum)? FindExistingOrder(string uReplitId) =>
        _sap.FindZoneFulfillmentOrder(uReplitId);

    // ── Create SO ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sorts fragments into insertion order (LineSeq ASC then WhsCode priority ASC),
    /// then calls SapService.CreateZoneFulfillmentOrder.
    /// Returns a SapSoResult with DocEntry, DocNum, and verified RDR1 lines.
    /// Throws SapOrderAddException on SAP-definitive failure (rc != 0 from Add()).
    /// Any other exception → UnknownOutcome (caller handles).
    /// </summary>
    public SapSoResult CreateSalesOrder(
        Guid requestId,
        string cardCode,
        DateTime docDate,
        DateTime deliveryDate,
        int? slpCode,
        IReadOnlyList<AllocationFragment> fragments,
        IReadOnlyList<DomainRequestLine>  requestLines,
        IReadOnlyList<ZoneWarehouse>      zone)
    {
        string uReplitId = BuildReplitId(requestId);

        // Deterministic insertion order: LineSeq (from requestLines), then zone priority
        var lineSeqById = requestLines.ToDictionary(l => l.RequestLineId, l => l.LineSeq);
        var whsPriority = zone.ToDictionary(w => w.WhsCode, w => w.Priority);

        var ordered = fragments
            .OrderBy(f => lineSeqById.TryGetValue(f.RequestLineId, out int seq) ? seq : int.MaxValue)
            .ThenBy(f => whsPriority.TryGetValue(f.WhsCode, out int p) ? p : int.MaxValue)
            .ToList();

        _log.LogInformation(
            "[ZF-SapOrder] Creating ORDR uReplitId={ReplitId} cardCode={Card} lines={N}",
            uReplitId, cardCode, ordered.Count);

        var (docEntry, docNum, rdr1Lines) = _sap.CreateZoneFulfillmentOrder(
            cardCode, docDate, deliveryDate, slpCode, uReplitId, ordered, requestLines);

        return new SapSoResult
        {
            DocEntry = docEntry,
            DocNum   = docNum,
            Lines    = rdr1Lines
        };
    }

    // ── C11: RDR1 reconciliation → SoLineFragmentRecords ─────────────────────

    /// <summary>
    /// Maps RDR1 lines (by LineNum order = insertion order) back to AllocationFragments.
    /// The orderedFragments list must be the exact same list passed to CreateSalesOrder.
    /// Same-item/same-WHS/different RequestLines are disambiguated by insertion order.
    /// </summary>
    public static List<SoLineFragmentRecord> ReconcileRdr1(
        long orchestrationId,
        long allocationPlanId,
        int  docEntry,
        IReadOnlyList<AllocationFragment> orderedFragments,
        IReadOnlyList<Rdr1Line>           rdr1Lines)
    {
        if (orderedFragments.Count != rdr1Lines.Count)
            throw new InvalidOperationException(
                $"RDR1 line count mismatch: expected {orderedFragments.Count}, " +
                $"got {rdr1Lines.Count} for DocEntry={docEntry}.");

        var result = new List<SoLineFragmentRecord>(orderedFragments.Count);

        for (int i = 0; i < orderedFragments.Count; i++)
        {
            var frag = orderedFragments[i];
            var rdr1 = rdr1Lines[i];

            result.Add(new SoLineFragmentRecord
            {
                OrchestrationId  = orchestrationId,
                RequestLineId    = frag.RequestLineId,
                AllocationPlanId = allocationPlanId,
                SoDocEntry       = docEntry,
                SoLineNum        = rdr1.LineNum,
                ItemCode         = rdr1.ItemCode,
                WhsCode          = rdr1.WhsCode,
                SoLineQty        = frag.SoLineQty,
                AllocatedQty     = frag.AllocatedQty,
                UnallocatedQty   = frag.UnallocatedQty,
                ReleasedQty      = 0m,
                DeliveredQty     = 0m
            });
        }

        return result;
    }
}

/// <summary>SAP returned rc != 0 from ORDR.Add() — definitive failure (not unknown outcome).</summary>
public sealed class SapOrderAddException(int rc, string sapError)
    : Exception($"SAP ORDR.Add() rc={rc}: {sapError}")
{
    public int    Rc       { get; } = rc;
    public string SapError { get; } = sapError;
}
