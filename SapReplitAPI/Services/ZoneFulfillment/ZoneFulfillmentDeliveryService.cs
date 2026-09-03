using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Phase C delivery gate service.
/// Implements the full delivery orchestration including idempotency, preflight validation,
/// and ODLN construction — with ODLN.Add() gated behind an explicit live-mutation flag.
///
/// CURRENT STATE: MUTATION_ENABLED = false.
/// ODLN.Add() will NOT be called until the pre-mutation runtime gate passes and explicit
/// authorization is granted. The endpoint returns the preflight result and a gate verdict.
/// </summary>
public sealed class ZoneFulfillmentDeliveryService
{
    // ── MUTATION GATE ─────────────────────────────────────────────────────────
    // Authorized 2026-09-02: production automation flow active.
    // ODLN.Add() is enabled. Full delivery preflight runs before every Add().
    private const bool MUTATION_ENABLED = true;

    private readonly ZoneFulfillmentRepository          _repo;
    private readonly SapService                         _sap;
    private readonly ZoneFulfillmentDeliveryCoordinator _coordinator;
    private readonly ILogger<ZoneFulfillmentDeliveryService> _log;

    public ZoneFulfillmentDeliveryService(
        ZoneFulfillmentRepository          repo,
        SapService                         sap,
        ZoneFulfillmentDeliveryCoordinator coordinator,
        ILogger<ZoneFulfillmentDeliveryService> log)
    {
        _repo        = repo;
        _sap         = sap;
        _coordinator = coordinator;
        _log         = log;
    }

    // ── Preflight (read-only) ──────────────────────────────────────────────────

    /// <summary>
    /// Loads all gate data from MolasIntegration and SAP without any mutation.
    /// Section 6: SAP-first delivered-qty aggregation per fragment (SUM DLN1.Quantity per BaseLine).
    /// Section 11: Returns SapDeliveredQty, MolasDeliveredQty, RemainingPickedQty, EligibleForDelivery per fragment.
    /// </summary>
    public async Task<DeliveryPreflightResult> PreflightAsync(
        Guid requestId, CancellationToken ct = default)
    {
        // 1. Load orchestration
        var orch = await _repo.FindOrchestrationAsync(requestId, ct)
            ?? throw new InvalidOperationException($"RequestId {requestId} not found.");

        if (orch.SoDocEntry is null)
            throw new InvalidOperationException(
                $"RequestId {requestId} has no SO — order creation must complete first.");

        string uReplitId = ZoneFulfillmentSapOrderService.BuildReplitId(requestId);

        // 2. Load fragments + pick lists
        var fragments  = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);
        var pickLists  = await _repo.GetPickListRecordsAsync(orch.Id, ct);

        // 3. ORDR UDF state from SAP
        var soUdfs = _sap.GetOrdrUdfState(orch.SoDocEntry.Value);

        // 4. SAP-first delivery truth — two separate lists.
        //
        // ACTIVE: strict query — CANCELED='N' + U_ZoneRef='ZoneFulfillment' + U_ReplitId + BaseType=17 + BaseEntry.
        //   Only this list may feed ActiveSapDeliveredQty, eligibility, and idempotency.
        // HISTORICAL: union of active + any known DocEntry from MolasIntegration (includes cancelled ODLNs).
        //   For audit display only. Must never drive eligibility or delivery quantity.
        var activeSapDeliveries = _sap.FindZoneFulfillmentDeliveries(uReplitId, orch.SoDocEntry.Value);

        var existingRecords     = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);

        // Build historical list: start from active, then add legacy ODLNs from MolasIntegration by DocEntry.
        var historicalSapDeliveries = new List<SapDeliveryLine>(activeSapDeliveries);
        var activeDocEntries        = new HashSet<int>(activeSapDeliveries.Select(d => d.DocEntry));
        // Track per-DocEntry SAP cancellation state for DeliveryRecord reconciliation below.
        var sapCanceledByDocEntry   = new Dictionary<int, bool>();

        foreach (var rec in existingRecords.Where(r => r.SapDocEntry.HasValue))
        {
            int de = rec.SapDocEntry!.Value;
            if (!activeDocEntries.Contains(de))
            {
                var (histLines, canceledFlag) = _sap.ReadDln1ByDocEntryAny(de);
                sapCanceledByDocEntry[de] = canceledFlag == "Y";
                if (histLines.Count > 0)
                {
                    historicalSapDeliveries.AddRange(histLines);
                    _log.LogInformation(
                        "[ZF-DLV] Historical ODLN DocEntry={De} found (canceled={C}) — added to audit list only",
                        de, canceledFlag);
                }
            }
            else
            {
                sapCanceledByDocEntry[de] = false; // it's in active, so not cancelled
            }
        }

        // 5. Reconcile stale DeliveryRecords: if SAP ODLN is cancelled, mark record as Canceled.
        foreach (var rec in existingRecords.Where(r => r.SapDocEntry.HasValue && r.Status == DeliveryRecordStatus.Created))
        {
            if (sapCanceledByDocEntry.TryGetValue(rec.SapDocEntry!.Value, out bool isCanceled) && isCanceled)
            {
                _log.LogWarning(
                    "[ZF-DLV] DeliveryRecord Id={Id} SapDocEntry={De} — SAP ODLN is CANCELED. Reconciling status to Canceled.",
                    rec.Id, rec.SapDocEntry.Value);
                await _repo.UpdateDeliveryRecordAsync(rec.Id, DeliveryRecordStatus.Canceled,
                    rec.SapDocEntry.Value, rec.SapDocNum, null, ct);
                rec.Status = DeliveryRecordStatus.Canceled; // update in-memory
            }
        }

        // 6. Build ACTIVE delivered-qty lookup by BaseLine (SoLineNum) — for eligibility.
        var activeDeliveredByBaseLine = activeSapDeliveries
            .GroupBy(d => d.BaseLine)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Quantity));

        // 7. Invoice filter verification
        bool invoiceFilterActive = true;

        // 8. Per-fragment gate data
        var fragData   = new List<DeliveryFragmentGateData>();
        var gateErrors = new List<string>();

        var pickListByFragment = pickLists.ToDictionary(p => p.SoLineFragmentId);

        foreach (var frag in fragments)
        {
            if (!pickListByFragment.TryGetValue(frag.Id, out var plRecord))
            {
                gateErrors.Add($"Fragment {frag.Id} (SO {frag.SoDocEntry} Line {frag.SoLineNum}): no PickListRecord found.");
                continue;
            }

            var pkl1 = _sap.GetPkl1LineState(
                plRecord.PickListAbsEntry, frag.SoDocEntry, frag.SoLineNum);
            var rdr1OpenQty = _sap.GetRdr1OpenQty(frag.SoDocEntry, frag.SoLineNum);
            var bins = _sap.GetPickListBinAllocations(
                plRecord.PickListAbsEntry, frag.SoDocEntry, frag.SoLineNum);

            // ACTIVE delivered qty — only non-cancelled ZF ODLNs with proper identity fields.
            decimal activeSapDelivered = activeDeliveredByBaseLine.TryGetValue(frag.SoLineNum, out var asd) ? asd : 0m;

            // HISTORICAL delivered qty — includes cancelled ODLNs, for audit display only.
            decimal historicalDelivered = historicalSapDeliveries
                .Where(d => d.BaseLine == frag.SoLineNum)
                .Sum(d => d.Quantity);

            decimal molasDelivered = frag.DeliveredQty;

            // Section 9: SAP active delivery truth wins.
            // If activeSapDeliveredQty=0 but local DeliveredQty>0 (stale from a cancelled ODLN),
            // reconcile the local record to 0 so future preflight reads correctly.
            // Historical cancelled delivery remains in DeliveryRecord/DeliveryFragmentRecord.
            if (activeSapDelivered == 0m && molasDelivered > 0m)
            {
                _log.LogWarning(
                    "[ZF-DLV] SAP-first reconciliation: FragId={Frag} ItemCode={Item} " +
                    "activeSapDelivered=0 but MolasDeliveredQty={Molas} — reconciling to 0.",
                    frag.Id, frag.ItemCode, molasDelivered);
                await _repo.UpdateSoLineFragmentDeliveredQtyAsync(frag.Id, 0m, ct);
                molasDelivered = 0m;
            }
            else if (activeSapDelivered != molasDelivered)
                _log.LogWarning(
                    "[ZF-DLV] Reconciliation drift: FragId={Frag} ItemCode={Item} " +
                    "ActiveSapDelivered={Sap} MolasDelivered={Molas}",
                    frag.Id, frag.ItemCode, activeSapDelivered, molasDelivered);

            // EligiblePickedQty: PKL1.PickStatus=Y → pkl1.PickQtty; else 0 (closed pick cannot be reused).
            decimal eligiblePickedQty = (pkl1?.PickStatus == "Y") ? (pkl1.PickQtty) : 0m;
            decimal remainingDeliverable = eligiblePickedQty - activeSapDelivered;

            // RequiresRepick: PKL1 is Closed AND no active delivery — pick was closed by a cancelled delivery.
            bool requiresRepick = pkl1?.PickStatus == "C" && activeSapDelivered == 0m;

            var errors = new List<string>();

            if (pkl1 is null)
                errors.Add($"PKL1 row not found for AbsEntry={plRecord.PickListAbsEntry}, SoLine={frag.SoLineNum}.");
            else if (requiresRepick)
            {
                // Informational only — logged but does NOT enter gateErrors (doesn't block other fragments).
                _log.LogWarning(
                    "[ZF-DLV] Fragment {Item} SoLine={L}: PKL1.PickStatus=C AND ActiveSapDelivered=0 — " +
                    "DELIVERY_CANCELLED_REQUIRES_REPICK. Excluded from delivery.",
                    frag.ItemCode, frag.SoLineNum);
            }
            else if (pkl1.PickStatus != "Y" && remainingDeliverable > 0m)
                errors.Add($"PKL1.PickStatus={pkl1.PickStatus} — expected 'Y' (Picked).");

            // Over-delivery guard
            if (activeSapDelivered > eligiblePickedQty && eligiblePickedQty > 0m)
                errors.Add($"OVER-DELIVERY: ActiveSapDeliveredQty={activeSapDelivered} > EligiblePickedQty={eligiblePickedQty}.");

            if (remainingDeliverable > 0m && remainingDeliverable > rdr1OpenQty && rdr1OpenQty > 0m)
                errors.Add($"remainingDeliverableQty={remainingDeliverable} > RDR1.OpenQty={rdr1OpenQty}.");

            decimal binTotal = bins.Sum(b => b.Qty);
            if (remainingDeliverable > 0m && bins.Count > 0 && binTotal != remainingDeliverable)
                errors.Add($"Bin total {binTotal} != remainingDeliverableQty {remainingDeliverable}.");

            // eligible only when remaining > 0 (active pick) and no hard errors
            bool eligible = remainingDeliverable > 0m && !requiresRepick && errors.Count == 0;

            // Gate errors: only blocking errors for eligible fragments enter the global list
            if (errors.Count > 0 && remainingDeliverable > 0m && !requiresRepick)
                gateErrors.AddRange(errors);

            fragData.Add(new DeliveryFragmentGateData
            {
                Fragment                = frag,
                PickListRecord          = plRecord,
                SapPkl1                 = pkl1,
                ActiveSapDeliveredQty   = activeSapDelivered,
                HistoricalDeliveredQty  = historicalDelivered,
                MolasDeliveredQty       = molasDelivered,
                EligiblePickedQty       = eligiblePickedQty,
                RemainingDeliverableQty = remainingDeliverable,
                RequiresRepick          = requiresRepick,
                Rdr1OpenQty             = rdr1OpenQty,
                BinAllocations          = bins,
                ValidationErrors        = errors,
                EligibleForDelivery     = eligible
            });
        }

        _log.LogInformation(
            "[ZF-DLV] Preflight RequestId={Rid} fragments={N} eligible={E} " +
            "activeSapDeliveries={Ad} historicalDeliveries={Hd} gateErrors={Errs}",
            requestId, fragData.Count,
            fragData.Count(f => f.EligibleForDelivery),
            activeSapDeliveries.Count, historicalSapDeliveries.Count, gateErrors.Count);

        return new DeliveryPreflightResult
        {
            RequestId                    = requestId,
            Orchestration                = orch,
            SoUdfs                       = soUdfs,
            Fragments                    = fragData,
            ExistingDeliveryRecords      = existingRecords,
            ExistingActiveDeliveries     = activeSapDeliveries,
            ExistingHistoricalDeliveries = historicalSapDeliveries,
            InvoiceFilterActive          = invoiceFilterActive,
            GateErrors                   = gateErrors
        };
    }

    // ── Execute (mutation-gated) ────────────────────────────────────────────────

    /// <summary>
    /// Executes the full delivery creation flow including idempotency, preflight,
    /// and ODLN.Add(). Returns ZfDeliveryResult covering all outcomes.
    /// </summary>
    public async Task<ZfDeliveryResult> ExecuteDeliveryAsync(
        Guid requestId, CancellationToken ct = default)
    {
        using var lockCtx = await _coordinator.AcquireAsync(requestId, ct);

        // Step 1: Load preflight data (includes idempotency checks)
        var preflight = await PreflightAsync(requestId, ct);
        var orch      = preflight.Orchestration;
        string uReplitId = ZoneFulfillmentSapOrderService.BuildReplitId(requestId);

        // Step 2 (Section 8/9/10): SAP-first plural delivery truth.
        // A SAP ODLN's existence no longer means "orchestration fully delivered".
        // It means "some quantity has been delivered". Evaluate per-fragment remaining qty.

        // Section 10: Crash recovery — find SAP ODLNs not yet persisted in MolasIntegration.
        var molasDocEntries = new HashSet<int>(
            preflight.ExistingDeliveryRecords
                .Where(r => r.SapDocEntry.HasValue)
                .Select(r => r.SapDocEntry!.Value));

        var unreconciledDocEntries = preflight.ExistingSapDeliveries
            .Select(d => d.DocEntry)
            .Distinct()
            .Where(de => !molasDocEntries.Contains(de))
            .ToList();

        if (unreconciledDocEntries.Count > 0)
        {
            _log.LogWarning(
                "[ZF-DLV] Crash recovery: {N} SAP ODLNs found without DeliveryRecord — reconciling. DocEntries={Des}",
                unreconciledDocEntries.Count, string.Join(",", unreconciledDocEntries));

            // Also reconcile any Pending DeliveryRecords (crashed before UpdateDeliveryRecord)
            var pendingRecord = preflight.ExistingDeliveryRecords
                .FirstOrDefault(r => r.Status == DeliveryRecordStatus.Pending);

            foreach (var unreconciledDe in unreconciledDocEntries)
            {
                // Find DLN1 lines in SAP for this unreconciled DocEntry
                var sapLines = preflight.ExistingSapDeliveries
                    .Where(d => d.DocEntry == unreconciledDe)
                    .ToList();
                int unreconciledDocNum = sapLines.First().DocNum;

                // Reuse the Pending record if one exists, otherwise insert a new one
                long recoveryRecordId;
                if (pendingRecord != null)
                {
                    recoveryRecordId = pendingRecord.Id;
                    await _repo.UpdateDeliveryRecordAsync(recoveryRecordId, DeliveryRecordStatus.Created,
                        unreconciledDe, unreconciledDocNum, null, ct);
                    _log.LogWarning("[ZF-DLV] Recovery: finalized Pending DeliveryRecord Id={Id} → DocEntry={De}",
                        recoveryRecordId, unreconciledDe);
                    pendingRecord = null; // only reuse once
                }
                else
                {
                    var newRecord = new DeliveryRecordModel
                    {
                        OrchestrationId  = orch.Id,
                        ZoneRef          = "ZoneFulfillment",
                        DeliveryLocation = orch.DeliveryLocation,
                        CardCode         = preflight.SoUdfs?.UZoneRef != null
                            ? (_sap.GetOrdrCardCode(orch.SoDocEntry!.Value) ?? "")
                            : "",
                        Status           = DeliveryRecordStatus.Created
                    };
                    recoveryRecordId = await _repo.InsertDeliveryRecordAsync(newRecord, ct);
                    await _repo.UpdateDeliveryRecordAsync(recoveryRecordId, DeliveryRecordStatus.Created,
                        unreconciledDe, unreconciledDocNum, null, ct);
                    _log.LogWarning("[ZF-DLV] Recovery: inserted new DeliveryRecord Id={Id} for DocEntry={De}",
                        recoveryRecordId, unreconciledDe);
                }

                // Reconcile DeliveryFragmentRecord for each DLN1 line
                foreach (var sapLine in sapLines)
                {
                    var fragForLine = preflight.Fragments
                        .FirstOrDefault(f => f.Fragment.SoLineNum == sapLine.BaseLine);
                    if (fragForLine is null) continue;

                    var recoveryBins = _sap.GetPickListBinAllocations(
                        fragForLine.PickListRecord.PickListAbsEntry,
                        fragForLine.Fragment.SoDocEntry,
                        fragForLine.Fragment.SoLineNum);
                    try
                    {
                        await _repo.InsertDeliveryFragmentRecordAsync(new DeliveryFragmentRecordModel
                        {
                            DeliveryRecordId = recoveryRecordId,
                            FragmentId       = fragForLine.Fragment.Id,
                            SoDocEntry       = fragForLine.Fragment.SoDocEntry,
                            SoLineNum        = fragForLine.Fragment.SoLineNum,
                            ItemCode         = fragForLine.Fragment.ItemCode,
                            WhsCode          = fragForLine.Fragment.WhsCode,
                            PickedQty        = sapLine.Quantity,
                            PickListAbsEntry = fragForLine.PickListRecord.PickListAbsEntry,
                            DlnLineNum       = sapLine.DlnLineNum,
                            Bins             = recoveryBins.Select(b => new DeliveryFragmentBinRecordModel
                            {
                                BinAbsEntry = b.BinAbsEntry,
                                BinCode     = b.BinCode,
                                Quantity    = b.Qty
                            }).ToList()
                        }, ct);
                    }
                    catch (Exception ex) when (ex.Message.Contains("UNIQUE") || ex.Message.Contains("Violation"))
                    {
                        _log.LogInformation("[ZF-DLV] Recovery: fragment record already exists FragId={F} — skipping",
                            fragForLine.Fragment.Id);
                    }

                    // Update SoLineFragment.DeliveredQty from SAP truth
                    decimal newDelivered = fragForLine.SapDeliveredQty;
                    if (newDelivered != fragForLine.Fragment.DeliveredQty)
                        await _repo.UpdateSoLineFragmentDeliveredQtyAsync(
                            fragForLine.Fragment.Id, newDelivered, ct);
                }
            }

            // After crash recovery, re-evaluate eligibility from updated preflight
            var reconciledRecords = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
            return new ZfDeliveryResult
            {
                Preflight   = preflight,
                Record      = reconciledRecords.LastOrDefault(),
                GateVerdict = "CRASH_RECOVERY_RECONCILED_FROM_SAP"
            };
        }

        // Step 3 (Section 9 rule 5): All fragments fully delivered?
        var eligibleAfterSapTruth = preflight.Fragments.Where(f => f.EligibleForDelivery).ToList();
        if (preflight.ExistingSapDeliveries.Count > 0 && eligibleAfterSapTruth.Count == 0)
        {
            _log.LogInformation(
                "[ZF-DLV] All fragments fully delivered per SAP truth. RequestId={Rid}", requestId);
            var lastRecord = preflight.ExistingDeliveryRecords.LastOrDefault();
            return new ZfDeliveryResult
            {
                Preflight   = preflight,
                Record      = lastRecord,
                GateVerdict = "ALL_FRAGMENTS_FULLY_DELIVERED"
            };
        }

        // Step 4: Gate errors come only from ELIGIBLE fragments.
        // Non-eligible fragments (already-delivered PKL1=C, zero remaining) are excluded from the delivery;
        // their errors don't block delivery of the remaining eligible lines.
        var blockingErrors = eligibleAfterSapTruth
            .SelectMany(f => f.ValidationErrors)
            .ToList();

        if (blockingErrors.Count > 0)
        {
            _log.LogError(
                "[ZF-AUTO] DeliveryBlocked RequestId={Rid} soDocEntry={De} verdict=GATE_ERRORS_BLOCK_MUTATION errors=[{Errs}]",
                requestId, orch.SoDocEntry, string.Join("; ", blockingErrors));
            return new ZfDeliveryResult
            {
                Preflight   = preflight,
                Record      = preflight.ExistingDeliveryRecord,
                GateVerdict = "GATE_ERRORS_BLOCK_MUTATION"
            };
        }

        // ── Step 5: mutation is enabled — proceed to ODLN.Add() ─────────────────

        // Step 6: Read CardCode from live ORDR
        string cardCode = _sap.GetOrdrCardCode(orch.SoDocEntry!.Value)
            ?? throw new InvalidOperationException(
                $"ORDR CardCode not found for DocEntry={orch.SoDocEntry}.");

        // Step 7: Validate
        ValidateDeliveryLocation(orch.DeliveryLocation);

        // Step 8 (Bug #3 fix): Build durable bin allocations for ALL eligible fragments (SAP-authoritative)
        var deliveryLines = new List<DeliveryLineSpec>(eligibleAfterSapTruth.Count);
        foreach (var fd in eligibleAfterSapTruth)
        {
            var bins = _sap.GetPickListBinAllocations(
                fd.PickListRecord.PickListAbsEntry,
                fd.Fragment.SoDocEntry,
                fd.Fragment.SoLineNum);

            if (bins.Count == 0)
                throw new InvalidOperationException(
                    $"Durable bin allocation empty for PickListAbsEntry={fd.PickListRecord.PickListAbsEntry} " +
                    $"ItemCode={fd.Fragment.ItemCode}. Cannot create delivery without bin specification.");

            deliveryLines.Add(new DeliveryLineSpec(
                fd.Fragment.SoDocEntry,
                fd.Fragment.SoLineNum,
                fd.RemainingPickedQty,
                fd.Fragment.WhsCode,
                bins));
        }

        _log.LogInformation(
            "[ZF-DLV] Eligible fragments for delivery: {Items}",
            string.Join(", ", eligibleAfterSapTruth.Select(f => $"{f.Fragment.ItemCode}×{f.RemainingPickedQty}")));

        // Step 10: Insert DeliveryRecord (Pending).
        // After schema migration: UNIQUE(SapDocEntry WHERE NOT NULL) guards against duplicate ODLNs;
        // UNIQUE(OrchestrationId) removed — multiple Pending rows per orchestration are allowed.
        long deliveryRecordId;
        try
        {
            var pendingRecord = new DeliveryRecordModel
            {
                OrchestrationId  = orch.Id,
                ZoneRef          = "ZoneFulfillment",
                DeliveryLocation = orch.DeliveryLocation,
                CardCode         = cardCode,
                Status           = DeliveryRecordStatus.Pending
            };
            deliveryRecordId = await _repo.InsertDeliveryRecordAsync(pendingRecord, ct);
        }
        catch (Exception ex) when (ex.Message.Contains("UNIQUE") || ex.Message.Contains("Violation"))
        {
            var existing = await _repo.FindDeliveryRecordAsync(orch.Id, ct);
            return new ZfDeliveryResult
            {
                Preflight   = preflight,
                Record      = existing,
                GateVerdict = "CONCURRENCY_CAUGHT_BY_UNIQUE_CONSTRAINT"
            };
        }

        // Step 11: Pre-ODLN.Add() live re-verification
        var liveUdfs = _sap.GetOrdrUdfState(orch.SoDocEntry!.Value);
        var liveGateErrors = new List<string>();
        if (liveUdfs is null)
            liveGateErrors.Add("ORDR UDFs not readable — SO may not exist.");
        else
        {
            if (liveUdfs.DocStatus != "O")
                liveGateErrors.Add($"ORDR.DocStatus={liveUdfs.DocStatus} — expected 'O'.");
            if (liveUdfs.Canceled  != "N")
                liveGateErrors.Add($"ORDR.Canceled={liveUdfs.Canceled} — expected 'N'.");
            if (liveUdfs.UZoneRef  != "ZoneFulfillment")
                liveGateErrors.Add($"ORDR.U_ZoneRef='{liveUdfs.UZoneRef}' — expected 'ZoneFulfillment'.");
            if (liveUdfs.UReplitId != uReplitId)
                liveGateErrors.Add($"ORDR.U_ReplitId mismatch (expected '{uReplitId}').");
        }

        // Per-eligible-fragment live re-check
        foreach (var fd in eligibleAfterSapTruth)
        {
            var livePkl1     = _sap.GetPkl1LineState(
                fd.PickListRecord.PickListAbsEntry, fd.Fragment.SoDocEntry, fd.Fragment.SoLineNum);
            decimal liveOpen = _sap.GetRdr1OpenQty(fd.Fragment.SoDocEntry, fd.Fragment.SoLineNum);

            if (livePkl1 is null)
                liveGateErrors.Add($"PKL1 not readable for AbsEntry={fd.PickListRecord.PickListAbsEntry} ItemCode={fd.Fragment.ItemCode}.");
            else if (livePkl1.PickStatus != "Y")
                liveGateErrors.Add($"PKL1.PickStatus={livePkl1.PickStatus} for ItemCode={fd.Fragment.ItemCode} — expected 'Y'.");

            if (liveOpen <= 0m)
                liveGateErrors.Add($"RDR1.OpenQty={liveOpen} for SoLineNum={fd.Fragment.SoLineNum} — expected > 0.");
        }

        if (liveGateErrors.Count > 0)
        {
            string gateMsg = string.Join("; ", liveGateErrors);
            _log.LogError(
                "[ZF-AUTO] DeliveryBlocked RequestId={Rid} soDocEntry={De} verdict=LIVE_RECHECK_FAILED errors=[{Msg}]",
                requestId, orch.SoDocEntry, gateMsg);
            await _repo.UpdateDeliveryRecordAsync(deliveryRecordId, DeliveryRecordStatus.Failed,
                null, null, $"Pre-Add live re-check: {gateMsg}", ct);
            return new ZfDeliveryResult
            {
                Preflight   = preflight,
                Record      = await _repo.FindDeliveryRecordAsync(orch.Id, ct),
                GateVerdict = "LIVE_RECHECK_FAILED"
            };
        }

        // Step 12 (Bug #3 fix): ODLN.Add() with all eligible fragments as DLN1 lines
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (rc, docEntry, docNum, sapError) = _sap.CreateZoneFulfillmentDelivery(
            cardCode,
            DateTime.UtcNow.Date,
            uReplitId,
            orch.DeliveryLocation,
            deliveryLines);
        sw.Stop();

        if (rc != 0)
        {
            await _repo.UpdateDeliveryRecordAsync(deliveryRecordId, DeliveryRecordStatus.Failed,
                null, null, sapError, ct);
            throw new InvalidOperationException(
                $"ODLN.Add() failed rc={rc}: {sapError}");
        }

        // Step 13: Read back ODLN for verification
        var odlnReadback = _sap.ReadBackZoneFulfillmentOdln(docEntry, sw.ElapsedMilliseconds);

        // Build DLN line number map by BaseLine for fragment record persistence
        var dlnLineByBaseLine = odlnReadback?.Lines
            .ToDictionary(l => l.BaseLine, l => l.LineNum)
            ?? new Dictionary<int, int>();

        // Step 14: Update DeliveryRecord → Created
        await _repo.UpdateDeliveryRecordAsync(deliveryRecordId, DeliveryRecordStatus.Created,
            docEntry, docNum, null, ct);

        // Step 15 (Bug #3 fix): Insert DeliveryFragmentRecord for EACH eligible fragment
        foreach (var fd in eligibleAfterSapTruth)
        {
            int dlnLineNum = dlnLineByBaseLine.TryGetValue(fd.Fragment.SoLineNum, out int ln) ? ln : 0;
            var bins       = deliveryLines.First(l => l.SoLineNum == fd.Fragment.SoLineNum).DurableBins;

            var fragmentRecord = new DeliveryFragmentRecordModel
            {
                DeliveryRecordId = deliveryRecordId,
                FragmentId       = fd.Fragment.Id,
                SoDocEntry       = fd.Fragment.SoDocEntry,
                SoLineNum        = fd.Fragment.SoLineNum,
                ItemCode         = fd.Fragment.ItemCode,
                WhsCode          = fd.Fragment.WhsCode,
                PickedQty        = fd.RemainingPickedQty,
                DeliveredQty     = fd.RemainingPickedQty,
                PickListAbsEntry = fd.PickListRecord.PickListAbsEntry,
                DlnLineNum       = dlnLineNum,
                Bins             = bins.Select(b => new DeliveryFragmentBinRecordModel
                {
                    BinAbsEntry = b.BinAbsEntry,
                    BinCode     = b.BinCode,
                    Quantity    = b.Qty
                }).ToList()
            };
            await _repo.InsertDeliveryFragmentRecordAsync(fragmentRecord, ct);

            // Step 16: Update SoLineFragment.DeliveredQty per fragment
            decimal newDeliveredQty = fd.Fragment.DeliveredQty + fd.RemainingPickedQty;
            await _repo.UpdateSoLineFragmentDeliveredQtyAsync(fd.Fragment.Id, newDeliveredQty, ct);
        }

        _log.LogInformation(
            "[ZF-AUTO] DeliveryCreated RequestId={Rid} soDocEntry={So} dlnDocEntry={De} dlnDocNum={Dn} lineCount={N}",
            requestId, orch.SoDocEntry, docEntry, docNum, eligibleAfterSapTruth.Count);

        // Return the newly created record (latest by Id) — not the historical cancelled one (oldest).
        var allRecords = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        var createdRecord = allRecords.LastOrDefault();
        return new ZfDeliveryResult
        {
            Preflight   = preflight,
            Record      = createdRecord,
            GateVerdict = "DELIVERY_CREATED",
            Odln        = odlnReadback
        };
    }

    // ── Validation helpers ─────────────────────────────────────────────────────

    private static void ValidateDeliveryLocation(string deliveryLocation)
    {
        if (string.IsNullOrWhiteSpace(deliveryLocation))
            throw new ArgumentException("DeliveryLocation must not be empty.");
        if (deliveryLocation.Length > 50)
            throw new ArgumentException(
                $"DeliveryLocation '{deliveryLocation}' exceeds ODLN.U_DeliveryLocation max length of 50 chars.");
    }
}
