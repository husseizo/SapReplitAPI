using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.Warehouse;
using SapReplitAPI.Services.Warehouse;

namespace SapReplitAPI.Controllers;

/// <summary>
/// Warehouse App picking endpoints — "Select Bin &amp; Pick" workflow.
///
/// GET  /api/warehouse/pick-lists/{absEntry}/state           — derive UI state + live candidates
/// GET  /api/warehouse/pick-lists/{absEntry}/bin-candidates  — live OIBQ candidates for one line
/// POST /api/warehouse/pick-lists/{absEntry}/confirm-pick    — picker confirms bin selection
///
/// Does NOT create/modify ORDR, ODLN, OINV, OPKL, RDR1, or any ZF record.
/// Only calls SAP DI API pick list Update() via WarehouseBinPickService.
/// </summary>
[ApiController]
[Route("api/warehouse/pick-lists")]
public sealed class WarehousePickController : ControllerBase
{
    private readonly WarehouseBinPickService        _service;
    private readonly ILogger<WarehousePickController> _log;

    public WarehousePickController(
        WarehouseBinPickService        service,
        ILogger<WarehousePickController> log)
    {
        _service = service;
        _log     = log;
    }

    /// <summary>
    /// GET /api/warehouse/pick-lists/{absEntry}/state
    ///
    /// Returns UI state + per-line candidates (if AwaitingBinSelection) or allocated bins.
    /// State matrix:
    ///   R + bins=0 + stock  → AwaitingBinSelection  → "Select Bin &amp; Pick"
    ///   R + bins=0 + no stk → NoStock               → no action
    ///   R + bins&gt;0         → ReadyAllocated         → "Confirm Pick"
    ///   Y                   → Picked
    ///   P                   → PartiallyPicked
    ///   D                   → PartiallyDelivered
    ///   C / Canceled=Y      → Closed
    /// </summary>
    [HttpGet("{absEntry:int}/state")]
    public async Task<IActionResult> GetState(int absEntry, CancellationToken ct)
    {
        var result = await _service.GetStateAsync(absEntry, ct);
        if (result is null)
            return NotFound(new { absEntry, error = "Pick list not found in cache." });
        return Ok(result);
    }

    /// <summary>
    /// GET /api/warehouse/pick-lists/{absEntry}/bin-candidates?pickEntry={pickEntry}
    ///
    /// Returns live OIBQ bin candidates for one specific pick line.
    /// Calls SAP fresh — always reflects current physical bin stock.
    /// </summary>
    [HttpGet("{absEntry:int}/bin-candidates")]
    public async Task<IActionResult> GetBinCandidates(
        int absEntry,
        [FromQuery] int pickEntry,
        CancellationToken ct)
    {
        var (found, candidates) = await _service.GetBinCandidatesAsync(absEntry, pickEntry, ct);
        if (!found)
            return NotFound(new { absEntry, pickEntry, error = "Pick list line not found in cache." });
        return Ok(new { absEntry, pickEntry, count = candidates.Count, candidates });
    }

    /// <summary>
    /// POST /api/warehouse/pick-lists/{absEntry}/confirm-pick
    ///
    /// Confirms picker's bin selection. Calls SAP DI API. Triggers targeted cache refresh.
    /// 200 OK       — all lines confirmed successfully
    /// 207 Multi-Status — partial success (check Lines[].Success)
    /// 400 Bad Request — validation failure (no SAP call made)
    /// 422 Unprocessable — SAP rejected the pick
    /// </summary>
    [HttpPost("{absEntry:int}/confirm-pick")]
    public async Task<IActionResult> ConfirmPick(
        int absEntry,
        [FromBody] ConfirmPickRequest req,
        CancellationToken ct)
    {
        if (req.Lines.Count == 0)
            return BadRequest(new { error = "Lines must not be empty." });

        _log.LogInformation(
            "[WH-PICK] ConfirmPick POST AbsEntry={Abs} Lines={N}", absEntry, req.Lines.Count);

        var result = await _service.ConfirmPickAsync(absEntry, req, ct);

        if (result.Success)
            return Ok(result);

        bool anySuccess = result.Lines.Any(l => l.Success);
        if (anySuccess)
            return StatusCode(207, result); // partial

        // All failed — check if it was a validation error (rc=-1) or SAP error
        bool allValidation = result.Lines.All(l => l.SapRc == -1);
        return allValidation ? BadRequest(result) : UnprocessableEntity(result);
    }
}
