using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;

namespace SapReplitAPI.Controllers;

/// <summary>
/// V2 Offline Fulfillment endpoints.
///
/// All endpoints return 503 when the feature flag is disabled.
/// No V1 (PendingOrder) logic is touched here.
/// </summary>
[ApiController]
[Route("api/offline-fulfillments")]
public sealed class OfflineFulfillmentController : ControllerBase
{
    private readonly OfflineFulfillmentService _svc;
    private readonly OfflineFulfillmentOptions _opts;

    public OfflineFulfillmentController(
        OfflineFulfillmentService svc,
        IOptions<OfflineFulfillmentOptions> opts)
    {
        _svc  = svc;
        _opts = opts.Value;
    }

    private IActionResult FeatureDisabled() =>
        StatusCode(503, new { error = "Offline Fulfillment V2 is not enabled on this environment." });

    // ── POST /api/offline-fulfillments ─────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> Capture(
        [FromBody] CaptureOfflineFulfillmentRequest req,
        CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();

        var order = await _svc.CaptureAsync(req, ct);
        return order is null
            ? StatusCode(500, new { error = "Capture failed." })
            : CreatedAtAction(nameof(GetById), new { id = order.Id }, MapOrderDto(order));
    }

    // ── GET /api/offline-fulfillments ─────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? state,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var orders = await _svc.ListAsync(state, limit, ct);
        return Ok(orders.Select(MapOrderDto));
    }

    // ── GET /api/offline-fulfillments/{id} ────────────────────────────────────

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var order = await _svc.GetByIdAsync(id, ct);
        return order is null ? NotFound() : Ok(MapOrderDto(order));
    }

    // ── GET /api/offline-fulfillments/{id}/status ─────────────────────────────

    [HttpGet("{id:int}/status")]
    public async Task<IActionResult> GetStatus(int id, CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var order = await _svc.GetByIdAsync(id, ct);
        if (order is null) return NotFound();
        return Ok(new
        {
            order.Id,
            order.OfflineId,
            order.State,
            order.RecoveryStage,
            order.ReconciliationReason,
            order.ErrorMessage,
            order.UpdatedAtUtc,
            SapSalesOrderDocEntry = order.SapSalesOrderDocEntry,
            SapDeliveryDocEntry   = order.SapDeliveryDocEntry,
            SapInvoiceDocEntry    = order.SapInvoiceDocEntry
        });
    }

    // ── POST /api/offline-fulfillments/{id}/reserve ───────────────────────────

    [HttpPost("{id:int}/reserve")]
    public async Task<IActionResult> Reserve(
        int id,
        [FromBody] List<OfflineReservationRequest> requests,
        CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var result = await _svc.ReserveStockAsync(id, requests, ct);
        if (!result.Success)
            return Conflict(new { error = result.Error });
        return Ok(new { message = "Offline reservations created." });
    }

    // ── POST /api/offline-fulfillments/{id}/pick ──────────────────────────────

    [HttpPost("{id:int}/pick")]
    public async Task<IActionResult> RecordPick(
        int id,
        [FromBody] List<OfflinePickRequest> picks,
        CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var result = await _svc.RecordPickAsync(id, picks, ct);
        if (!result.Success)
            return Conflict(new { error = result.Error });
        return Ok(new { message = "Pick recorded." });
    }

    // ── POST /api/offline-fulfillments/{id}/confirm-pick ─────────────────────

    [HttpPost("{id:int}/confirm-pick")]
    public async Task<IActionResult> ConfirmPick(int id, CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var result = await _svc.ConfirmPickAsync(id, ct);
        if (!result.Success)
            return Conflict(new { error = result.Error });
        return Ok(new { confirmId = result.ConfirmId, message = "Offline pick confirmed. Order queued for recovery." });
    }

    // ── GET /api/offline-fulfillments/mirrored-stock/{itemCode} ───────────────

    [HttpGet("mirrored-stock/{itemCode}")]
    public async Task<IActionResult> GetMirroredStock(
        string itemCode,
        [FromQuery] string whsCode,
        [FromQuery] int? binAbsEntry,
        [FromQuery] int? excludeOrderId,
        CancellationToken ct)
    {
        if (!_opts.Enabled) return FeatureDisabled();
        var result = await _svc.GetMirroredStockAsync(itemCode, whsCode, binAbsEntry, excludeOrderId, ct);
        return Ok(result);
    }

    // ── Mapping ────────────────────────────────────────────────────────────────

    private static object MapOrderDto(Models.Offline.OfflineFulfillmentOrder o) => new
    {
        o.Id,
        o.OfflineId,
        o.WorkflowVersion,
        o.CardCode,
        o.DocDate,
        o.DeliveryDate,
        o.SlpCode,
        o.DocCurrency,
        o.DeliveryLocation,
        o.State,
        o.RecoveryStage,
        o.SapSalesOrderDocEntry,
        o.SapSalesOrderDocNum,
        o.SapDeliveryDocEntry,
        o.SapDeliveryDocNum,
        o.SapInvoiceDocEntry,
        o.SapInvoiceDocNum,
        o.ReconciliationReason,
        o.ErrorMessage,
        o.CreatedAtUtc,
        o.UpdatedAtUtc,
        Lines = o.Lines.Select(l => new
        {
            l.Id, l.LineSeq, l.RequestedLineId, l.ItemCode,
            l.RequestedQty, l.UnitPrice, l.Description, l.U_ItemName, l.U_Manufacturer
        }),
        Picks = o.Picks.Select(p => new
        {
            p.Id, p.RequestedLineId, p.ItemCode, p.RequestedQty,
            p.PickedQty, p.WhsCode, p.BinAbsEntry, p.BinCode,
            p.PickerReference, p.PickedAtUtc, p.ConfirmedAtUtc,
            p.OfflineConfirmId, p.IsConfirmed
        }),
        Reservations = o.Reservations.Select(r => new
        {
            r.Id, r.ItemCode, r.WhsCode, r.BinAbsEntry, r.ReservedQty, r.State
        })
    };
}
