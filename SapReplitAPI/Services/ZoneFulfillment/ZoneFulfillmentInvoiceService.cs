using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Zone Fulfillment invoice service — production automation gate.
///
/// InvoiceAutomationEnabled is read from ZoneFulfillment:InvoiceAutomationEnabled in config
/// (false by default; true in appsettings.Production.json).
/// Full InvoiceRecord state machine: No row → Pending → Created / Failed → retry.
/// SAP-first recovery: Pending + SAP OINV exists → reconcile without second OINV.Add().
/// Per-delivery concurrency lock prevents duplicate creation under concurrent requests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZoneFulfillmentInvoiceService
{
    private readonly bool                                   _invoiceAutomationEnabled;
    private readonly ZoneFulfillmentInvoiceStartupHealth    _startupHealth;
    private readonly ZoneFulfillmentRepository              _repo;
    private readonly SapService                             _sap;
    private readonly ILogger<ZoneFulfillmentInvoiceService> _log;

    /// <summary>Exposed for diagnostics/status endpoints.</summary>
    public bool InvoiceAutomationEnabled => _invoiceAutomationEnabled;

    // Per-delivery semaphore — serializes concurrent invoice attempts for the same ODLN.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _invoiceLocks = new();
    private static SemaphoreSlim GetLock(int deliveryDocEntry)
        => _invoiceLocks.GetOrAdd(deliveryDocEntry, _ => new SemaphoreSlim(1, 1));

    public ZoneFulfillmentInvoiceService(
        ZoneFulfillmentRepository                repo,
        SapService                               sap,
        ILogger<ZoneFulfillmentInvoiceService>   log,
        IConfiguration                           configuration,
        ZoneFulfillmentInvoiceStartupHealth      startupHealth)
    {
        _repo                     = repo;
        _sap                      = sap;
        _log                      = log;
        _startupHealth            = startupHealth;
        _invoiceAutomationEnabled = configuration.GetValue<bool>(
            "ZoneFulfillment:InvoiceAutomationEnabled", defaultValue: false);
    }

    // ── Preflight (read-only) ─────────────────────────────────────────────────

    /// <summary>
    /// Full read-only invoice preflight — 7 gates; never mutates SAP or MolasIntegration.
    /// </summary>
    public async Task<ZfInvoicePreflightResult> PreflightAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var result = new ZfInvoicePreflightResult
        {
            RequestId       = requestId,
            MutationEnabled = _invoiceAutomationEnabled
        };

        // Gate 1 — orchestration exists
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
        {
            result.GateErrors.Add($"Orchestration not found for RequestId={requestId}");
            return result;
        }
        result.OrchestrationId = orch.Id;

        // Gate 2 — active delivery record
        var deliveries = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        var activeDelivery = deliveries
            .Where(d => d.Status == DeliveryRecordStatus.Created && d.SapDocEntry.HasValue)
            .OrderByDescending(d => d.Id)
            .FirstOrDefault();

        if (activeDelivery is null)
        {
            result.GateErrors.Add(
                $"No active DeliveryRecord (Status=Created) found for OrchestrationId={orch.Id}. " +
                $"Delivery records: [{string.Join(", ", deliveries.Select(d => $"Id={d.Id}/Status={d.Status}"))}]");
            return result;
        }

        result.DeliveryDocEntry = activeDelivery.SapDocEntry!.Value;
        result.DeliveryDocNum   = activeDelivery.SapDocNum ?? activeDelivery.SapDocEntry!.Value;

        // Gate 3 — ODLN header
        var odln = _sap.GetOdlnForInvoice(result.DeliveryDocEntry);
        result.Odln = odln;

        if (odln is null)
        {
            result.GateErrors.Add($"ODLN DocEntry={result.DeliveryDocEntry} not found in SAP.");
            return result;
        }

        if (odln.Canceled != "N")
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} is CANCELED in SAP (CANCELED={odln.Canceled}).");

        if (odln.DocStatus != "O")
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} DocStatus={odln.DocStatus} (expected O=Open/uninvoiced). " +
                $"If DocStatus=C the delivery is already fully invoiced.");

        // Gate 4 — ZF UDFs match
        if (!string.Equals(odln.UZoneRef, "ZoneFulfillment", StringComparison.OrdinalIgnoreCase))
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} U_ZoneRef='{odln.UZoneRef}' (expected 'ZoneFulfillment').");

        var expectedReplitId = orch.U_ReplitId ?? "";
        if (!string.Equals(odln.UReplitId, expectedReplitId, StringComparison.OrdinalIgnoreCase))
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} U_ReplitId='{odln.UReplitId}' does not match " +
                $"orchestration U_ReplitId='{expectedReplitId}'.");

        // Gate 5 — eligible DLN1 lines
        var eligibleLines = _sap.GetDln1EligibleLines(result.DeliveryDocEntry);
        result.EligibleLines = eligibleLines;

        if (eligibleLines.Count == 0)
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} has no DLN1 lines with OpenQty > 0. Nothing to invoice.");

        // Gate 6 — MolasIntegration idempotency
        var existingRecord = await _repo.FindInvoiceRecordByDeliveryDocEntryAsync(result.DeliveryDocEntry, ct);
        result.ExistingInvoiceRecord = existingRecord;

        if (existingRecord?.Status == InvoiceRecordStatus.Created)
            result.GateErrors.Add(
                $"InvoiceRecord Id={existingRecord.Id} already Status=Created " +
                $"for DeliveryDocEntry={result.DeliveryDocEntry}. SapDocEntry={existingRecord.SapDocEntry}.");

        // Gate 7 — SAP-first invoice search
        var sapInvoices = _sap.SearchActiveInvoicesByDelivery(result.DeliveryDocEntry);
        result.ExistingSapInvoices = sapInvoices;

        if (sapInvoices.Count > 0)
        {
            var summaries = string.Join(", ", sapInvoices.Select(
                i => $"OINV DocEntry={i.DocEntry} DocNum={i.DocNum} Status={i.DocStatus}"));
            result.GateErrors.Add(
                $"SAP already has active invoice(s) for ODLN {result.DeliveryDocEntry}: [{summaries}]. " +
                $"SAP-first idempotency gate: do not create duplicate invoice.");
        }

        _log.LogInformation(
            "[ZF-Invoice] Preflight complete for RequestId={RequestId} ODLN={De} " +
            "EligibleLines={Lines} GatePass={Pass} InvoiceAutomationEnabled={Me}",
            requestId, result.DeliveryDocEntry, eligibleLines.Count,
            result.GatePass, _invoiceAutomationEnabled);

        return result;
    }

    // ── Invoice execution — full state machine ────────────────────────────────

    /// <summary>
    /// Executes ZF OINV creation with full Pending-recovery contract.
    /// Implements the §10 execution order with per-delivery concurrency lock.
    /// Never relies on SQL UNIQUE violation as flow control.
    /// </summary>
    public async Task<ZfInvoiceExecuteResult> ExecuteInvoiceAsync(
        Guid requestId, CancellationToken ct = default)
    {
        // Step 1 — load orchestration
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
        {
            return new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_PreflightBlocked,
                GateErrors = [$"Orchestration not found for RequestId={requestId}"]
            };
        }

        // Step 2 — load active delivery record
        var deliveries = await _repo.GetDeliveryRecordsAsync(orch.Id, ct);
        var activeDelivery = deliveries
            .Where(d => d.Status == DeliveryRecordStatus.Created && d.SapDocEntry.HasValue)
            .OrderByDescending(d => d.Id)
            .FirstOrDefault();

        if (activeDelivery is null)
        {
            return new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_PreflightBlocked,
                GateErrors = [$"No active DeliveryRecord (Status=Created) for OrchestrationId={orch.Id}."]
            };
        }

        int    deliveryDocEntry = activeDelivery.SapDocEntry!.Value;
        string deliveryLocation = orch.DeliveryLocation ?? "";

        // Step 3 — acquire per-delivery lock (serializes concurrent invoice attempts)
        var lck = GetLock(deliveryDocEntry);
        await lck.WaitAsync(ct);
        try
        {
            return await ExecuteLockedAsync(
                requestId, orch, activeDelivery, deliveryDocEntry, deliveryLocation, ct);
        }
        finally
        {
            lck.Release();
        }
    }

    private async Task<ZfInvoiceExecuteResult> ExecuteLockedAsync(
        Guid                           requestId,
        FulfillmentOrchestrationRecord orch,
        DeliveryRecordModel            activeDelivery,
        int                            deliveryDocEntry,
        string                         deliveryLocation,
        CancellationToken              ct)
    {
        // Step 4 — load / create / reuse InvoiceRecord
        _log.LogInformation(
            "[ZF-INV] InvoiceAutomationStarted RequestId={Rid} OrchId={Oid} DeliveryDocEntry={De}",
            requestId, orch.Id, deliveryDocEntry);

        var invoiceRecord = await _repo.GetOrCreateInvoiceRecordAsync(
            orch.Id, activeDelivery.Id, deliveryDocEntry, ct);

        bool isPendingNew    = invoiceRecord.SapErrorMessage is null && invoiceRecord.SapDocEntry is null;
        bool isRetryFromFail = invoiceRecord.Status == InvoiceRecordStatus.Pending
                               && !isPendingNew;

        if (invoiceRecord.Status == InvoiceRecordStatus.Pending && !isPendingNew)
        {
            _log.LogInformation(
                "[ZF-INV] InvoicePendingReused RequestId={Rid} OrchId={Oid} " +
                "DeliveryRecordId={Drid} DeliveryDocEntry={De} InvoiceRecordId={Iid}",
                requestId, orch.Id, activeDelivery.Id, deliveryDocEntry, invoiceRecord.Id);
        }
        else if (invoiceRecord.Status == InvoiceRecordStatus.Pending && isPendingNew)
        {
            _log.LogInformation(
                "[ZF-INV] InvoicePendingCreated RequestId={Rid} OrchId={Oid} " +
                "DeliveryRecordId={Drid} DeliveryDocEntry={De} InvoiceRecordId={Iid}",
                requestId, orch.Id, activeDelivery.Id, deliveryDocEntry, invoiceRecord.Id);
        }

        if (isRetryFromFail)
        {
            _log.LogInformation(
                "[ZF-INV] InvoiceRetryFromFailed RequestId={Rid} InvoiceRecordId={Iid} " +
                "DeliveryDocEntry={De}",
                requestId, invoiceRecord.Id, deliveryDocEntry);
        }

        // Step 5 — SAP-first search for existing OINV
        var sapMatches = _sap.SearchActiveInvoicesByDelivery(deliveryDocEntry);

        // Step 6 — if SAP OINV found, attempt structural validation + reconcile
        if (sapMatches.Count > 0)
        {
            _log.LogInformation(
                "[ZF-INV] ExistingSapInvoiceFound RequestId={Rid} DeliveryDocEntry={De} " +
                "Candidates=[{Candidates}]",
                requestId, deliveryDocEntry,
                string.Join(", ", sapMatches.Select(m => $"DocEntry={m.DocEntry}")));

            foreach (var match in sapMatches)
            {
                var oinvDetail = _sap.ReadZfOinv(match.DocEntry);
                if (oinvDetail is null) continue;

                var dln1Lines  = _sap.GetDln1EligibleLines(deliveryDocEntry);
                var validErr   = ValidateStructure(oinvDetail, deliveryDocEntry,
                                                   orch, dln1Lines);

                if (validErr is not null)
                {
                    _log.LogWarning(
                        "[ZF-INV] ExistingSapInvoiceFound but structural validation failed " +
                        "RequestId={Rid} OinvDocEntry={Ie} Error={Err}",
                        requestId, match.DocEntry, validErr);
                    continue;
                }

                // Valid match — branch on whether the row was already Created or needs recovery
                if (invoiceRecord.Status == InvoiceRecordStatus.Created)
                {
                    // Row already Created — idempotent return, no UPDATE needed
                    _log.LogInformation(
                        "[ZF-INV] InvoiceAlreadyCreated RequestId={Rid} InvoiceRecordId={Iid} " +
                        "SapDocEntry={Se} DeliveryDocEntry={De}",
                        requestId, invoiceRecord.Id, oinvDetail.DocEntry, deliveryDocEntry);

                    return new ZfInvoiceExecuteResult
                    {
                        Verdict         = ZfInvoiceExecuteResult.V_AlreadyCreated,
                        AlreadyApplied  = true,
                        RecoveredFromSap = false,
                        InvoiceRecordId = invoiceRecord.Id,
                        InvoiceDocEntry = oinvDetail.DocEntry,
                        InvoiceDocNum   = oinvDetail.DocNum,
                        OinvReadback    = oinvDetail
                    };
                }

                // Row was Pending/Failed — reconcile to Created
                await _repo.UpdateInvoiceRecordAsync(
                    invoiceRecord.Id,
                    InvoiceRecordStatus.Created,
                    oinvDetail.DocEntry, oinvDetail.DocNum, null, ct);

                _log.LogInformation(
                    "[ZF-INV] InvoiceRecoveredFromSap RequestId={Rid} OrchId={Oid} " +
                    "InvoiceRecordId={Iid} SapDocEntry={Se} DeliveryDocEntry={De}",
                    requestId, orch.Id, invoiceRecord.Id, oinvDetail.DocEntry, deliveryDocEntry);

                return new ZfInvoiceExecuteResult
                {
                    Verdict          = ZfInvoiceExecuteResult.V_RecoveredFromSap,
                    AlreadyApplied   = true,
                    RecoveredFromSap = true,
                    InvoiceRecordId  = invoiceRecord.Id,
                    InvoiceDocEntry  = oinvDetail.DocEntry,
                    InvoiceDocNum    = oinvDetail.DocNum,
                    OinvReadback     = oinvDetail
                };
            }

            // SAP OINV(s) found but none passed structural validation — treat as unrecoverable
            var badSummaries = string.Join(", ", sapMatches.Select(m => $"DocEntry={m.DocEntry}"));
            _log.LogError(
                "[ZF-INV] InvoiceFailed RequestId={Rid} DeliveryDocEntry={De} " +
                "— SAP invoice(s) [{Bad}] exist but structural validation failed. " +
                "Manual investigation required.",
                requestId, deliveryDocEntry, badSummaries);

            return new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_PreflightBlocked,
                GateErrors = [$"SAP OINV(s) found ({badSummaries}) but structural validation failed. " +
                               "Manual review required before retry."]
            };
        }

        // Step 7 — InvoiceRecord already Created (no SAP OINV above found it, but record is Created)
        if (invoiceRecord.Status == InvoiceRecordStatus.Created)
        {
            var existingOinv = invoiceRecord.SapDocEntry.HasValue
                ? _sap.ReadZfOinv(invoiceRecord.SapDocEntry.Value)
                : null;

            _log.LogInformation(
                "[ZF-INV] InvoiceAlreadyCreated RequestId={Rid} InvoiceRecordId={Iid} " +
                "SapDocEntry={Se} DeliveryDocEntry={De}",
                requestId, invoiceRecord.Id, invoiceRecord.SapDocEntry, deliveryDocEntry);

            return new ZfInvoiceExecuteResult
            {
                Verdict         = ZfInvoiceExecuteResult.V_AlreadyCreated,
                AlreadyApplied  = true,
                InvoiceRecordId = invoiceRecord.Id,
                InvoiceDocEntry = invoiceRecord.SapDocEntry,
                InvoiceDocNum   = invoiceRecord.SapDocNum,
                OinvReadback    = existingOinv
            };
        }

        // Step 8 — InvoiceAutomationEnabled guard (config + startup health)
        if (!_invoiceAutomationEnabled || !_startupHealth.InvoiceRecordAvailable)
        {
            string reason = !_invoiceAutomationEnabled
                ? "InvoiceAutomationEnabled=false in configuration."
                : $"InvoiceRecord schema unavailable at startup: {_startupHealth.ProbeMessage}";
            _log.LogWarning(
                "[ZF-Invoice] MUTATION_DISABLED RequestId={RequestId}. {Reason}", requestId, reason);
            return new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_MutationDisabled,
                GateErrors = [reason]
            };
        }

        // Step 9 — run preflight (ODLN state, eligible lines, UDF match)
        // At this point: no SAP OINV, InvoiceRecord is Pending.
        // Gates 3/4/5 check ODLN state; Gates 6/7 may still fire (harmless — caller handles).
        var preflight = await PreflightAsync(orch.RequestId, ct);

        // Gate errors that matter here: ODLN state/cancel/lines/UDF mismatch.
        // Gate 6 (existing Created record) and Gate 7 (SAP invoice) should not fire at this point
        // since we've already handled both above. If they somehow do, they will block correctly.
        if (!preflight.GatePass)
        {
            _log.LogWarning(
                "[ZF-INV] InvoicePreflightBlocked RequestId={Rid} ODLN={De} " +
                "Errors=[{Errors}]. OINV.Add() blocked.",
                requestId, deliveryDocEntry, string.Join("; ", preflight.GateErrors));

            return new ZfInvoiceExecuteResult
            {
                Verdict    = ZfInvoiceExecuteResult.V_PreflightBlocked,
                GateErrors = preflight.GateErrors,
                Preflight  = preflight
            };
        }

        _log.LogInformation(
            "[ZF-INV] InvoicePreflightPassed RequestId={Rid} ODLN={De} EligibleLines={N}",
            requestId, deliveryDocEntry, preflight.EligibleLines.Count);

        // Step 10 — OINV.Add() using the existing Pending InvoiceRecord row
        try
        {
            var (docEntry, docNum) = _sap.CreateZoneFulfillmentInvoice(preflight, deliveryLocation);

            await _repo.UpdateInvoiceRecordAsync(
                invoiceRecord.Id,
                InvoiceRecordStatus.Created,
                docEntry, docNum, null, ct);

            _log.LogInformation(
                "[ZF-INV] InvoiceCreated RequestId={Rid} OrchId={Oid} " +
                "InvoiceRecordId={Iid} SapDocEntry={Se} SapDocNum={Sn} " +
                "DeliveryDocEntry={De}",
                requestId, orch.Id, invoiceRecord.Id, docEntry, docNum, deliveryDocEntry);

            var readback = _sap.ReadZfOinv(docEntry);
            return new ZfInvoiceExecuteResult
            {
                Verdict         = ZfInvoiceExecuteResult.V_OinvCreated,
                AlreadyApplied  = false,
                InvoiceRecordId = invoiceRecord.Id,
                InvoiceDocEntry = docEntry,
                InvoiceDocNum   = docNum,
                OinvReadback    = readback,
                Preflight       = preflight
            };
        }
        catch (Exception ex)
        {
            // §11: failure after OINV.Add() — try SAP-first recovery before marking Failed
            _log.LogError(ex,
                "[ZF-Invoice] OINV.Add() threw for RequestId={Rid} DeliveryDocEntry={De}. " +
                "Checking SAP-first before marking Failed.",
                requestId, deliveryDocEntry);

            var recovery = _sap.SearchActiveInvoicesByDelivery(deliveryDocEntry);
            if (recovery.Count > 0)
            {
                var recoverOinv = _sap.ReadZfOinv(recovery[0].DocEntry);
                if (recoverOinv is not null)
                {
                    var dln1 = _sap.GetDln1EligibleLines(deliveryDocEntry);
                    var err  = ValidateStructure(recoverOinv, deliveryDocEntry, orch, dln1);
                    if (err is null)
                    {
                        await _repo.UpdateInvoiceRecordAsync(
                            invoiceRecord.Id,
                            InvoiceRecordStatus.Created,
                            recoverOinv.DocEntry, recoverOinv.DocNum, null, ct);

                        _log.LogWarning(
                            "[ZF-INV] InvoiceRecoveredFromSap (post-exception) RequestId={Rid} " +
                            "InvoiceRecordId={Iid} SapDocEntry={Se}",
                            requestId, invoiceRecord.Id, recoverOinv.DocEntry);

                        return new ZfInvoiceExecuteResult
                        {
                            Verdict          = ZfInvoiceExecuteResult.V_RecoveredFromSap,
                            AlreadyApplied   = true,
                            RecoveredFromSap = true,
                            InvoiceRecordId  = invoiceRecord.Id,
                            InvoiceDocEntry  = recoverOinv.DocEntry,
                            InvoiceDocNum    = recoverOinv.DocNum,
                            OinvReadback     = recoverOinv
                        };
                    }
                }
            }

            // Could not recover from SAP — mark Failed
            string errMsg = ex.Message;
            await _repo.UpdateInvoiceRecordAsync(
                invoiceRecord.Id, InvoiceRecordStatus.Failed,
                null, null, errMsg, ct);

            _log.LogError(ex,
                "[ZF-INV] InvoiceFailed RequestId={Rid} OrchId={Oid} " +
                "InvoiceRecordId={Iid} DeliveryDocEntry={De}",
                requestId, orch.Id, invoiceRecord.Id, deliveryDocEntry);

            throw;
        }
    }

    // ── Structural validation helper ──────────────────────────────────────────

    /// <summary>
    /// Validates that an existing SAP OINV structurally matches the expected ZF delivery.
    /// Returns null if the invoice is valid; returns an error message if it fails any check.
    /// </summary>
    private static string? ValidateStructure(
        OinvCreatedReadback              oinv,
        int                              deliveryDocEntry,
        FulfillmentOrchestrationRecord   orch,
        IReadOnlyList<Dln1InvoiceLine>   dln1Lines)
    {
        // CardCode must match — read from ODLN via preflight or use orch state
        // (We validate UDFs which embed the orchestration identity)

        if (!string.Equals(oinv.UZoneRef, "ZoneFulfillment", StringComparison.OrdinalIgnoreCase))
            return $"U_ZoneRef='{oinv.UZoneRef}' expected 'ZoneFulfillment'.";

        var orchReplitId = orch.U_ReplitId ?? "";
        if (!string.Equals(oinv.UReplitId, orchReplitId, StringComparison.OrdinalIgnoreCase))
            return $"U_ReplitId='{oinv.UReplitId}' does not match orchestration '{orchReplitId}'.";

        var orchLocation = orch.DeliveryLocation ?? "";
        if (!string.Equals(oinv.UDeliveryLocation, orchLocation, StringComparison.OrdinalIgnoreCase))
            return $"U_DeliveryLocation='{oinv.UDeliveryLocation}' does not match orchestration '{orchLocation}'.";

        // All INV1 lines must point to this delivery (BaseType=15, BaseEntry=deliveryDocEntry)
        var badEntry = oinv.Lines.FirstOrDefault(
            l => l.BaseType != 15 || l.BaseEntry != deliveryDocEntry);
        if (badEntry is not null)
            return $"INV1 line {badEntry.LineNum} has BaseType={badEntry.BaseType} " +
                   $"BaseEntry={badEntry.BaseEntry} (expected BaseType=15 BaseEntry={deliveryDocEntry}).";

        // All expected DLN1 BaseLines must be present
        var invoicedBaseLines = oinv.Lines.Select(l => l.BaseLine).ToHashSet();
        var expectedBaseLines = dln1Lines.Count > 0
            ? dln1Lines.Select(l => l.LineNum).ToHashSet()
            : oinv.Lines.Select(l => l.BaseLine).ToHashSet(); // if dln1Lines empty (already invoiced), accept

        if (dln1Lines.Count > 0)
        {
            var missing = expectedBaseLines.Except(invoicedBaseLines).ToList();
            if (missing.Count > 0)
                return $"INV1 missing expected DLN1 BaseLines: [{string.Join(",", missing)}].";
        }

        if (oinv.Lines.Count == 0)
            return "OINV has no INV1 lines.";

        return null; // valid
    }
}
