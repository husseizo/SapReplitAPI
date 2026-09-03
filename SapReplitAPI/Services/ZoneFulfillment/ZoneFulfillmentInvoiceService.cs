using System.Runtime.Versioning;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// C4: Read-only invoice preflight and controlled invoice creation for Zone Fulfillment.
///
/// MUTATION_ENABLED = true — ONE controlled OINV.Add() authorized for ODLN 30561 (RequestId 7b1f086b).
/// Preflight gates + InvoiceRecord idempotency guard every call before OINV.Add() is reached.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZoneFulfillmentInvoiceService
{
    private const bool MUTATION_ENABLED = true;

    private readonly ZoneFulfillmentRepository _repo;
    private readonly SapService                _sap;
    private readonly ILogger<ZoneFulfillmentInvoiceService> _log;

    public ZoneFulfillmentInvoiceService(
        ZoneFulfillmentRepository               repo,
        SapService                              sap,
        ILogger<ZoneFulfillmentInvoiceService>  log)
    {
        _repo = repo;
        _sap  = sap;
        _log  = log;
    }

    // ── Preflight ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Full read-only invoice preflight for a zone fulfillment request.
    /// Runs all 9 gates; never mutates SAP or MolasIntegration.
    /// </summary>
    public async Task<ZfInvoicePreflightResult> PreflightAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var result = new ZfInvoicePreflightResult
        {
            RequestId       = requestId,
            MutationEnabled = MUTATION_ENABLED
        };

        // Gate 1 — orchestration exists
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
        {
            result.GateErrors.Add($"Orchestration not found for RequestId={requestId}");
            return result;
        }
        result.OrchestrationId = orch.Id;

        // Gate 2 — orchestration has an active delivery (Status=Created)
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

        // Gate 3 — SAP-first: ODLN header verification
        var odln = _sap.GetOdlnForInvoice(result.DeliveryDocEntry);
        result.Odln = odln;

        if (odln is null)
        {
            result.GateErrors.Add($"ODLN DocEntry={result.DeliveryDocEntry} not found in SAP.");
            return result;
        }

        if (odln.Canceled != "N")
        {
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} is CANCELED in SAP (CANCELED={odln.Canceled}). Cannot invoice.");
        }

        if (odln.DocStatus != "O")
        {
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} DocStatus={odln.DocStatus} (expected O=Open/uninvoiced). " +
                $"If DocStatus=C the delivery is already fully invoiced.");
        }

        // Gate 4 — ZF UDFs match
        if (!string.Equals(odln.UZoneRef, "ZoneFulfillment", StringComparison.OrdinalIgnoreCase))
        {
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} U_ZoneRef='{odln.UZoneRef}' (expected 'ZoneFulfillment'). " +
                $"Document is not a ZF delivery.");
        }

        var expectedReplitId = orch.U_ReplitId ?? "";
        if (!string.Equals(odln.UReplitId, expectedReplitId, StringComparison.OrdinalIgnoreCase))
        {
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} U_ReplitId='{odln.UReplitId}' does not match " +
                $"orchestration U_ReplitId='{expectedReplitId}'.");
        }

        // Gate 5 — DLN1 eligible lines (OpenQty > 0)
        var eligibleLines = _sap.GetDln1EligibleLines(result.DeliveryDocEntry);
        result.EligibleLines = eligibleLines;

        if (eligibleLines.Count == 0)
        {
            result.GateErrors.Add(
                $"ODLN {result.DeliveryDocEntry} has no DLN1 lines with OpenQty > 0. " +
                $"Nothing to invoice.");
        }

        // Gate 6 — MolasIntegration InvoiceRecord idempotency check
        var existingRecord = await _repo.FindInvoiceRecordByDeliveryDocEntryAsync(
            result.DeliveryDocEntry, ct);
        result.ExistingInvoiceRecord = existingRecord;

        if (existingRecord is not null && existingRecord.Status == InvoiceRecordStatus.Created)
        {
            result.GateErrors.Add(
                $"InvoiceRecord Id={existingRecord.Id} already exists in Status=Created " +
                $"for DeliveryDocEntry={result.DeliveryDocEntry}. " +
                $"SapDocEntry={existingRecord.SapDocEntry}. Idempotency: already invoiced.");
        }

        // Gate 7 — SAP-first invoice search (OINV/INV1 BaseType=15, BaseEntry=delivery)
        var sapInvoices = _sap.SearchActiveInvoicesByDelivery(result.DeliveryDocEntry);
        result.ExistingSapInvoices = sapInvoices;

        if (sapInvoices.Count > 0)
        {
            var summaries = string.Join(", ", sapInvoices.Select(i =>
                $"OINV DocEntry={i.DocEntry} DocNum={i.DocNum} Status={i.DocStatus}"));
            result.GateErrors.Add(
                $"SAP already has active invoice(s) for ODLN {result.DeliveryDocEntry}: [{summaries}]. " +
                $"SAP-first idempotency gate: do not create duplicate invoice.");
        }

        // Gate 8 — MUTATION_ENABLED guard (always fails in C4)
        if (!MUTATION_ENABLED)
        {
            // Not a GateError — reported separately in the response as mutationEnabled=false.
            _log.LogInformation(
                "[ZF-Invoice] Preflight complete for RequestId={RequestId} ODLN={De} " +
                "EligibleLines={Lines} GatePass={Pass} MUTATION_ENABLED={Me}",
                requestId, result.DeliveryDocEntry, eligibleLines.Count,
                result.GatePass, MUTATION_ENABLED);
        }

        return result;
    }

    // ── Invoice mutation ──────────────────────────────────────────────────────

    public const string MUTATION_DISABLED_VERDICT =
        "MUTATION_DISABLED_PENDING_AUTHORIZATION: OINV.Add() is disabled in this build. " +
        "Run the preflight endpoint to verify gate state. " +
        "Separate written authorization required before enabling.";

    /// <summary>
    /// Executes controlled OINV creation for a zone fulfillment delivery.
    /// 1. Runs full preflight (7 gates).
    /// 2. Short-circuits if gate fails or MUTATION_ENABLED=false.
    /// 3. Idempotency: if InvoiceRecord already Created → returns existing without OINV.Add().
    /// 4. Inserts InvoiceRecord (Pending), calls OINV.Add(), updates record (Created/Failed).
    /// 5. Reads back OINV header + INV1 lines.
    /// </summary>
    public async Task<(ZfInvoicePreflightResult Preflight, string Verdict)> ExecuteInvoiceAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var preflight = await PreflightAsync(requestId, ct);

        if (!MUTATION_ENABLED)
        {
            _log.LogWarning(
                "[ZF-Invoice] ExecuteInvoiceAsync called but MUTATION_ENABLED=false. " +
                "RequestId={RequestId}. Returning MUTATION_DISABLED verdict.",
                requestId);
            return (preflight, MUTATION_DISABLED_VERDICT);
        }

        if (!preflight.GatePass)
        {
            _log.LogWarning(
                "[ZF-Invoice] Preflight gate FAILED for RequestId={RequestId} ODLN={De}. " +
                "Errors=[{Errors}]. OINV.Add() blocked.",
                requestId, preflight.DeliveryDocEntry,
                string.Join("; ", preflight.GateErrors));
            return (preflight, "PREFLIGHT_GATE_FAILED");
        }

        // Idempotency: already Created → return readback without a second OINV.Add()
        if (preflight.ExistingInvoiceRecord?.Status == InvoiceRecordStatus.Created
            && preflight.ExistingInvoiceRecord.SapDocEntry.HasValue)
        {
            _log.LogInformation(
                "[ZF-Invoice] Idempotency: InvoiceRecord already Created for ODLN={De} " +
                "SapDocEntry={Se}. No second OINV.Add().",
                preflight.DeliveryDocEntry, preflight.ExistingInvoiceRecord.SapDocEntry);

            preflight.CreatedInvoiceRecord = preflight.ExistingInvoiceRecord;
            preflight.OinvCreated = _sap.ReadZfOinv(preflight.ExistingInvoiceRecord.SapDocEntry.Value);
            return (preflight, "ALREADY_CREATED_IDEMPOTENT");
        }

        // Resolve the orchestration to get DeliveryLocation
        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        string deliveryLocation = orch?.DeliveryLocation ?? "";

        // Resolve DeliveryRecord for InvoiceRecord insert
        var deliveries  = await _repo.GetDeliveryRecordsAsync(preflight.OrchestrationId, ct);
        var activeDelivery = deliveries
            .Where(d => d.Status == DeliveryRecordStatus.Created && d.SapDocEntry.HasValue)
            .OrderByDescending(d => d.Id)
            .FirstOrDefault()!;

        // Insert InvoiceRecord (Pending) — idempotency anchor in MolasIntegration
        var invoiceRecord = new InvoiceRecordModel
        {
            OrchestrationId  = preflight.OrchestrationId,
            DeliveryRecordId = activeDelivery.Id,
            DeliveryDocEntry = preflight.DeliveryDocEntry,
            Status           = InvoiceRecordStatus.Pending
        };
        long invoiceRecordId = await _repo.InsertInvoiceRecordAsync(invoiceRecord, ct);

        _log.LogInformation(
            "[ZF-Invoice] InvoiceRecord Pending inserted Id={Id} ODLN={De} RequestId={Rid}",
            invoiceRecordId, preflight.DeliveryDocEntry, requestId);

        // OINV.Add() — the one controlled mutation
        try
        {
            var (docEntry, docNum) = _sap.CreateZoneFulfillmentInvoice(preflight, deliveryLocation);

            await _repo.UpdateInvoiceRecordAsync(
                invoiceRecordId,
                InvoiceRecordStatus.Created,
                docEntry, docNum, null, ct);

            _log.LogInformation(
                "[ZF-Invoice] OINV.Add() SUCCESS RequestId={Rid} ODLN={De} " +
                "OINV DocEntry={Ie} DocNum={In}",
                requestId, preflight.DeliveryDocEntry, docEntry, docNum);

            var readback = _sap.ReadZfOinv(docEntry);
            var created  = new InvoiceRecordModel
            {
                Id               = invoiceRecordId,
                OrchestrationId  = preflight.OrchestrationId,
                DeliveryRecordId = activeDelivery.Id,
                DeliveryDocEntry = preflight.DeliveryDocEntry,
                SapDocEntry      = docEntry,
                SapDocNum        = docNum,
                Status           = InvoiceRecordStatus.Created
            };

            preflight.OinvCreated          = readback;
            preflight.CreatedInvoiceRecord = created;

            return (preflight, "OINV_CREATED");
        }
        catch (Exception ex)
        {
            string errMsg = ex.Message;
            await _repo.UpdateInvoiceRecordAsync(
                invoiceRecordId,
                InvoiceRecordStatus.Failed,
                null, null, errMsg, ct);

            _log.LogError(ex,
                "[ZF-Invoice] OINV.Add() FAILED RequestId={Rid} ODLN={De} — InvoiceRecord Id={Id} set to Failed",
                requestId, preflight.DeliveryDocEntry, invoiceRecordId);

            throw;
        }
    }
}
