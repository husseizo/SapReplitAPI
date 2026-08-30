using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Services.CachedServices;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/deliveries")]
public class DeliveriesController : ControllerBase
{
    private readonly DeliveryCacheService _cache;
    private readonly ILogger<DeliveriesController> _log;

    public DeliveriesController(DeliveryCacheService cache, ILogger<DeliveriesController> log)
    {
        _cache = cache;
        _log   = log;
    }

    /// <summary>GET /api/deliveries — filtered list. All params optional.</summary>
    [HttpGet]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] string? status,
        [FromQuery] string? canceled,
        [FromQuery] string? cardCode,
        [FromQuery] string? customer,
        [FromQuery] int?    slpCode,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var rows = await _cache.GetDeliveriesAsync(status, canceled, cardCode, customer, slpCode, from, to, ct);
        return Ok(rows);
    }

    /// <summary>GET /api/deliveries/open — DocStatus='O' && Canceled='N'.</summary>
    [HttpGet("open")]
    public async Task<IActionResult> GetOpen(CancellationToken ct)
    {
        var rows = await _cache.GetOpenDeliveriesAsync(ct);
        return Ok(rows);
    }

    /// <summary>GET /api/deliveries/{docEntry} — single delivery with lines.</summary>
    [HttpGet("{docEntry:int}")]
    public async Task<IActionResult> GetByDocEntry(int docEntry, CancellationToken ct)
    {
        var delivery = await _cache.GetByDocEntryAsync(docEntry, ct);
        if (delivery is null)
            return NotFound(new { error = $"Delivery DocEntry={docEntry} not found in cache." });

        return Ok(delivery);
    }

    /// <summary>GET /api/deliveries/{docEntry}/lines — lines only.</summary>
    [HttpGet("{docEntry:int}/lines")]
    public async Task<IActionResult> GetLines(int docEntry, CancellationToken ct)
    {
        var lines = await _cache.GetLinesAsync(docEntry, ct);
        return Ok(lines);
    }
}
