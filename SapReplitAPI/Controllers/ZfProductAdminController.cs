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
}
