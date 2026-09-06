using SapReplitAPI.Models.Offline;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Services.Offline;

/// <summary>
/// SAP mutation adapter for V2 offline recovery.
///
/// SAP-FIRST CONTRACT:
///   Every Create* method checks SAP for an existing document before creating.
///   On crash/restart, the existing document is returned without duplication.
///
/// PHYSICAL TRUTH CONTRACT:
///   ReplayOfflinePicksAsync uses confirmed IsConfirmed picks as authoritative truth.
///   SAP bin disagreement → ReconciliationRequired. No silent reallocation.
///
/// ZERO SAP MUTATIONS GUARD:
///   All SAP mutations are unreachable when OfflineFulfillmentOptions.Enabled=false.
/// </summary>
public sealed class OfflineSapAdapter : IOfflineSapAdapter
{
    private readonly SapService               _sap;
    private readonly PickerResolutionService  _picker;
    private readonly ILogger<OfflineSapAdapter> _log;

    public OfflineSapAdapter(
        SapService                      sap,
        PickerResolutionService         picker,
        ILogger<OfflineSapAdapter>      log)
    {
        _sap    = sap;
        _picker = picker;
        _log    = log;
    }

    // ── U_ReplitId convention for offline V2 ─────────────────────────────────
    // "OF-{OfflineId:N}" — distinguishable from ZF's "ZF-{requestId:N}"
    private static string BuildUReplitId(Guid offlineId) =>
        "OF-" + offlineId.ToString("N").ToUpperInvariant();

    // ── 1. ORDR ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SAP-first: returns existing ORDR if found, else creates new one.
    /// Groups confirmed picks by (ItemCode, WhsCode) — one ORDR line per group.
    /// </summary>
    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryOrderAsync(
        OfflineFulfillmentOrder order, CancellationToken ct)
    {
        var uReplitId = BuildUReplitId(order.OfflineId);

        // SAP-first check
        var existing = _sap.FindZoneFulfillmentOrder(uReplitId);
        if (existing.HasValue)
        {
            _log.LogInformation(
                "[OF-V2-SAP] ORDR SAP-first hit DocEntry={De} uReplitId={Rid} — skipping create",
                existing.Value.DocEntry, uReplitId);
            return Task.FromResult<(int, int, string?)>((existing.Value.DocEntry, existing.Value.DocNum, null));
        }

        var confirmedPicks = order.Picks.Where(p => p.IsConfirmed).ToList();
        var lineById       = order.Lines.ToDictionary(l => l.RequestedLineId);

        // One ORDR line per (RequestedLineId, WhsCode): qty = sum of PickedQty for that group.
        // Multiple bins within same (requestedLine, whs) are collapsed — SAP sees qty only at ORDR level.
        var groups = confirmedPicks
            .GroupBy(p => (p.RequestedLineId, p.WhsCode))
            .Select(g =>
            {
                var pick    = g.First();
                lineById.TryGetValue(g.Key.RequestedLineId, out var lineInfo);
                return (
                    ItemCode:      pick.ItemCode,
                    WhsCode:       g.Key.WhsCode,
                    Qty:           g.Sum(p => p.PickedQty),
                    UnitPrice:     lineInfo?.UnitPrice ?? 0m,
                    Description:   lineInfo?.Description,
                    U_ItemName:    lineInfo?.U_ItemName,
                    U_Manufacturer: lineInfo?.U_Manufacturer
                );
            })
            .ToList();

        if (groups.Count == 0)
        {
            _log.LogWarning("[OF-V2-SAP] ORDR: no line groups from confirmed picks for OrderId={Id}", order.Id);
            return Task.FromResult<(int, int, string?)>((0, 0, "No confirmed picks to create ORDR."));
        }

        var (docEntry, docNum, error) = _sap.CreateOfflineRecoveryOrder(
            order.CardCode,
            order.DocDate,
            order.DeliveryDate ?? order.DocDate,
            order.SlpCode,
            order.DocCurrency,
            uReplitId,
            order.DeliveryLocation,
            groups);

        return Task.FromResult<(int, int, string?)>((docEntry, docNum ?? 0, error));
    }

    // ── 2. OPKL (one per warehouse) ───────────────────────────────────────────

    /// <summary>
    /// SAP-first per warehouse: one OPKL per WhsCode group.
    /// Picker is resolved via PickerResolutionService — fail closed to PICKER_MAPPING_INVALID.
    /// Existing OPKL verified for complete line membership — fail closed to PICKLIST_FRAGMENT_MISMATCH.
    /// </summary>
    public async Task<(string? Error, string? ReconciliationCode)> CreateOfflineRecoveryPickListsAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        int soDocEntry = order.SapSalesOrderDocEntry!.Value;
        var uReplitId  = BuildUReplitId(order.OfflineId);

        var rdr1Lines = _sap.ReadRdr1Lines(soDocEntry);
        if (rdr1Lines.Count == 0)
        {
            _log.LogWarning("[OF-V2-SAP] OPKL: no RDR1 lines for ORDR DocEntry={De}", soDocEntry);
            return ("No RDR1 lines found for ORDR. Cannot create OPKL.", ReconciliationReasonCode.SapPreflightFailed);
        }

        var picksByGroup = confirmedPicks
            .GroupBy(p => (p.ItemCode, p.WhsCode))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.PickedQty));

        var byWhs = rdr1Lines.GroupBy(l => l.WhsCode).ToList();

        foreach (var whsGroup in byWhs)
        {
            var whsCode          = whsGroup.Key;
            var linesForWhs      = whsGroup.ToList();
            var firstLine        = linesForWhs[0];
            var expectedLineNums = linesForWhs.Select(l => l.LineNum).ToHashSet();

            // Picker resolution — fail closed on all 6 conditions
            int ownerCode;
            try
            {
                var pickerResult = await _picker.ResolveAsync(whsCode, ct);
                ownerCode = pickerResult.SapUser.UserId;
                _log.LogInformation(
                    "[OF-V2-SAP] OPKL picker resolved WHS={Whs} OwnerCode={Oc} UserCode={Usr}",
                    whsCode, ownerCode, pickerResult.SapUser.UserCode);
            }
            catch (PickerAssignmentNotFoundException ex)
            {
                _log.LogWarning("[OF-V2-SAP] OPKL picker not found WHS={Whs}: {Msg}", whsCode, ex.Message);
                return (ex.Message, ReconciliationReasonCode.PickerMappingInvalid);
            }
            catch (PickerAssignmentInvalidException ex)
            {
                _log.LogWarning("[OF-V2-SAP] OPKL picker invalid WHS={Whs}: {Msg}", whsCode, ex.Message);
                return (ex.Message, ReconciliationReasonCode.PickerMappingInvalid);
            }
            catch (PickerSapUserUnavailableException ex)
            {
                _log.LogWarning("[OF-V2-SAP] OPKL picker SAP user unavailable WHS={Whs}: {Msg}", whsCode, ex.Message);
                return (ex.Message, ReconciliationReasonCode.PickerMappingInvalid);
            }

            // SAP-first: check if OPKL already exists for this warehouse group
            var existingAbsEntry = _sap.FindZoneFulfillmentPickListForFragment(
                uReplitId, soDocEntry, firstLine.LineNum);

            if (existingAbsEntry.HasValue)
            {
                // Verify completeness: ALL expected lines must be in the existing OPKL.
                // Partial OPKL (crash left SAP with incomplete document) must not be silently accepted.
                var actualLineNums = _sap.GetPickListLineNums(existingAbsEntry.Value, soDocEntry)
                                         .ToHashSet();
                if (!expectedLineNums.SetEquals(actualLineNums))
                {
                    var expected = string.Join(",", expectedLineNums.OrderBy(x => x));
                    var actual   = string.Join(",", actualLineNums.OrderBy(x => x));
                    _log.LogWarning(
                        "[OF-V2-SAP] OPKL fragment mismatch WHS={Whs} AbsEntry={Abs} Expected=[{E}] Actual=[{A}]",
                        whsCode, existingAbsEntry.Value, expected, actual);
                    return (
                        $"Existing OPKL {existingAbsEntry.Value} for WHS '{whsCode}' has incomplete or mismatched lines. Expected=[{expected}] Actual=[{actual}].",
                        ReconciliationReasonCode.PicklistFragmentMismatch);
                }

                _log.LogInformation(
                    "[OF-V2-SAP] OPKL SAP-first hit WHS={Whs} AbsEntry={Abs} lines={N} — complete, adopting",
                    whsCode, existingAbsEntry.Value, actualLineNums.Count);
                continue;
            }

            var specs = linesForWhs
                .Select(l =>
                {
                    picksByGroup.TryGetValue((l.ItemCode, l.WhsCode), out decimal pickedQty);
                    double releasedQty = pickedQty > 0 ? (double)pickedQty : (double)l.Quantity;
                    return new PickListLineSpec(soDocEntry, l.LineNum, releasedQty);
                })
                .ToList();

            try
            {
                var absEntry = _sap.CreateZoneFulfillmentPickListMultiLine(uReplitId, ownerCode, specs);
                _log.LogInformation("[OF-V2-SAP] OPKL created AbsEntry={Abs} WHS={Whs} lines={N}",
                    absEntry, whsCode, specs.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[OF-V2-SAP] OPKL.Add() failed WHS={Whs}", whsCode);
                return (ex.Message, ReconciliationReasonCode.SapPreflightFailed);
            }
        }

        return (null, null);
    }

    // ── 3. Pick replay ────────────────────────────────────────────────────────

    /// <summary>
    /// Replays confirmed physical picks into SAP using desired-state semantics.
    /// Fails closed on any bin disagreement — never silently reallocates.
    /// </summary>
    public Task<(string? Error, string? ReconciliationCode)> ReplayOfflinePicksAsync(
        OfflineFulfillmentOrder order,
        IReadOnlyList<OfflineFulfillmentPick> confirmedPicks,
        CancellationToken ct)
    {
        int soDocEntry = order.SapSalesOrderDocEntry!.Value;
        var uReplitId  = BuildUReplitId(order.OfflineId);

        var rdr1Lines = _sap.ReadRdr1Lines(soDocEntry);
        if (rdr1Lines.Count == 0)
            return Task.FromResult<(string?, string?)>(
                ("No RDR1 lines found for ORDR — cannot replay picks.", null));

        // Index picks by (ItemCode, WhsCode) for matching to RDR1 lines
        var picksByGroup = confirmedPicks
            .GroupBy(p => (p.ItemCode, p.WhsCode))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var line in rdr1Lines)
        {
            // Find the OPKL for this ORDR line
            var absEntry = _sap.FindZoneFulfillmentPickListForFragment(
                uReplitId, soDocEntry, line.LineNum);

            if (absEntry == null)
            {
                _log.LogWarning("[OF-V2-SAP] PickReplay: no OPKL for DocEntry={De} LineNum={Ln}",
                    soDocEntry, line.LineNum);
                return Task.FromResult<(string?, string?)>(
                    ($"No OPKL found for ORDR line {line.LineNum}. Cannot replay picks.", null));
            }

            // Aggregate picks for this (ItemCode, WhsCode) into bin allocations
            if (!picksByGroup.TryGetValue((line.ItemCode, line.WhsCode), out var linePicks))
            {
                _log.LogWarning("[OF-V2-SAP] PickReplay: no picks for ItemCode={Item} WhsCode={Whs}",
                    line.ItemCode, line.WhsCode);
                continue;
            }

            // Aggregate by bin (sum PickedQty per BinAbsEntry)
            var binAllocs = linePicks
                .Where(p => p.BinAbsEntry.HasValue)
                .GroupBy(p => p.BinAbsEntry!.Value)
                .Select(g =>
                {
                    var first = g.First();
                    return new BinPickAlloc(
                        g.Key,
                        first.BinCode ?? $"BIN-{g.Key}",
                        g.Sum(p => p.PickedQty));
                })
                .ToList();

            double desiredPickedQty = (double)linePicks.Sum(p => p.PickedQty);

            try
            {
                var (rc, sapError, _) = _sap.UpdateZoneFulfillmentPickList(
                    absEntry.Value, soDocEntry, line.LineNum,
                    desiredPickedQty, binAllocs, line.ItemCode, line.WhsCode);

                if (rc != 0)
                {
                    _log.LogWarning("[OF-V2-SAP] PickReplay UpdateZF failed rc={Rc} err={Err}",
                        rc, sapError);
                    return Task.FromResult<(string?, string?)>(
                        (sapError ?? "UpdateZoneFulfillmentPickList failed", ReconciliationReasonCode.SapBinShortage));
                }
            }
            catch (BinReservationConflictException ex)
            {
                _log.LogWarning("[OF-V2-SAP] PickReplay BinReservationConflict Bin={Bin} Avail={Avail} Req={Req}",
                    ex.Conflict.BinCode, ex.Conflict.EffectiveAvailableQty, ex.Conflict.RequestedPickQty);
                return Task.FromResult<(string?, string?)>(
                    (ex.Message, ReconciliationReasonCode.SapBinShortage));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[OF-V2-SAP] PickReplay unexpected error");
                return Task.FromResult<(string?, string?)>((ex.Message, null));
            }
        }

        return Task.FromResult<(string?, string?)>((null, null));
    }

    // ── 4. ODLN ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SAP-first: returns existing ODLN if found. Else builds delivery from durable PKL2 bins.
    /// </summary>
    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryDeliveryAsync(
        int salesOrderDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct)
    {
        var uReplitId  = BuildUReplitId(order.OfflineId);
        int soDocEntry = salesOrderDocEntry;

        // SAP-first check
        var existing = _sap.FindOfflineRecoveryDelivery(uReplitId, soDocEntry);
        if (existing.HasValue)
        {
            _log.LogInformation(
                "[OF-V2-SAP] ODLN SAP-first hit DocEntry={De} uReplitId={Rid} — skipping create",
                existing.Value.DocEntry, uReplitId);
            return Task.FromResult<(int, int, string?)>(
                (existing.Value.DocEntry, existing.Value.DocNum, null));
        }

        var rdr1Lines = _sap.ReadRdr1Lines(soDocEntry);
        if (rdr1Lines.Count == 0)
            return Task.FromResult<(int, int, string?)>((0, 0, "No RDR1 lines — cannot create ODLN."));

        // Build delivery lines from durable PKL2 bins (NOT fresh OIBQ)
        var deliveryLines = new List<DeliveryLineSpec>();
        foreach (var line in rdr1Lines)
        {
            var absEntry = _sap.FindZoneFulfillmentPickListForFragment(
                uReplitId, soDocEntry, line.LineNum);

            if (absEntry == null)
            {
                _log.LogWarning("[OF-V2-SAP] ODLN: no OPKL for line {Ln} — using empty bins", line.LineNum);
                deliveryLines.Add(new DeliveryLineSpec(
                    soDocEntry, line.LineNum, line.Quantity, line.WhsCode, []));
                continue;
            }

            var durableBins = _sap.GetPickListBinAllocations(absEntry.Value, soDocEntry, line.LineNum);
            deliveryLines.Add(new DeliveryLineSpec(
                soDocEntry, line.LineNum, line.Quantity, line.WhsCode, durableBins));
        }

        DateTime deliveryDate = order.DeliveryDate ?? order.DocDate;
        var (rc, docEntry, docNum, sapError) = _sap.CreateOfflineRecoveryDelivery(
            order.CardCode, deliveryDate, uReplitId, order.DeliveryLocation, deliveryLines);

        return Task.FromResult<(int, int, string?)>((docEntry, docNum, rc != 0 ? sapError : null));
    }

    // ── 5. OINV ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SAP-first: returns existing OINV if found. Else creates OINV from ODLN.
    /// </summary>
    public Task<(int DocEntry, int DocNum, string? Error)> CreateOfflineRecoveryInvoiceAsync(
        int deliveryDocEntry,
        OfflineFulfillmentOrder order,
        CancellationToken ct)
    {
        // SAP-first: check for existing active OINV linked to this ODLN
        var existing = _sap.SearchActiveInvoicesByDelivery(deliveryDocEntry);
        if (existing.Count > 0)
        {
            var inv = existing[0];
            _log.LogInformation(
                "[OF-V2-SAP] OINV SAP-first hit DocEntry={De} DocNum={Dn} ODLN={DlnEntry} — skipping create",
                inv.DocEntry, inv.DocNum, deliveryDocEntry);
            return Task.FromResult<(int, int, string?)>((inv.DocEntry, inv.DocNum, null));
        }

        var uReplitId = BuildUReplitId(order.OfflineId);
        try
        {
            var (docEntry, docNum) = _sap.CreateOfflineRecoveryInvoice(
                deliveryDocEntry,
                order.CardCode,
                order.DocDate,
                order.DeliveryDate ?? order.DocDate,
                order.DocCurrency,
                order.SlpCode,
                uReplitId,
                order.DeliveryLocation);

            return Task.FromResult<(int, int, string?)>((docEntry, docNum, null));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[OF-V2-SAP] OINV.Add() failed for ODLN={DlnEntry}", deliveryDocEntry);
            return Task.FromResult<(int, int, string?)>((0, 0, ex.Message));
        }
    }
}
