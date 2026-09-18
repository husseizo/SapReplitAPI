using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Services.Invoice;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/invoices")]
public class InvoiceBackfillController : ControllerBase
{
    private readonly InvoiceBaseRefBackfillService _backfill;
    private readonly ILogger<InvoiceBackfillController> _log;

    public InvoiceBackfillController(
        InvoiceBaseRefBackfillService backfill,
        ILogger<InvoiceBackfillController> log)
    {
        _backfill = backfill;
        _log = log;
    }

    // POST /api/invoices/backfill-base-refs
    // Admin one-shot: reads INV1 base refs from SAP and patches SQLite + Neon.
    // Idempotent — safe to run multiple times.
    [HttpPost("backfill-base-refs")]
    public async Task<IActionResult> BackfillBaseRefs(CancellationToken ct)
    {
        _log.LogInformation("[InvoiceBackfill] Manual backfill requested via API.");
        var result = await _backfill.RunAsync(ct);
        return Ok(new
        {
            doc_entries_scanned    = result.DocEntriesScanned,
            lines_updated          = result.LinesUpdated,
            doc_entries_with_errors = result.DocEntriesWithErrors,
            errors                 = result.Errors
        });
    }
}
