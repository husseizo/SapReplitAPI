using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/returns")]
public sealed class ReturnsController : ControllerBase
{
    private readonly SapService _sap;
    private readonly ILogger<ReturnsController> _logger;

    public ReturnsController(SapService sap, ILogger<ReturnsController> logger)
    {
        _sap    = sap;
        _logger = logger;
    }

    // POST /api/returns
    // Creates an ORIN (A/R Credit Memo) from an ORRR (Return Request).
    // Idempotent: same app_ref returns same ORIN without creating a duplicate.
    // Gate 7: bin/whs mismatch fails before ORIN.Add().
    // Gate 8: quantity > open_qty fails before ORIN.Add().
    [HttpPost]
    public IActionResult CreateReturn([FromBody] CreateReturnDto dto)
    {
        if (dto.ReturnRequestDocEntry <= 0)
            return BadRequest(new { error = "return_request_doc_entry is required and must be > 0" });

        if (string.IsNullOrWhiteSpace(dto.AppRef))
            return BadRequest(new { error = "app_ref is required" });

        if (dto.Lines is null || dto.Lines.Count == 0)
            return BadRequest(new { error = "lines must not be empty" });

        if (dto.Lines.Any(l => l.Quantity <= 0))
            return BadRequest(new { error = "all line quantities must be > 0" });

        try
        {
            var result = _sap.CreateCreditMemoFromReturnRequest(dto);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[ReturnsController] CreateReturn failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnsController] CreateReturn unexpected error");
            return StatusCode(500, new { error = "Internal error creating credit memo" });
        }
    }

    // GET /api/returns/list?cardCode=&limit=
    [HttpGet("list")]
    public IActionResult ListReturns(
        [FromQuery] string? cardCode,
        [FromQuery] int limit = 50)
    {
        try
        {
            var q = new ListReturnsQuery { CardCode = cardCode, Limit = Math.Min(limit, 200) };
            var result = _sap.ListCreditMemos(q);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnsController] ListReturns error");
            return StatusCode(500, new { error = "Internal error listing credit memos" });
        }
    }

    // GET /api/returns/{docEntry}
    [HttpGet("{docEntry:int}")]
    public IActionResult GetReturn(int docEntry)
    {
        try
        {
            var result = _sap.GetReturnResponseByDocEntry(docEntry);
            if (result is null)
                return NotFound(new { error = $"Credit Memo DocEntry={docEntry} not found" });
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReturnsController] GetReturn error");
            return StatusCode(500, new { error = "Internal error reading credit memo" });
        }
    }
}
