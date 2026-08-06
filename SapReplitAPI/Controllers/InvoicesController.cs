using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Services;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly InvoiceFromDeliveryService _service;
    private readonly ILogger<InvoicesController> _log;

    public InvoicesController(
        InvoiceFromDeliveryService service,
        ILogger<InvoicesController> log)
    {
        _service = service;
        _log = log;
    }

    /// <summary>
    /// Manually trigger invoice creation from all open deliveries (ODLN).
    /// The job also runs automatically every morning at 08:30.
    /// </summary>
    /// <remarks>
    /// Body: { "docDueDate": "2026-08-31" }
    /// docDueDate is required. It is used as the payment due date on the invoice
    /// only when the delivery itself has no DocDueDate; otherwise the delivery's
    /// own DocDueDate takes precedence.
    /// </remarks>
    [HttpPost("from-deliveries")]
    public async Task<IActionResult> CreateFromDeliveries([FromBody] CreateFromDeliveriesRequest req)
    {
        if (req?.DocDueDate == null)
            return BadRequest(new { message = "docDueDate is required." });

        _log.LogInformation("📥 [InvoicesController] Manual invoice-from-deliveries triggered. DueDate={DueDate}",
            req.DocDueDate.Value.ToString("yyyy-MM-dd"));

        try
        {
            var result = await _service.ProcessOpenDeliveriesAsync("Manual", req.DocDueDate);
            return Ok(new
            {
                totalFound = result.TotalFound,
                succeeded  = result.Succeeded,
                skipped    = result.Skipped,
                failed     = result.Failed,
                locked     = result.Locked,
                results    = result.Items
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }
}

public class CreateFromDeliveriesRequest
{
    public DateTime? DocDueDate { get; set; }
}
