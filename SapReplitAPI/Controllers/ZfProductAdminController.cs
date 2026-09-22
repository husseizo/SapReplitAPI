using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Filters;
using SapReplitAPI.Models.ProductAdmin;
using SapReplitAPI.Services.ProductAdmin;
using System.Runtime.Versioning;

namespace SapReplitAPI.Controllers;

/// <summary>
/// Product price administration.
/// All routes require X-ZF-Admin-Key header (ZfAdminKeyAuthFilter).
///
///   PUT  /api/product-admin/products/{itemCode}/prices/{priceListNum}
///   POST /api/product-admin/products/prices/bulk
///   POST /api/product-admin/products/prices/bulk/preview
///   POST /api/product-admin/products/{itemCode}/prices/{priceListNum}/cache-repair
///   GET  /api/product-admin/products/prices/bulk/{requestId}
/// </summary>
[ApiController]
[Route("api/product-admin")]
[ServiceFilter(typeof(ZfAdminKeyAuthFilter))]
[SupportedOSPlatform("windows")]
public sealed class ZfProductAdminController : ControllerBase
{
    private readonly ZfProductAdminService             _service;
    private readonly ILogger<ZfProductAdminController> _log;

    public ZfProductAdminController(
        ZfProductAdminService             service,
        ILogger<ZfProductAdminController> log)
    {
        _service = service;
        _log     = log;
    }

    /// <summary>
    /// Update a single price list entry for one item.
    /// Returns 200 on success or CACHE_SYNC_WARNING; 409 on CONCURRENCY_CONFLICT;
    /// 400 on validation failures; 404 on item/price-list not found; 502 on SAP errors.
    /// </summary>
    [HttpPut("products/{itemCode}/prices/{priceListNum:int}")]
    public async Task<IActionResult> UpdatePrice(
        string itemCode, int priceListNum,
        [FromBody] UpdateProductPriceRequest req,
        CancellationToken ct)
    {
        _log.LogInformation(
            "[ProductAdmin] PUT price {Item} PL{Pl} by {By}",
            itemCode, priceListNum, req.RequestedBy);

        var result = await _service.UpdatePriceAsync(itemCode, priceListNum, req, ct);

        return result.ResultCode switch
        {
            PriceUpdateResult.Success           => Ok(result),
            PriceUpdateResult.CacheSyncWarning  => Ok(result),   // SAP succeeded; cache is stale
            PriceUpdateResult.ValidationFailed  => BadRequest(result),
            PriceUpdateResult.ItemNotFound      => NotFound(result),
            PriceUpdateResult.PriceListNotFound => NotFound(result),
            PriceUpdateResult.ConcurrencyConflict => Conflict(result),
            _                                   => StatusCode(502, result),
        };
    }

    /// <summary>
    /// Bulk price update. Each item is isolated — one failure does not abort others.
    /// Always returns 207 Multi-Status; inspect per-item ResultCode in the response body.
    /// RequestId is required and used for idempotency: already-completed successes are
    /// skipped and returned with ALREADY_COMPLETED.
    /// </summary>
    [HttpPost("products/prices/bulk")]
    public async Task<IActionResult> UpdatePricesBulk(
        [FromBody] BulkUpdateProductPricesRequest req,
        CancellationToken ct)
    {
        if (req.RequestId == Guid.Empty)
            return BadRequest(new { message = "RequestId must be a non-empty GUID." });

        _log.LogInformation(
            "[ProductAdmin] POST bulk prices RequestId={Rid} items={Count} by {By}",
            req.RequestId, req.Updates?.Count ?? 0, req.RequestedBy);

        var result = await _service.UpdatePricesBulkAsync(req, ct);
        return StatusCode(207, result);
    }

    /// <summary>
    /// Preview bulk price changes — read-only, no SAP mutations.
    /// Returns per-item validation status, current/proposed prices, and batch totals.
    /// </summary>
    [HttpPost("products/prices/bulk/preview")]
    public async Task<IActionResult> PreviewBulkPrices(
        [FromBody] BulkPreviewRequest req,
        CancellationToken ct)
    {
        _log.LogInformation(
            "[ProductAdmin] POST bulk preview RequestId={Rid} items={Count} by {By}",
            req.RequestId, req.Updates?.Count ?? 0, req.RequestedBy);

        var result = await _service.PreviewBulkPricesAsync(req, ct);
        return Ok(result);
    }

    /// <summary>
    /// Repair the SQLite and Neon caches for a single item/price-list from live SAP data.
    /// Zero SAP mutations — read-only from SAP perspective.
    /// </summary>
    [HttpPost("products/{itemCode}/prices/{priceListNum:int}/cache-repair")]
    public async Task<IActionResult> RepairCache(
        string itemCode, int priceListNum,
        [FromBody] CacheRepairRequest req,
        CancellationToken ct)
    {
        _log.LogInformation(
            "[ProductAdmin] POST cache-repair {Item} PL{Pl} by {By} triggeringAuditId={Aid}",
            itemCode, priceListNum, req.RequestedBy, req.TriggeringAuditId);

        var result = await _service.RepairCacheAsync(itemCode, priceListNum, req, ct);

        return result.Success ? Ok(result) : StatusCode(502, result);
    }

    /// <summary>
    /// Returns all audit records for a bulk request ID.
    /// </summary>
    [HttpGet("products/prices/bulk/{requestId:guid}")]
    public async Task<IActionResult> GetBulkRequestStatus(Guid requestId, CancellationToken ct)
    {
        _log.LogInformation("[ProductAdmin] GET batch status RequestId={Rid}", requestId);
        var result = await _service.GetBulkRequestStatusAsync(requestId, ct);
        return Ok(result);
    }
}
