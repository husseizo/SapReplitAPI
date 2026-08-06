using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.Pending;
using SapReplitAPI.Services;
using System.Runtime.InteropServices;

namespace SapReplitAPI.Controllers;

/// <summary>
/// Draft-first order path: build an order incrementally then submit.
/// POST   /api/orders/local                          — create draft
/// GET    /api/orders/local/{replitId}               — get draft
/// PUT    /api/orders/local/{replitId}/lines         — upsert a line
/// DELETE /api/orders/local/{replitId}/lines/{num}   — remove a line
/// POST   /api/orders/local/{replitId}/submit        — submit (SAP or pending)
/// DELETE /api/orders/local/{replitId}               — cancel draft
///
/// GET    /api/orders/local/pending                  — list non-draft orders
/// POST   /api/orders/local/pending/{id}/retry       — reset a Failed order
/// </summary>
[ApiController]
[Route("api/orders/local")]
public class LocalOrdersController : ControllerBase
{
    private readonly PendingOrderService _pending;
    private readonly SapService _sap;
    private readonly ILogger<LocalOrdersController> _log;

    public LocalOrdersController(
        PendingOrderService pending,
        SapService sap,
        ILogger<LocalOrdersController> log)
    {
        _pending = pending;
        _sap = sap;
        _log = log;
    }

    // ── Draft management ──────────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> CreateDraft([FromBody] CreateOrderDto dto)
    {
        var replitId = PendingOrderService.NewReplitId();
        var draft = await _pending.CreateDraftAsync(dto, replitId);
        return CreatedAtAction(nameof(GetDraft), new { replitId = draft.ReplitId },
            new { ReplitId = draft.ReplitId, Status = "Draft", PendingId = draft.Id });
    }

    [HttpGet("{replitId}")]
    public async Task<IActionResult> GetDraft(string replitId)
    {
        var draft = await _pending.GetDraftAsync(replitId);
        if (draft == null) return NotFound(new { Message = $"Draft {replitId} not found." });
        return Ok(MapDraft(draft));
    }

    [HttpPut("{replitId}/lines")]
    public async Task<IActionResult> UpsertLine(string replitId, [FromBody] OrderLineDto dto)
    {
        try
        {
            await _pending.UpsertLineAsync(replitId, new PendingOrderLine
            {
                LineNum       = dto.LineNum,
                ItemCode      = dto.ItemCode,
                Quantity      = dto.Quantity,
                Price         = dto.Price,
                WhsCode       = dto.WhsCode ?? "001",
                Dscription    = dto.Dscription,
                U_Manufacturer = dto.U_Manufacturer
            });
            return Ok(new { Message = "Line saved." });
        }
        catch (InvalidOperationException ex) { return NotFound(new { Message = ex.Message }); }
    }

    [HttpDelete("{replitId}/lines/{lineNum:int}")]
    public async Task<IActionResult> RemoveLine(string replitId, int lineNum)
    {
        try
        {
            await _pending.RemoveLineAsync(replitId, lineNum);
            return NoContent();
        }
        catch (InvalidOperationException ex) { return NotFound(new { Message = ex.Message }); }
    }

    [HttpPost("{replitId}/submit")]
    public async Task<IActionResult> Submit(string replitId)
    {
        try
        {
            var order = await _pending.PromoteToPendingAsync(replitId);

            // Try SAP immediately
            var dto = new CreateOrderDto
            {
                CardCode     = order.CardCode,
                DocDate      = order.DocDate,
                DeliveryDate = order.DeliveryDate,
                SlpCode      = order.SlpCode,
                DocCur       = order.DocCurrency,
                Lines        = order.Lines.Select(l => new OrderLineDto
                {
                    ItemCode      = l.ItemCode,
                    Quantity      = l.Quantity,
                    Price         = l.Price,
                    WhsCode       = l.WhsCode,
                    Dscription    = l.Dscription ?? "",
                    U_Manufacturer = l.U_Manufacturer ?? ""
                }).ToList()
            };

            try
            {
                var docEntry = _sap.CreateOrder(dto, order.ReplitId);
                await _pending.MarkSyncedAsync(order.Id, docEntry);
                _log.LogInformation("✅ [LocalOrder] {ReplitId} submitted to SAP. DocEntry={DocEntry}", replitId, docEntry);
                return Ok(new { ReplitId = replitId, Status = "Synced", DocEntry = docEntry });
            }
            catch (Exception ex) when (IsSapUnavailable(ex))
            {
                // SAP offline — stay as Pending, background job will retry
                _log.LogWarning("📥 [LocalOrder] {ReplitId} queued for later sync. SAP issue: {Error}", replitId, ex.Message);
                return Accepted(new
                {
                    Message = "SAP is currently unavailable. Order queued and will sync automatically.",
                    ReplitId = replitId,
                    Status = "Pending",
                    PendingId = order.Id,
                    DocEntry = (int?)null
                });
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpDelete("{replitId}")]
    public async Task<IActionResult> CancelDraft(string replitId)
    {
        try
        {
            await _pending.CancelDraftAsync(replitId);
            return NoContent();
        }
        catch (InvalidOperationException ex) { return NotFound(new { Message = ex.Message }); }
    }

    // ── Pending order management ──────────────────────────────────────────────

    [HttpGet("pending")]
    public async Task<IActionResult> ListPending([FromQuery] string? status = null)
    {
        var orders = await _pending.GetAllAsync(status);
        return Ok(orders.Select(o => new
        {
            o.Id,
            o.ReplitId,
            o.CardCode,
            o.DocDate,
            o.Status,
            o.SapDocEntry,
            o.RetryCount,
            o.NextRetryAt,
            o.ErrorMessage,
            o.CreatedAt,
            o.SyncedAt,
            LineCount = o.Lines.Count
        }));
    }

    [HttpPost("pending/{id:int}/retry")]
    public async Task<IActionResult> RetryFailed(int id)
    {
        try
        {
            await _pending.ResetForRetryAsync(id);
            return Ok(new { Message = $"Order {id} reset for retry." });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsSapUnavailable(Exception ex) =>
        ex.Message.Contains("SAP Connection failed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) ||
        ex is COMException ||
        ex.Message.StartsWith("Failed to create order:", StringComparison.OrdinalIgnoreCase);

    private static object MapDraft(SapReplitAPI.Models.Pending.PendingOrder o) => new
    {
        o.ReplitId,
        o.CardCode,
        o.DocDate,
        o.DeliveryDate,
        o.SlpCode,
        o.Status,
        Lines = o.Lines.Select(l => new
        {
            l.LineNum,
            l.ItemCode,
            l.Quantity,
            l.Price,
            l.WhsCode,
            l.Dscription,
            l.U_Manufacturer
        })
    };
}
