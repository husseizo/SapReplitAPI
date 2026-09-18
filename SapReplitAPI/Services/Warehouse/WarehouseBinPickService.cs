using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Warehouse;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Services.Warehouse;

/// <summary>
/// Warehouse App picking service — handles the "Select Bin &amp; Pick" workflow.
///
/// Business state: OPKL.Status=R, PKL2=0 (no pre-allocated bin).
/// Picker selects source bin(s) from live OIBQ candidates, confirms pick.
/// SAP DI API writes PKL2. Targeted cache refresh follows.
///
/// Safety constraints (frozen):
///   - Never modifies OPKL directly via SQL.
///   - Never calls ZoneAllocationEngine or TieredZoneAllocationEngine (no re-tiering).
///   - Never calls ZoneFulfillmentDeliveryService or InvoiceService (no ODLN/OINV).
///   - Never creates or updates ORDR.
///   - SAP update is via existing UpdateZoneFulfillmentPickList only (DI API).
/// </summary>
public sealed class WarehouseBinPickService
{
    private readonly IWarehousePickSapAdapter        _sap;
    private readonly PickListCacheService            _cache;
    private readonly IPickListEventRefreshService    _refresh;
    private readonly ILogger<WarehouseBinPickService> _log;

    public WarehouseBinPickService(
        IWarehousePickSapAdapter        sap,
        PickListCacheService            cache,
        IPickListEventRefreshService    refresh,
        ILogger<WarehouseBinPickService> log)
    {
        _sap     = sap;
        _cache   = cache;
        _refresh = refresh;
        _log     = log;
    }

    // ── GET state ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full UI state for one pick list, including live OIBQ candidates
    /// when the pick list is in AwaitingBinSelection state.
    /// </summary>
    public async Task<PickListStateResponseDto?> GetStateAsync(
        int absEntry, CancellationToken ct = default)
    {
        var header = await _cache.ReadPickListAsync(absEntry, ct);
        if (header is null) return null;

        var lines = await _cache.ReadPickListLinesAsync(absEntry, ct);
        var bins  = await _cache.ReadPickListBinsAsync(absEntry, ct);

        int cachedBinCount = bins.Count;

        // Build per-line state — load live candidates when bins=0
        var lineStates = new List<PickLineStateDto>(lines.Count);
        int totalCandidates = 0;

        foreach (var l in lines)
        {
            var lineBins   = bins.Where(b => b.PickEntry == l.PickEntry).ToList();
            var candidates = new List<BinCandidateDto>();

            if (cachedBinCount == 0 && header.Status == "R")
            {
                // Load live OIBQ candidates for this line's ItemCode + WhsCode
                candidates = _sap.QueryBinCandidates(l.ItemCode, l.WhsCode);
                totalCandidates += candidates.Count;
            }

            lineStates.Add(new PickLineStateDto
            {
                PickEntry     = l.PickEntry,
                ItemCode      = l.ItemCode,
                Dscription    = l.Dscription,
                WhsCode       = l.WhsCode,
                RelQtty       = l.RelQtty,
                PickQtty      = l.PickQtty,
                PickStatus    = l.PickStatus,
                Candidates    = candidates,
                AllocatedBins = lineBins.Select(b => new AllocatedBinDto(
                    b.BinAbsEntry, b.BinCode, b.WhsCode, b.RelQtty, b.PickQtty)).ToList(),
            });
        }

        // Determine overall UI state (use first line's candidate count for single-line lists;
        // for multi-line: any candidate = AwaitingBinSelection)
        int candidateSignal = cachedBinCount > 0 ? -1 : totalCandidates;
        var uiState = PickListUiStateComputer.Compute(
            header.Status, header.Canceled, cachedBinCount, candidateSignal);

        return new PickListStateResponseDto
        {
            AbsEntry      = absEntry,
            SapStatus     = header.Status,
            UiState       = uiState.ToString(),
            UiLabel       = PickListUiStateComputer.ToLabel(uiState),
            UiAction      = PickListUiStateComputer.ToAction(uiState),
            SupportingText= PickListUiStateComputer.ToSupportingText(uiState),
            Lines         = lineStates,
        };
    }

    // ── GET bin candidates ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns live OIBQ bin candidates for a specific pick line.
    /// Only valid for Status=R + Bins=0. Returns empty list for all other states.
    /// </summary>
    public async Task<(bool Found, List<BinCandidateDto> Candidates)> GetBinCandidatesAsync(
        int absEntry, int pickEntry, CancellationToken ct = default)
    {
        var lines = await _cache.ReadPickListLinesAsync(absEntry, ct);
        var line  = lines.FirstOrDefault(l => l.PickEntry == pickEntry);
        if (line is null)
            return (false, new());

        var candidates = _sap.QueryBinCandidates(line.ItemCode, line.WhsCode);
        return (true, candidates);
    }

    // ── POST confirm pick ──────────────────────────────────────────────────────

    /// <summary>
    /// Executes picker-selected bin confirmation for one or more pick lines.
    ///
    /// Per line:
    ///   1. Load fresh OIBQ candidates.
    ///   2. Validate bin selections (wrong-WHS, disabled, insufficient qty, total mismatch).
    ///   3. Call SAP DI API UpdateZoneFulfillmentPickList.
    ///   4. If SAP fails: record error, continue remaining lines.
    ///
    /// After all lines:
    ///   5. If any line succeeded: trigger targeted AbsEntry cache refresh (non-fatal).
    ///
    /// The caller's UI must use the refreshed SAP result rather than assuming success.
    /// </summary>
    public async Task<ConfirmPickResponseDto> ConfirmPickAsync(
        int absEntry, ConfirmPickRequest req, CancellationToken ct = default)
    {
        var cachedLines = await _cache.ReadPickListLinesAsync(absEntry, ct);
        var results     = new List<LineConfirmResultDto>(req.Lines.Count);
        bool anySuccess = false;

        foreach (var reqLine in req.Lines)
        {
            var cached = cachedLines.FirstOrDefault(l => l.PickEntry == reqLine.PickEntry);
            if (cached is null)
            {
                results.Add(new LineConfirmResultDto
                {
                    PickEntry = reqLine.PickEntry,
                    Success   = false,
                    PickedQty = 0m,
                    SapRc     = -1,
                    Error     = $"PickEntry {reqLine.PickEntry} not found for AbsEntry {absEntry}.",
                });
                continue;
            }

            // 1. Fresh candidates (pre-mutation recheck)
            var candidates = _sap.QueryBinCandidates(cached.ItemCode, cached.WhsCode);

            // 2. Validate
            var validationError = PickBinValidator.ValidateLine(
                reqLine.PickedQty, candidates, reqLine.Bins);

            if (validationError is not null)
            {
                _log.LogWarning(
                    "[WH-PICK] Validation failed AbsEntry={Abs} PickEntry={Pe}: {Err}",
                    absEntry, reqLine.PickEntry, validationError);
                results.Add(new LineConfirmResultDto
                {
                    PickEntry = reqLine.PickEntry,
                    Success   = false,
                    PickedQty = 0m,
                    SapRc     = -1,
                    Error     = validationError,
                });
                continue;
            }

            // 3. Map to BinPickAlloc for SAP DI API
            var binAllocs = reqLine.Bins.Select(b =>
                new BinPickAlloc(b.BinAbsEntry,
                    candidates.First(c => c.BinAbsEntry == b.BinAbsEntry).BinCode,
                    b.Qty)).ToList();

            _log.LogInformation(
                "[WH-PICK] ConfirmPick AbsEntry={Abs} PickEntry={Pe} Item={Item} Whs={Whs} " +
                "PickedQty={Qty} Bins={N}",
                absEntry, reqLine.PickEntry, cached.ItemCode, cached.WhsCode,
                reqLine.PickedQty, binAllocs.Count);

            // 4. SAP DI API pick
            var (rc, sapErr, postState) = _sap.ExecutePick(
                absEntry,
                cached.OrderEntry,
                cached.OrderLine,
                (double)reqLine.PickedQty,
                binAllocs,
                cached.ItemCode,
                cached.WhsCode);

            if (rc != 0)
            {
                _log.LogError(
                    "[WH-PICK] SAP pick failed AbsEntry={Abs} PickEntry={Pe} rc={Rc} err='{Err}'",
                    absEntry, reqLine.PickEntry, rc, sapErr);
                results.Add(new LineConfirmResultDto
                {
                    PickEntry = reqLine.PickEntry,
                    Success   = false,
                    PickedQty = 0m,
                    SapRc     = rc,
                    Error     = sapErr,
                });
                continue;
            }

            anySuccess = true;
            results.Add(new LineConfirmResultDto
            {
                PickEntry = reqLine.PickEntry,
                Success   = true,
                PickedQty = postState?.PickQtty ?? reqLine.PickedQty,
                SapRc     = 0,
                Error     = null,
            });

            _log.LogInformation(
                "[WH-PICK] SAP pick success AbsEntry={Abs} PickEntry={Pe} PickQtty={Qty}",
                absEntry, reqLine.PickEntry, postState?.PickQtty);
        }

        bool overallSuccess = results.All(r => r.Success);

        // 5. Targeted cache refresh after any successful line — non-fatal
        if (anySuccess)
        {
            try
            {
                await _refresh.RefreshAsync(absEntry, ct);
                _log.LogInformation("[WH-PICK] Cache refresh triggered AbsEntry={Abs}", absEntry);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WH-PICK] Cache refresh non-fatal AbsEntry={Abs}", absEntry);
            }
        }

        return new ConfirmPickResponseDto
        {
            AbsEntry = absEntry,
            Success  = overallSuccess,
            Lines    = results,
            Error    = overallSuccess ? null : "One or more lines failed — see Lines for details.",
        };
    }
}
