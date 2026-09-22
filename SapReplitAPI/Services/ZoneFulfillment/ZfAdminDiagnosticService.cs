using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Phase 1A read-only aggregate diagnostic.
/// Combines MolasIntegration repo reads with live SAP state reads.
/// No mutations of any kind.
/// </summary>
public sealed class ZfAdminDiagnosticService
{
    private readonly ZoneFulfillmentRepository _repo;
    private readonly SapService                _sap;
    private readonly ILogger<ZfAdminDiagnosticService> _log;

    public ZfAdminDiagnosticService(
        ZoneFulfillmentRepository          repo,
        SapService                         sap,
        ILogger<ZfAdminDiagnosticService>  log)
    {
        _repo = repo;
        _sap  = sap;
        _log  = log;
    }

    // ── Main aggregate ────────────────────────────────────────────────────────

    public async Task<ZfOrderDiagnosticResult?> GetOrderDiagnosticAsync(
        int soDocEntry, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch is null) return null;

        var fragments  = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        var allPlrs    = await _repo.GetPickListRecordsAsync(orch.Id, ct);
        var deliveries = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        var activeReplan = await _repo.FindActiveReplanOperationAsync(soDocEntry, ct);

        // Live SAP reads — track failure separately so classifier can suppress false FragmentRdr1Missing
        bool sapRdr1ReadFailed = false;
        List<SapService.Rdr1Line> rdr1Lines;
        try { rdr1Lines = _sap.GetRdr1AllLines(soDocEntry); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfAdmin] GetRdr1AllLines failed for DocEntry={DocEntry}", soDocEntry);
            rdr1Lines = [];
            sapRdr1ReadFailed = true;
        }

        // Build per-fragment diagnostics
        var fragDiags = BuildFragmentDiagnostics(fragments, allPlrs, rdr1Lines, sapRdr1ReadFailed);

        // Build delivery diagnostics with live ODLN state
        var delivDiags = BuildDeliveryDiagnostics(deliveries);

        // Invoice records for successful deliveries
        var invoiceDiags = await BuildInvoiceDiagnosticsAsync(deliveries, ct);

        var replanDiag = activeReplan is null ? null : ToReplanDiag(activeReplan);

        // Classify
        var classInput = BuildClassifierInput(orch, activeReplan, fragDiags, delivDiags, invoiceDiags, sapRdr1ReadFailed);
        var verdict    = ZfConsistencyClassifier.Classify(classInput);
        var actions    = ZfConsistencyClassifier.RecommendActions(verdict);

        return new ZfOrderDiagnosticResult
        {
            OrchestrationId  = orch.Id,
            RequestId        = orch.RequestId,
            SoDocEntry       = soDocEntry,
            SoDocNum         = orch.SoDocNum,
            State            = orch.State,
            DeliveryLocation = orch.DeliveryLocation,
            FailureKind      = orch.FailureKind,
            ErrorMessage     = orch.ErrorMessage,
            UZoneRef         = orch.U_ReplitId is not null ? "ZoneFulfillment" : null,
            UReplitId        = orch.U_ReplitId,
            OriginWhsCode    = orch.OriginWhsCode,
            AllocationReason = orch.AllocationReason,
            CreatedAtUtc     = orch.CreatedAtUtc,
            UpdatedAtUtc     = orch.UpdatedAtUtc,
            Fragments        = fragDiags,
            Deliveries       = delivDiags,
            Invoices         = invoiceDiags,
            ActiveReplan     = replanDiag,
            Consistency      = verdict,
            AvailableActions = actions,
        };
    }

    // ── Timeline ──────────────────────────────────────────────────────────────

    public async Task<ZfOrderTimelineResult?> GetOrderTimelineAsync(
        int soDocEntry, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch is null) return null;

        var deliveries   = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        var replanOps    = await _repo.GetReplanOperationsBySoDocEntryAsync(soDocEntry, ct);
        var fragments    = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);
        var invoiceDiags = await BuildInvoiceDiagnosticsAsync(deliveries, ct);

        var events = new List<ZfTimelineEntry>();

        events.Add(new ZfTimelineEntry(
            orch.CreatedAtUtc, "ORCHESTRATION_CREATED",
            $"RequestId={orch.RequestId} DeliveryLocation={orch.DeliveryLocation}", null));

        // Fragment WHS changes
        foreach (var f in fragments.Where(f => f.WhsChangedAtUtc.HasValue).OrderBy(f => f.WhsChangedAtUtc))
            events.Add(new ZfTimelineEntry(
                f.WhsChangedAtUtc!.Value, "FRAGMENT_WHS_CHANGED",
                $"LineNum={f.SoLineNum} {f.OriginalWhsCode}→{f.WhsCode}",
                $"ChangedBy={f.WhsChangedBy}"));

        // Replan operations (each step is just start/complete for summary)
        foreach (var r in replanOps)
        {
            events.Add(new ZfTimelineEntry(
                r.StartedAtUtc, "REPLAN_STARTED",
                $"OperationId={r.OperationId} ChangedBy={r.ChangedBy}", null));

            if (r.CompletedAtUtc.HasValue)
                events.Add(new ZfTimelineEntry(
                    r.CompletedAtUtc.Value, "REPLAN_COMPLETED",
                    $"OperationId={r.OperationId} FinalStep={r.CurrentStep}",
                    r.LastError));
            else if (r.LastError is not null)
                events.Add(new ZfTimelineEntry(
                    r.StartedAtUtc, "REPLAN_ERROR",
                    $"OperationId={r.OperationId} Step={r.CurrentStep}",
                    r.LastError));
        }

        // Delivery records
        foreach (var d in deliveries)
        {
            string verb = d.Status switch
            {
                DeliveryRecordStatus.Created  => "DELIVERY_CREATED",
                DeliveryRecordStatus.Failed   => "DELIVERY_FAILED",
                DeliveryRecordStatus.Canceled => "DELIVERY_CANCELED",
                _                             => "DELIVERY_ATTEMPT"
            };

            var detail = d.Status == DeliveryRecordStatus.Created
                ? $"ODLN DocEntry={d.SapDocEntry}"
                : d.SapErrorMessage;

            events.Add(new ZfTimelineEntry(
                d.CreatedAtUtc, verb,
                $"DeliveryRecord Id={d.Id} Status={d.Status}", detail));
        }

        // Invoice records
        foreach (var inv in invoiceDiags)
        {
            string verb = inv.Status == InvoiceRecordStatus.Created
                ? "INVOICE_CREATED"
                : "INVOICE_FAILED";

            events.Add(new ZfTimelineEntry(
                inv.CreatedAtUtc, verb,
                $"InvoiceRecord Id={inv.Id} DeliveryDocEntry={inv.DeliveryDocEntry}",
                inv.Status == InvoiceRecordStatus.Created
                    ? $"OINV DocEntry={inv.SapDocEntry}"
                    : inv.SapErrorMessage));
        }

        // Orchestration state as final event if terminal
        if (orch.State is OrchestrationState.Delivered
                       or OrchestrationState.Canceled
                       or OrchestrationState.Failed
                       or OrchestrationState.UnknownOutcome)
            events.Add(new ZfTimelineEntry(
                orch.UpdatedAtUtc, "STATE_CHANGED",
                $"State → {orch.State}", orch.ErrorMessage));

        events.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        return new ZfOrderTimelineResult
        {
            SoDocEntry = soDocEntry,
            RequestId  = orch.RequestId,
            Events     = events,
        };
    }

    // ── Incidents ─────────────────────────────────────────────────────────────

    public async Task<ZfOrderIncidentResult?> GetOrderIncidentsAsync(
        int soDocEntry, CancellationToken ct = default)
    {
        var orch = await _repo.FindOrchestrationBySoDocEntryAsync(soDocEntry, ct);
        if (orch is null) return null;

        var deliveries = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        var replanOps  = await _repo.GetReplanOperationsBySoDocEntryAsync(soDocEntry, ct);
        var fragments  = await _repo.GetSoLineFragmentsBySoDocEntryAsync(soDocEntry, ct);

        var failedDeliveries = BuildDeliveryDiagnostics(
            deliveries.Where(d => d.Status == DeliveryRecordStatus.Failed).ToList());

        var replanConflicts = replanOps
            .Where(r => r.CurrentStep == ReplanStep.RecoveryRequired)
            .Select(ToReplanDiag)
            .ToList();

        // Collect fragment WHS mismatches using live RDR1 data
        var rdr1Lines     = ReadSafely(() => _sap.GetRdr1AllLines(soDocEntry), "GetRdr1AllLines", soDocEntry, []);
        var rdr1ByLineNum = rdr1Lines.ToDictionary(l => l.LineNum);

        var consistencyIssues = new List<string>();
        foreach (var f in fragments)
        {
            if (!rdr1ByLineNum.TryGetValue(f.SoLineNum, out var rdr1)) continue;
            if (!string.Equals(f.WhsCode, rdr1.WhsCode, StringComparison.OrdinalIgnoreCase))
                consistencyIssues.Add(
                    $"LineNum={f.SoLineNum}: SoLineFragment.WhsCode={f.WhsCode} ≠ RDR1.WhsCode={rdr1.WhsCode}");
        }

        return new ZfOrderIncidentResult
        {
            SoDocEntry        = soDocEntry,
            RequestId         = orch.RequestId,
            FailedDeliveries  = failedDeliveries,
            ReplanConflicts   = replanConflicts,
            ConsistencyIssues = consistencyIssues,
        };
    }

    // ── Diagnosis Console ────────────────────────────────────────────────────

    public async Task<ZfDiagnosticIncidentsResult?> GetOrderDiagnosticIncidentsAsync(
        int soDocEntry, CancellationToken ct = default)
    {
        var diag = await GetOrderDiagnosticAsync(soDocEntry, ct);
        if (diag is null) return null;

        var incidents = BuildDiagnosticIncidents(diag);

        return new ZfDiagnosticIncidentsResult
        {
            SoDocEntry         = soDocEntry,
            RequestId          = diag.RequestId,
            OrchestrationState = diag.State,
            ConsistencyStatus  = diag.Consistency.Status,
            Incidents          = incidents,
            HasActiveIncidents = incidents.Any(i => i.IncidentStatus == ZfDiagnosticIncidentStatus.Active),
        };
    }

    internal static List<ZfDiagnosticIncident> BuildDiagnosticIncidents(ZfOrderDiagnosticResult diag)
    {
        var incidents = new List<ZfDiagnosticIncident>();
        var now = DateTime.UtcNow;

        bool isTerminal = diag.Consistency.Status is ZfConsistencyStatus.Completed
                                                   or ZfConsistencyStatus.Canceled
                                                   or ZfConsistencyStatus.Failed;

        // RDR1 missing: ZF fragment has no corresponding SAP Sales Order line.
        // SapRdr1ItemCode=null means the RDR1 row was absent (not a read failure — that's
        // suppressed at the classifier level via SapRdr1LookupFailed).
        foreach (var frag in diag.Fragments.Where(f => f.SapRdr1ItemCode is null))
        {
            string incidentStatus = isTerminal
                ? ZfDiagnosticIncidentStatus.Historical
                : ZfDiagnosticIncidentStatus.Active;

            var evidence = new List<string>
            {
                $"SoLineFragment Id={frag.Id} LineNum={frag.SoLineNum} ItemCode={frag.ItemCode} WhsCode={frag.FragmentWhsCode}",
                $"SAP RDR1 lookup: no row found for DocEntry={diag.SoDocEntry} LineNum={frag.SoLineNum}",
                $"Orchestration state: {diag.State}",
                $"Consistency status: {diag.Consistency.Status}",
            };

            incidents.Add(new ZfDiagnosticIncident
            {
                Severity         = ZfDiagnosticSeverity.High,
                Category         = ZfDiagnosticCategory.SapZfIntegrityDivergence,
                Code             = ZfConsistencyStatus.FragmentRdr1Missing,
                Title            = "SAP Sales Order Line Missing",
                Summary          = $"ZF fragment for item {frag.ItemCode} (LineNum={frag.SoLineNum}) has no " +
                                   $"corresponding SAP RDR1 line. The Sales Order line may have been deleted externally.",
                IncidentStatus   = incidentStatus,
                SapDocEntry      = diag.SoDocEntry,
                OrchestrationId  = diag.OrchestrationId,
                FragmentId       = frag.Id,
                SoLineNum        = frag.SoLineNum,
                AffectedItemCode = frag.ItemCode,
                Evidence         = evidence,
                CurrentOrchState    = diag.State,
                CurrentConsistency  = diag.Consistency.Status,
                Impact           = incidentStatus == ZfDiagnosticIncidentStatus.Active
                    ? "Fulfillment is blocked. RETRY_DELIVERY and RETRY_INVOICE cannot proceed until the SAP/ZF divergence is resolved."
                    : null,
                SafeActions      = [new ZfAvailableAction(
                    "MANUAL_RESOLUTION_REQUIRED", Enabled: false, MutationAvailable: false,
                    "The ZF fragment references an SAP Sales Order line that no longer exists. " +
                    "Resolve the SAP/ZF divergence manually before proceeding.")],
                BlockedActions   = incidentStatus == ZfDiagnosticIncidentStatus.Active
                    ? ["RETRY_DELIVERY", "RETRY_INVOICE"]
                    : [],
                DetectedAtUtc    = now,
                LastObservedAtUtc = now,
                IsResolved       = isTerminal,
                RecoveryEvidence = isTerminal ? $"Order reached terminal state: {diag.Consistency.Status}" : null,
            });
        }

        return incidents;
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private List<ZfFragmentDiagnostic> BuildFragmentDiagnostics(
        List<SoLineFragmentRecord>  fragments,
        List<PickListRecordModel>   allPlrs,
        List<SapService.Rdr1Line>   rdr1Lines,
        bool                        sapReadFailed = false)
    {
        var rdr1ByLine = rdr1Lines.ToDictionary(l => l.LineNum);
        var result     = new List<ZfFragmentDiagnostic>();

        foreach (var f in fragments)
        {
            // Most recent active PLR for this line (latest by Id, Status != Closed preferred)
            var plr = allPlrs
                .Where(p => p.SoLineNum == f.SoLineNum)
                .OrderByDescending(p => p.UpdatedAtUtc)
                .FirstOrDefault();

            rdr1ByLine.TryGetValue(f.SoLineNum, out var rdr1);

            bool whsMatch = rdr1 is not null &&
                string.Equals(f.WhsCode, rdr1.WhsCode, StringComparison.OrdinalIgnoreCase);

            // Live SAP PKL1/PKL2 (only if we have an active pick list)
            decimal pkl1PickQtty  = 0m;
            string? pkl1PickStatus = null;
            string? pkl2BinCode   = null;
            string? pkl2BinWhsCode = null;

            if (plr?.PickListAbsEntry > 0)
            {
                var (header, pkl1Lines, pkl2Bins) = ReadSafely(
                    () => _sap.GetPickListFullState(plr.PickListAbsEntry),
                    "GetPickListFullState", plr.PickListAbsEntry,
                    ((SapService.OpklHeader?)null, new List<SapService.Pkl1Row>(), new List<SapService.Pkl2Row>()));

                var pkl1 = pkl1Lines.FirstOrDefault(l =>
                    l.OrderEntry == f.SoDocEntry && l.OrderLine == f.SoLineNum);
                if (pkl1 is not null)
                {
                    pkl1PickQtty   = pkl1.PickQtty;
                    pkl1PickStatus = pkl1.PickStatus;
                }

                // First bin for this pick list (single-bin common case)
                var firstBin = pkl2Bins.FirstOrDefault();
                if (firstBin is not null)
                {
                    pkl2BinCode = firstBin.BinCode;
                    // Derive WHS from bin code prefix or OBIN lookup via GetPickListBinAllocations
                    pkl2BinWhsCode = GetBinWhsCode(plr.PickListAbsEntry, firstBin.BinAbs);
                }
            }

            result.Add(new ZfFragmentDiagnostic(
                Id:                f.Id,
                SoLineNum:         f.SoLineNum,
                ItemCode:          f.ItemCode,
                FragmentWhsCode:   f.WhsCode,
                SoLineQty:         f.SoLineQty,
                AllocatedQty:      f.AllocatedQty,
                DeliveredQty:      f.DeliveredQty,
                OriginalWhsCode:   f.OriginalWhsCode,
                WhsChangedAtUtc:   f.WhsChangedAtUtc,
                WhsChangedBy:      f.WhsChangedBy,
                SapRdr1ItemCode:   rdr1?.ItemCode,   // null = no RDR1 row (or read failed)
                SapRdr1WhsCode:    rdr1?.WhsCode,
                SapRdr1LineStatus: rdr1?.LineStatus,
                SapRdr1OpenQty:    rdr1?.OpenQty ?? 0m,
                WhsMatchesSap:     whsMatch,
                PickListWhsCode:   plr?.WhsCode,
                PickListAbsEntry:  plr?.PickListAbsEntry,
                PickListPickedQty: plr?.PickedQty ?? 0m,
                PickListStatus:    plr?.Status ?? "",
                SapPkl1PickQtty:   pkl1PickQtty,
                SapPkl1PickStatus: pkl1PickStatus,
                SapPkl2BinCode:    pkl2BinCode,
                SapPkl2BinWhsCode: pkl2BinWhsCode
            ));
        }

        return result;
    }

    private string? GetBinWhsCode(int absEntry, int binAbs)
    {
        // GetPickListBinAllocations returns OBIN.WhsCode
        try
        {
            var allocs = _sap.GetPickListBinAllocations([absEntry]);
            var match  = allocs.FirstOrDefault(a => a.BinAbsEntry == binAbs);
            return match?.WhsCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfAdmin] GetBinWhsCode failed AbsEntry={A} BinAbs={B}", absEntry, binAbs);
            return null;
        }
    }

    private List<ZfDeliveryDiagnostic> BuildDeliveryDiagnostics(List<DeliveryRecordModel> deliveries)
    {
        var result = new List<ZfDeliveryDiagnostic>();
        foreach (var d in deliveries)
        {
            SapService.OdlnHeaderState? odln = null;
            if (d.SapDocEntry.HasValue && d.Status == DeliveryRecordStatus.Created)
                odln = ReadSafely(() => _sap.GetOdlnHeaderState(d.SapDocEntry.Value),
                    "GetOdlnHeaderState", d.SapDocEntry.Value, (SapService.OdlnHeaderState?)null);

            result.Add(new ZfDeliveryDiagnostic(
                Id:              d.Id,
                Status:          d.Status,
                SapDocEntry:     d.SapDocEntry,
                SapDocNum:       d.SapDocNum,
                SapErrorMessage: d.SapErrorMessage,
                CreatedAtUtc:    d.CreatedAtUtc,
                UpdatedAtUtc:    d.UpdatedAtUtc,
                OdlnDocStatus:   odln?.DocStatus,
                OdlnCanceled:    odln?.Canceled,
                OdlnCardCode:    odln?.CardCode
            ));
        }
        return result;
    }

    private async Task<List<ZfInvoiceDiagnostic>> BuildInvoiceDiagnosticsAsync(
        List<DeliveryRecordModel> deliveries, CancellationToken ct)
    {
        var result = new List<ZfInvoiceDiagnostic>();
        foreach (var d in deliveries.Where(d => d.SapDocEntry.HasValue))
        {
            var inv = await _repo.FindInvoiceRecordByDeliveryDocEntryAsync(d.SapDocEntry!.Value, ct);
            if (inv is null) continue;

            result.Add(new ZfInvoiceDiagnostic(
                Id:              inv.Id,
                Status:          inv.Status,
                DeliveryDocEntry: inv.DeliveryDocEntry,
                SapDocEntry:     inv.SapDocEntry,
                SapDocNum:       inv.SapDocNum,
                SapErrorMessage: inv.SapErrorMessage,
                CreatedAtUtc:    inv.CreatedAtUtc
            ));
        }
        return result;
    }

    private static ZfClassifierInput BuildClassifierInput(
        FulfillmentOrchestrationRecord  orch,
        ZfReplanOperationRecord?        activeReplan,
        List<ZfFragmentDiagnostic>      fragDiags,
        List<ZfDeliveryDiagnostic>      delivDiags,
        List<ZfInvoiceDiagnostic>       invoiceDiags,
        bool                            sapRdr1ReadFailed = false)
    {
        var fragInputs = fragDiags.Select(f => new ZfFragmentClassInput(
            SoLineNum:              f.SoLineNum,
            FragmentWhsCode:        f.FragmentWhsCode,
            SapRdr1WhsCode:         f.SapRdr1WhsCode,
            SapRdr1ItemCode:        f.SapRdr1ItemCode,
            SapRdr1LineStatus:      f.SapRdr1LineStatus,
            PickListRecordWhsCode:  f.PickListWhsCode,
            SapPkl1PickQtty:       f.SapPkl1PickQtty,
            SapPkl1PickStatus:     f.SapPkl1PickStatus,
            SapPkl2BinWhsCode:     f.SapPkl2BinWhsCode,
            HasActiveOdln:         delivDiags.Any(d => d.Status == DeliveryRecordStatus.Created)
        )).ToList();

        var delivInputs = delivDiags.Select(d => new ZfDeliveryClassInput(d.Status)).ToList();
        bool hasInvoice = invoiceDiags.Any(i => i.Status == InvoiceRecordStatus.Created);

        return new ZfClassifierInput(
            OrchState:            orch.State,
            FailureKind:          orch.FailureKind,
            ActiveReplan:         activeReplan is null ? null : ToReplanDiag(activeReplan),
            Fragments:            fragInputs,
            Deliveries:           delivInputs,
            HasSuccessfulInvoice: hasInvoice,
            SapRdr1LookupFailed:  sapRdr1ReadFailed
        );
    }

    private static ZfReplanDiagnostic ToReplanDiag(ZfReplanOperationRecord r)
        => new(r.Id, r.OperationId, r.CurrentStep, r.LastGoodStep,
               r.LastError, r.ChangedBy, r.StartedAtUtc, r.CompletedAtUtc);

    // ── Safe SAP call wrapper ─────────────────────────────────────────────────

    private T ReadSafely<T>(Func<T> fn, string name, object context, T fallback)
    {
        try { return fn(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfAdmin] SAP read {Name} failed for {Context}", name, context);
            return fallback;
        }
    }
}
