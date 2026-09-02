using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Orchestrates Pick List creation and pick execution for Zone Fulfillment SOs.
///
/// One OPKL per distinct WhsCode (grouped), with multiple PKL1 lines.
/// OwnerCode resolved via PickerResolutionService before OPKL.Add().
/// After a successful Confirm Pick, delegates to ZoneFulfillmentAutomationService
/// to evaluate all-picks-complete and trigger automatic delivery if ready.
/// </summary>
public sealed class ZoneFulfillmentPickListService
{
    // Authorized 2026-09-02: production automation flow active.
    private const bool PICK_LIST_MUTATION_ENABLED = true;

    private readonly SapService                              _sap;
    private readonly ZoneFulfillmentRepository               _repo;
    private readonly PickerResolutionService                 _picker;
    private readonly ZoneFulfillmentAutomationService        _automation;
    private readonly ILogger<ZoneFulfillmentPickListService> _log;

    public ZoneFulfillmentPickListService(
        SapService                               sap,
        ZoneFulfillmentRepository                repo,
        PickerResolutionService                  picker,
        ZoneFulfillmentAutomationService         automation,
        ILogger<ZoneFulfillmentPickListService>  log)
    {
        _sap        = sap;
        _repo       = repo;
        _picker     = picker;
        _automation = automation;
        _log        = log;
    }

    /// <summary>
    /// Bug #2 fix: Creates one OPKL per distinct WhsCode, each with multiple PKL1 lines.
    /// Bug #1 fix: OwnerCode resolved per WHS via PickerResolutionService.
    /// Idempotent: replaying with the same RequestId returns existing Pick Lists.
    /// PICK_LIST_MUTATION_ENABLED gate blocks OPKL.Add() until schema migration is applied.
    /// </summary>
    public async Task<PickListCreateResult> CreatePickListsAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
            throw new InvalidOperationException($"RequestId {requestId} not found.");

        if (orch.State != OrchestrationState.Accepted)
            throw new InvalidOperationException(
                $"Orchestration {requestId} is in state '{orch.State}' — expected '{OrchestrationState.Accepted}'.");

        if (orch.SoDocEntry is null)
            throw new InvalidOperationException($"Orchestration {requestId} has no SoDocEntry.");

        var fragments = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        if (fragments.Count == 0)
            throw new InvalidOperationException($"No SoLineFragments found for orchestration {requestId}.");

        string uReplitId = orch.U_ReplitId
            ?? ZoneFulfillmentSapOrderService.BuildReplitId(requestId);

        // Group fragments by WhsCode → one OPKL per WHS
        var byWhs    = fragments.GroupBy(f => f.WhsCode).ToList();
        bool anyNew  = false;
        var infos    = new List<PickListInfo>();

        foreach (var whsGroup in byWhs)
        {
            string whsCode = whsGroup.Key;
            var    frags   = whsGroup.ToList();

            // Bug #1 fix: resolve picker for this warehouse (fail closed)
            var pickerResult = await _picker.ResolveAsync(whsCode, ct);
            int ownerCode    = pickerResult.SapUser.UserId;

            _log.LogInformation(
                "[ZF-PL] WHS={Whs} Picker={User} OwnerCode={Owner} fragments={N}",
                whsCode, pickerResult.SapUser.UserCode, ownerCode, frags.Count);

            var (groupInfos, groupIsNew) = await CreateOrGetPickListForWhsGroupAsync(
                orch, frags, uReplitId, ownerCode, ct);

            infos.AddRange(groupInfos);
            if (groupIsNew) anyNew = true;
        }

        _log.LogInformation(
            "[ZF-PL] CreatePickListsAsync RequestId={Rid} whsGroups={G} totalFragments={N} isNew={New}",
            requestId, byWhs.Count, fragments.Count, anyNew);

        return new PickListCreateResult
        {
            RequestId  = requestId,
            PickLists  = infos,
            IsNew      = anyNew
        };
    }

    // ── Private: grouped OPKL creation (Bug #2) ───────────────────────────────

    private async Task<(List<PickListInfo> infos, bool isNew)> CreateOrGetPickListForWhsGroupAsync(
        FulfillmentOrchestrationRecord             orch,
        IReadOnlyList<SoLineFragmentRecord>        frags,
        string                                     uReplitId,
        int                                        ownerCode,
        CancellationToken                          ct)
    {
        // Idempotency: if any fragment in this group already has a PickListRecord, return all
        var existingForFirstFrag = await _repo.FindPickListRecordAsync(frags[0].Id, frags[0].WhsCode, ct);
        if (existingForFirstFrag is not null)
        {
            _log.LogInformation(
                "[ZF-PL] Idempotent WHS={Whs} AbsEntry={Abs} — returning existing records",
                frags[0].WhsCode, existingForFirstFrag.PickListAbsEntry);
            var existingInfos = new List<PickListInfo>();
            foreach (var frag in frags)
            {
                var existing = await _repo.FindPickListRecordAsync(frag.Id, frag.WhsCode, ct);
                if (existing is not null)
                {
                    var (_, _, _, desc) = _sap.GetZoneFulfillmentItemDescription(frag.ItemCode);
                    existingInfos.Add(ToInfo(existing, isNew: false, desc));
                }
            }
            return (existingInfos, false);
        }

        // Bug #1+2 fix: create ONE OPKL for the WHS group with multiple PKL1 lines
        // Each fragment = one PKL1 line; pl.Lines.Add() appends subsequent lines.
        _log.LogInformation(
            "[ZF-PL] Creating grouped OPKL WHS={Whs} lineCount={N} ownerCode={Owner} uReplitId={Rid}",
            frags[0].WhsCode, frags.Count, ownerCode, uReplitId);

        // Bug #2 fix (Section 13): use multi-line OPKL creation for all group sizes.
        // CreateZoneFulfillmentPickListMultiLine handles 1 or N fragments uniformly.
        var lineSpecs = frags.Select(f => new SapReplitAPI.Models.ZoneFulfillment.PickListLineSpec(
            f.SoDocEntry, f.SoLineNum, (double)f.SoLineQty)).ToList();
        int absEntry = _sap.CreateZoneFulfillmentPickListMultiLine(uReplitId, ownerCode, lineSpecs);

        var resultInfos = new List<PickListInfo>();
        foreach (var frag in frags)
        {
            var record = new PickListRecordModel
            {
                OrchestrationId  = orch.Id,
                SoLineFragmentId = frag.Id,
                SoDocEntry       = frag.SoDocEntry,
                SoLineNum        = frag.SoLineNum,
                WhsCode          = frag.WhsCode,
                PickListAbsEntry = absEntry,
                ReleasedQty      = frag.SoLineQty,
                Status           = PickListStatus.Created
            };
            try
            {
                // InsertPickListRecordAsync sets record.Id via OUTPUT INSERTED.Id
                await _repo.InsertPickListRecordAsync(record, ct);
                // Mirror each OPKL line into PickListFragmentRecord for normalized multi-line tracking
                await _repo.InsertPickListFragmentRecordAsync(
                    record.Id, frag.Id, frag.SoDocEntry, frag.SoLineNum,
                    frag.ItemCode, frag.WhsCode, frag.SoLineQty, ct);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                // Duplicate (OrchId, FragId, AbsEntry) — concurrent race or replay; use latest existing PLR
                var winner = await _repo.FindPickListRecordAsync(frag.Id, frag.WhsCode, ct);
                if (winner is not null) { record = winner; }
                else throw;
            }
            var (_, _, _, desc) = _sap.GetZoneFulfillmentItemDescription(frag.ItemCode);
            resultInfos.Add(ToInfo(record, isNew: true, desc));
        }
        return (resultInfos, true);
    }

    // ── C-SAP-03: Pick execution ──────────────────────────────────────────────

    /// <summary>
    /// Executes a controlled pick on one specific OPKL for a Zone Fulfillment request.
    ///
    /// Desired-state contract (not additive):
    ///   desiredPickedQty = target final value, not a delta.
    ///   Replay with the same desiredPickedQty returns alreadyApplied=true without calling Update().
    ///
    /// C-SAP-03 constraint: desiredPickedQty must equal ReleasedQty (full pick only).
    ///   Partial picking is deferred to a future gate.
    ///
    /// Crash recovery:
    ///   If SAP PKL1.PickQtty already equals desiredPickedQty but MolasIntegration PickedQty=0,
    ///   the SAP truth wins — MolasIntegration is updated without a second SAP Update() call.
    /// </summary>
    public async Task<PickExecuteResult> ExecutePickAsync(
        Guid    requestId,
        int     pickListAbsEntry,
        decimal desiredPickedQty,
        CancellationToken ct = default)
    {
        // 1. Resolve orchestration
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
            throw new InvalidOperationException($"RequestId {requestId} not found.");
        if (orch.State != OrchestrationState.Accepted)
            throw new InvalidOperationException(
                $"Orchestration {requestId} is in state '{orch.State}' — expected '{OrchestrationState.Accepted}'.");

        // 2. Resolve PickListRecord and verify it belongs to this request
        var record = await _repo.FindPickListRecordByAbsEntryAsync(orch.Id, pickListAbsEntry, ct);
        if (record is null)
            throw new InvalidOperationException(
                $"PickListAbsEntry={pickListAbsEntry} not found for RequestId={requestId}.");

        // 3. C-SAP-03 full-pick enforcement: desiredPickedQty must equal ReleasedQty
        if (desiredPickedQty != record.ReleasedQty)
            throw new ArgumentException(
                $"Partial picking not supported in C-SAP-03. desiredPickedQty ({desiredPickedQty}) " +
                $"must equal ReleasedQty ({record.ReleasedQty}). Partial picking is deferred.");

        // 4. Read MolasIntegration current state
        decimal molasPicked = record.PickedQty;

        // 5. Read SAP PKL1 current truth
        var sapState = _sap.ReadZoneFulfillmentPickListLine(
            pickListAbsEntry, record.SoDocEntry, record.SoLineNum);
        if (sapState is null)
            throw new InvalidOperationException(
                $"PKL1 line not found for AbsEntry={pickListAbsEntry} SO={record.SoDocEntry} Line={record.SoLineNum}.");

        _log.LogInformation(
            "[ZF-PICK] ExecutePickAsync RequestId={Rid} AbsEntry={Abs} desiredPickedQty={Qty} " +
            "MolasPickedQty={MPicked} SapPickQtty={SPicked} SapPickStatus={Stat}",
            requestId, pickListAbsEntry, desiredPickedQty, molasPicked, sapState.PickQtty, sapState.PickStatus);

        // 6. Crash recovery: SAP already at target but MolasIntegration is behind
        if (sapState.PickQtty == desiredPickedQty)
        {
            bool needsPersist = molasPicked != desiredPickedQty;
            if (needsPersist)
            {
                _log.LogWarning(
                    "[ZF-PICK] SAP-first recovery: SAP PickQtty={SAP} but MolasIntegration PickedQty={MI} — persisting without SAP Update()",
                    sapState.PickQtty, molasPicked);
                await _repo.UpdatePickListPickedQtyAsync(record.Id, desiredPickedQty, PickListStatus.Picked, ct);
            }
            var crashAutomation = await _automation.EvaluateAndTriggerDeliveryAsync(requestId, ct);
            return new PickExecuteResult
            {
                RequestId          = requestId,
                PickListAbsEntry   = pickListAbsEntry,
                ReleasedQty        = record.ReleasedQty,
                PickedQty          = desiredPickedQty,
                Status             = PickListStatus.Picked,
                IsNew              = false,
                AlreadyApplied     = true,
                RecoveredFromSap   = needsPersist,
                SapPostState       = sapState,
                BinsUsed           = [],
                PostPickAutomation = crashAutomation
            };
        }

        // 7. Validate guard (belt-and-suspenders, already checked above)
        if (desiredPickedQty < 0 || desiredPickedQty > record.ReleasedQty)
            throw new ArgumentException(
                $"desiredPickedQty {desiredPickedQty} out of range [0, {record.ReleasedQty}].");

        // 8. Resolve live bin allocation from OIBQ (ItemCode from SoLineFragment)
        var frags = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        var frag = frags.FirstOrDefault(f => f.SoDocEntry == record.SoDocEntry && f.SoLineNum == record.SoLineNum);
        if (frag is null)
            throw new InvalidOperationException(
                $"SoLineFragment not found for SoDocEntry={record.SoDocEntry} SoLineNum={record.SoLineNum}.");

        var liveBins = _sap.QueryBinForPick(frag.ItemCode, frag.WhsCode);
        if (liveBins.Count == 0)
            _log.LogWarning("[ZF-PICK] No positive-stock bins found for ItemCode={Item} WhsCode={Whs} — attempting pick without bin specification",
                frag.ItemCode, frag.WhsCode);

        // Allocate from bins greedily (first bins with stock, sum to desiredPickedQty)
        var selectedBins = new List<BinPickAlloc>();
        decimal remaining = desiredPickedQty;
        foreach (var bin in liveBins)
        {
            if (remaining <= 0) break;
            decimal take = Math.Min(bin.Qty, remaining);
            selectedBins.Add(bin with { Qty = take });
            remaining -= take;
        }
        if (remaining > 0 && liveBins.Count > 0)
            _log.LogWarning("[ZF-PICK] Bin stock insufficient: needed {Need} but only {Got} available",
                desiredPickedQty, desiredPickedQty - remaining);

        // 9. Execute one SAP DI API Update()
        var (rc, sapErr, postState) = _sap.UpdateZoneFulfillmentPickList(
            pickListAbsEntry, record.SoDocEntry, record.SoLineNum,
            (double)desiredPickedQty, selectedBins);

        if (rc != 0)
            throw new SapPickListUpdateException(rc, sapErr ?? "Unknown SAP error");

        // 10+11. Post-state already read inside UpdateZoneFulfillmentPickList
        if (postState is null)
            throw new InvalidOperationException("SAP Update() succeeded but PKL1 readback returned null.");

        if (postState.PickQtty != desiredPickedQty)
            _log.LogWarning("[ZF-PICK] SAP PKL1.PickQtty={Got} does not match desiredPickedQty={Want} after Update()",
                postState.PickQtty, desiredPickedQty);

        // 12. Persist MolasIntegration only after SAP confirms
        string newStatus = postState.PickQtty == record.ReleasedQty
            ? PickListStatus.Picked
            : PickListStatus.Created;

        await _repo.UpdatePickListPickedQtyAsync(record.Id, postState.PickQtty, newStatus, ct);

        // 12b. Update PLFR if one exists for this PLR (graceful: 0 rows = pre-PLFR history, not an error)
        int plfrRows = await _repo.UpdatePickListFragmentPickedQtyAsync(record.Id, postState.PickQtty, newStatus, ct);
        if (plfrRows > 0)
            _log.LogInformation("[ZF-PICK] PLFR updated: PLR.Id={Id} PickedQty={Qty} Status={St}",
                record.Id, postState.PickQtty, newStatus);

        _log.LogInformation("[ZF-PICK] ExecutePickAsync SUCCESS AbsEntry={Abs} PickQtty={Qty} Status={St}",
            pickListAbsEntry, postState.PickQtty, newStatus);

        // Post-pick: evaluate all-picks-complete and auto-trigger delivery if ready
        var automation = await _automation.EvaluateAndTriggerDeliveryAsync(requestId, ct);

        return new PickExecuteResult
        {
            RequestId          = requestId,
            PickListAbsEntry   = pickListAbsEntry,
            ReleasedQty        = record.ReleasedQty,
            PickedQty          = postState.PickQtty,
            Status             = newStatus,
            IsNew              = true,
            AlreadyApplied     = false,
            RecoveredFromSap   = false,
            SapPostState       = postState,
            BinsUsed           = selectedBins,
            PostPickAutomation = automation
        };
    }

    // ── Controlled repick ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a NEW OPKL for a specific SO line that has a closed historical OPKL and an open RDR1 line.
    /// Authorization gate: requires the fragment's latest PLR.Status=Picked AND SAP PKL1.PickStatus=C
    /// AND RDR1.OpenQty>0 — otherwise throws InvalidOperationException.
    ///
    /// Crash recovery: if the latest PLR.Status=Created for this fragment, a prior repick OPKL was
    /// already created; returns it without creating a new one.
    ///
    /// Does NOT execute the pick — call ExecutePickAsync separately after creation.
    /// </summary>
    public async Task<RepickCreateResult> CreateRepickPickListAsync(
        Guid requestId, int soLineNum, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
            throw new InvalidOperationException($"RequestId {requestId} not found.");

        if (orch.SoDocEntry is null)
            throw new InvalidOperationException($"Orchestration {requestId} has no SoDocEntry.");

        string uReplitId = orch.U_ReplitId
            ?? ZoneFulfillmentSapOrderService.BuildReplitId(requestId);

        var fragments = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        var frag = fragments.FirstOrDefault(f => f.SoLineNum == soLineNum);
        if (frag is null)
            throw new InvalidOperationException(
                $"SoLineNum {soLineNum} not found for RequestId {requestId}.");

        var allPlrs = await _repo.GetPickListRecordsAsync(orch.Id, ct);
        var latestPlr = allPlrs.FirstOrDefault(p => p.SoLineFragmentId == frag.Id);
        if (latestPlr is null)
            throw new InvalidOperationException(
                $"No PickListRecord for SoLineNum {soLineNum} — run CreatePickLists first.");

        // Crash recovery: prior repick OPKL was created but not yet picked
        if (latestPlr.Status == PickListStatus.Created)
        {
            _log.LogInformation(
                "[ZF-REPICK] Crash recovery: PLR Id={Id} AbsEntry={Abs} Status=Created — returning existing",
                latestPlr.Id, latestPlr.PickListAbsEntry);
            var (_, _, _, crashDesc) = _sap.GetZoneFulfillmentItemDescription(frag.ItemCode);
            return new RepickCreateResult
            {
                RequestId        = requestId,
                PickListAbsEntry = latestPlr.PickListAbsEntry,
                PlrId            = latestPlr.Id,
                IsNew            = false,
                IsCrashRecovery  = true,
                SoLineNum        = soLineNum,
                ItemCode         = frag.ItemCode,
                WhsCode          = frag.WhsCode,
                ReleasedQty      = latestPlr.ReleasedQty,
                Status           = latestPlr.Status,
                LineDescription  = crashDesc,
                SapPkl1          = _sap.ReadZoneFulfillmentPickListLine(
                    latestPlr.PickListAbsEntry, frag.SoDocEntry, soLineNum)
            };
        }

        // Validate repick need: historical PKL1.PickStatus must be 'C' (completely picked)
        var pkl1History = _sap.ReadZoneFulfillmentPickListLine(
            latestPlr.PickListAbsEntry, frag.SoDocEntry, soLineNum);
        if (pkl1History is null)
            throw new InvalidOperationException(
                $"Historical PKL1 not found: AbsEntry={latestPlr.PickListAbsEntry} SO={frag.SoDocEntry} Line={soLineNum}.");

        if (pkl1History.PickStatus != "C")
            throw new InvalidOperationException(
                $"Historical PKL1.PickStatus='{pkl1History.PickStatus}' — expected 'C'. " +
                $"Repick not needed or OPKL still active.");

        // Validate RDR1 OpenQty > 0
        var openLines = _sap.GetOpenSoLines(frag.SoDocEntry);
        var openLine  = openLines.FirstOrDefault(l => l.LineNum == soLineNum);
        if (openLine is null || openLine.OpenQty <= 0)
            throw new InvalidOperationException(
                $"RDR1 SoDocEntry={frag.SoDocEntry} LineNum={soLineNum} " +
                $"OpenQty={openLine?.OpenQty ?? 0} — SO line closed. Repick not allowed.");

        // Resolve picker (fail closed — locked picker blocks repick)
        var pickerResult = await _picker.ResolveAsync(frag.WhsCode, ct);
        if (pickerResult.SapUser.Locked == "Y")
            throw new InvalidOperationException(
                $"Picker {pickerResult.SapUser.UserCode} (UserId={pickerResult.SapUser.UserId}) is locked.");
        int ownerCode = pickerResult.SapUser.UserId;

        _log.LogInformation(
            "[ZF-REPICK] Creating OPKL SoLineNum={Line} ItemCode={Item} WHS={Whs} Picker={User} uReplitId={Rid}",
            soLineNum, frag.ItemCode, frag.WhsCode, pickerResult.SapUser.UserCode, uReplitId);

        // Create new OPKL (SAP OPKL.Add — authorized for this specific controlled repick)
        var lineSpecs = new List<PickListLineSpec>
        {
            new(frag.SoDocEntry, soLineNum, (double)frag.SoLineQty)
        };
        int newAbsEntry = _sap.CreateZoneFulfillmentPickListMultiLine(uReplitId, ownerCode, lineSpecs);

        _log.LogInformation("[ZF-REPICK] New OPKL AbsEntry={Abs} for SoLineNum={Line}", newAbsEntry, soLineNum);

        // Insert new PLR (OUTPUT INSERTED.Id sets record.Id)
        var record = new PickListRecordModel
        {
            OrchestrationId  = orch.Id,
            SoLineFragmentId = frag.Id,
            SoDocEntry       = frag.SoDocEntry,
            SoLineNum        = soLineNum,
            WhsCode          = frag.WhsCode,
            PickListAbsEntry = newAbsEntry,
            ReleasedQty      = frag.SoLineQty,
            Status           = PickListStatus.Created
        };
        try
        {
            await _repo.InsertPickListRecordAsync(record, ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            var winner = await _repo.FindPickListRecordByAbsEntryAsync(orch.Id, newAbsEntry, ct);
            if (winner is not null) { record = winner; }
            else throw;
        }

        // Insert PLFR (SapPickEntry=NULL; PickedQty/PickStatus updated by ExecutePickAsync)
        try
        {
            await _repo.InsertPickListFragmentRecordAsync(
                record.Id, frag.Id, frag.SoDocEntry, soLineNum,
                frag.ItemCode, frag.WhsCode, frag.SoLineQty, ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            _log.LogWarning("[ZF-REPICK] PLFR duplicate for PLR.Id={Id} — skipped", record.Id);
        }

        // Read back PKL1 to confirm OPKL creation
        var pkl1New = _sap.ReadZoneFulfillmentPickListLine(newAbsEntry, frag.SoDocEntry, soLineNum);
        var (_, _, _, desc) = _sap.GetZoneFulfillmentItemDescription(frag.ItemCode);

        _log.LogInformation(
            "[ZF-REPICK] SUCCESS SoLineNum={Line} AbsEntry={Abs} PLR.Id={PlrId} PKL1.RelQtty={Rel} PKL1.PickQtty={Pick}",
            soLineNum, newAbsEntry, record.Id, pkl1New?.RelQtty, pkl1New?.PickQtty);

        return new RepickCreateResult
        {
            RequestId        = requestId,
            PickListAbsEntry = newAbsEntry,
            PlrId            = record.Id,
            IsNew            = true,
            IsCrashRecovery  = false,
            SoLineNum        = soLineNum,
            ItemCode         = frag.ItemCode,
            WhsCode          = frag.WhsCode,
            ReleasedQty      = frag.SoLineQty,
            Status           = PickListStatus.Created,
            LineDescription  = desc,
            SapPkl1          = pkl1New
        };
    }

    /// <summary>
    /// Returns a full pre-mutation gate state snapshot without modifying anything.
    /// Used by GET .../pick-lists/{absEntry}/state endpoint (Section 17).
    /// </summary>
    public async Task<PickListStateSnapshot> GetPickListStateAsync(
        Guid requestId, int pickListAbsEntry, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
            throw new InvalidOperationException($"RequestId {requestId} not found.");

        var record = await _repo.FindPickListRecordByAbsEntryAsync(orch.Id, pickListAbsEntry, ct);
        if (record is null)
            throw new InvalidOperationException(
                $"PickListAbsEntry={pickListAbsEntry} not found for RequestId={requestId}.");

        var frags = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        var frag = frags.FirstOrDefault(f => f.SoDocEntry == record.SoDocEntry && f.SoLineNum == record.SoLineNum);

        var sapPkl1    = _sap.ReadZoneFulfillmentPickListLine(pickListAbsEntry, record.SoDocEntry, record.SoLineNum);
        var sapSoUdfs  = orch.SoDocEntry.HasValue ? _sap.GetZoneFulfillmentSoUdfs(orch.SoDocEntry.Value) : null;
        var sapOitw    = frag is not null ? _sap.QueryPickListOitw(frag.ItemCode, frag.WhsCode) : null;
        var sapBins    = frag is not null ? _sap.QueryBinForPick(frag.ItemCode, frag.WhsCode) : [];

        return new PickListStateSnapshot
        {
            RequestId        = requestId,
            PickListAbsEntry = pickListAbsEntry,
            MolasRecord      = record,
            SapPkl1State     = sapPkl1,
            SapSoUdfs        = sapSoUdfs,
            SapOitw          = sapOitw,
            SapBins          = sapBins
        };
    }

    private static PickListInfo ToInfo(PickListRecordModel r, bool isNew, string? description = null) => new()
    {
        PickListAbsEntry = r.PickListAbsEntry,
        WhsCode          = r.WhsCode,
        SoDocEntry       = r.SoDocEntry,
        SoLineNum        = r.SoLineNum,
        ReleasedQty      = r.ReleasedQty,
        Status           = r.Status,
        IsNew            = isNew,
        LineDescription  = description
    };
}

// ── Result shapes ─────────────────────────────────────────────────────────────

public sealed class PickListCreateResult
{
    public required Guid              RequestId { get; init; }
    public required List<PickListInfo> PickLists { get; init; }
    public required bool               IsNew     { get; init; }
}

public sealed class PickListInfo
{
    public required int     PickListAbsEntry { get; init; }
    public required string  WhsCode          { get; init; }
    public required int     SoDocEntry       { get; init; }
    public required int     SoLineNum        { get; init; }
    public required decimal ReleasedQty      { get; init; }
    public required string  Status           { get; init; }
    public required bool    IsNew            { get; init; }
    public          string? LineDescription  { get; init; }
}

public sealed class PickExecuteResult
{
    public required Guid                       RequestId          { get; init; }
    public required int                        PickListAbsEntry   { get; init; }
    public required decimal                    ReleasedQty        { get; init; }
    public required decimal                    PickedQty          { get; init; }
    public required string                     Status             { get; init; }
    public required bool                       IsNew              { get; init; }
    public required bool                       AlreadyApplied     { get; init; }
    public required bool                       RecoveredFromSap   { get; init; }
    public required Pkl1LineState?             SapPostState       { get; init; }
    public required IReadOnlyList<BinPickAlloc> BinsUsed         { get; init; }
    public          PostPickAutomationResult?  PostPickAutomation { get; init; }
}

public sealed class PickListStateSnapshot
{
    public required Guid                        RequestId        { get; init; }
    public required int                         PickListAbsEntry { get; init; }
    public required PickListRecordModel         MolasRecord      { get; init; }
    public required Pkl1LineState?              SapPkl1State     { get; init; }
    public required SoUdfState?                 SapSoUdfs        { get; init; }
    public required OitwState?                  SapOitw          { get; init; }
    public required IReadOnlyList<BinPickAlloc> SapBins          { get; init; }
}

public sealed class RepickCreateResult
{
    public required Guid           RequestId        { get; init; }
    public required int            PickListAbsEntry { get; init; }
    public required long           PlrId            { get; init; }
    public required bool           IsNew            { get; init; }
    public required bool           IsCrashRecovery  { get; init; }
    public required int            SoLineNum        { get; init; }
    public required string         ItemCode         { get; init; }
    public required string         WhsCode          { get; init; }
    public required decimal        ReleasedQty      { get; init; }
    public required string         Status           { get; init; }
    public          string?        LineDescription  { get; init; }
    public          Pkl1LineState? SapPkl1          { get; init; }
}

public sealed class SapPickListUpdateException : Exception
{
    public int SapErrorCode { get; }
    public SapPickListUpdateException(int rc, string message) : base(message) => SapErrorCode = rc;
}
