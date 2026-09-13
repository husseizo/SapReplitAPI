using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/return-requests")]
public sealed class ReturnRequestsController : ControllerBase
{
    private readonly SapService _sap;
    private readonly ILogger<ReturnRequestsController> _logger;

    public ReturnRequestsController(SapService sap, ILogger<ReturnRequestsController> logger)
    {
        _sap    = sap;
        _logger = logger;
    }

    // POST /api/return-requests
    // Creates a Return Request (ORRR) from an invoice.
    // Idempotent: same app_ref returns the same ORRR without creating a duplicate.
    [HttpPost]
    public IActionResult CreateReturnRequest([FromBody] CreateReturnRequestDto dto)
    {
        if (dto.InvoiceDocEntry <= 0)
            return BadRequest(new { error = "invoice_doc_entry is required and must be > 0" });

        if (string.IsNullOrWhiteSpace(dto.AppRef))
            return BadRequest(new { error = "app_ref is required" });

        if (dto.Lines is null || dto.Lines.Count == 0)
            return BadRequest(new { error = "lines must not be empty" });

        if (dto.Lines.Any(l => l.Quantity <= 0))
            return BadRequest(new { error = "all line quantities must be > 0" });

        try
        {
            var result = _sap.CreateReturnRequest(dto);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[ReturnRequestsController] CreateReturnRequest failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnRequestsController] CreateReturnRequest unexpected error");
            return StatusCode(500, new { error = "Internal error creating return request" });
        }
    }

    // GET /api/return-requests?cardCode=&status=&limit=
    [HttpGet]
    public IActionResult ListReturnRequests(
        [FromQuery] string? cardCode,
        [FromQuery] string? status,
        [FromQuery] int limit = 50)
    {
        try
        {
            var q = new ListReturnRequestsQuery { CardCode = cardCode, Status = status, Limit = Math.Min(limit, 200) };
            var result = _sap.ListReturnRequests(q);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnRequestsController] ListReturnRequests error");
            return StatusCode(500, new { error = "Internal error listing return requests" });
        }
    }

    // GET /api/return-requests/list  (alias — matches frontend contract)
    [HttpGet("list")]
    public IActionResult ListReturnRequestsAlias(
        [FromQuery] string? cardCode,
        [FromQuery] string? status,
        [FromQuery] int limit = 50)
        => ListReturnRequests(cardCode, status, limit);

    // GET /api/return-requests/{docEntry}
    [HttpGet("{docEntry:int}")]
    public IActionResult GetReturnRequest(int docEntry)
    {
        try
        {
            var result = _sap.GetReturnRequestByDocEntry(docEntry);
            if (result is null)
                return NotFound(new { error = $"Return Request DocEntry={docEntry} not found" });
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnRequestsController] GetReturnRequest error");
            return StatusCode(500, new { error = "Internal error reading return request" });
        }
    }

    // POST /api/return-requests/{docEntry}/cancel
    // Cancels an open ORRR. Only allowed while DocStatus=O.
    [HttpPost("{docEntry:int}/cancel")]
    public IActionResult CancelReturnRequest(int docEntry)
    {
        try
        {
            _sap.CancelReturnRequest(docEntry);
            return Ok(new { doc_entry = docEntry, status = "Cancelled" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[ReturnRequestsController] CancelReturnRequest failed DocEntry={DocEntry}", docEntry);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnRequestsController] CancelReturnRequest error");
            return StatusCode(500, new { error = "Internal error cancelling return request" });
        }
    }
}
