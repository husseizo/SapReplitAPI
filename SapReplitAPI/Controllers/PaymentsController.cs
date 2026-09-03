// 📁 Path: Controllers/PaymentsController.cs

#pragma warning disable EF1002 // Possible null argument

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Filters;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.Queue;
using SapReplitAPI.Services;
using Microsoft.Data.Sqlite;
using static SapService;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly SapService _sapService;
        private readonly IBackgroundTaskQueue _taskQueue;

        public PaymentsController(SapService sapService, IBackgroundTaskQueue taskQueue)
        {
            _sapService = sapService;
            _taskQueue = taskQueue;
        }

        // 🔁 Retry helper for SQLITE_BUSY/LOCKED
        private static class SqliteRetry
        {
            public static async Task<T> RunAsync<T>(Func<Task<T>> op, int attempts = 3, int initialDelayMs = 150)
            {
                var delay = initialDelayMs;
                for (int i = 1; i <= attempts; i++)
                {
                    try { return await op(); }
                    catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                    {
                        if (i == attempts) throw;
                        await Task.Delay(delay);
                        delay *= 2;
                    }
                }
                throw new InvalidOperationException("Retry exhausted.");
            }
        }

        // 👇 tiny helper to set busy_timeout for the current connection
        private static async Task EnsureBusyTimeoutAsync(CacheDbContext db, int ms = 3000)
        {
            // no-op if connection isn't open yet; EF will open for the command
            await db.Database.ExecuteSqlRawAsync($"PRAGMA busy_timeout={ms};");
            // If you haven't already globally enabled WAL in startup, do it once app-wide.
            // await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }

        [HttpGet("cached-invoice-headers-slp")]
        public async Task<IActionResult> GetCachedInvoiceHeadersBySlp(
            [FromServices] CacheDbContext db,
            [FromQuery] int slpCode,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 1000)
        {
            if (slpCode <= 0)
                return BadRequest(new { message = "slpCode is required and must be a positive integer." });

            // clamp pagination to keep reads small
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 2000);

            var from = new DateTime(2024, 1, 1);

            await EnsureBusyTimeoutAsync(db);

            var baseQuery = db.Invoices
                .AsNoTracking()
                .Where(i => i.DocDate >= from && i.SalesEmployeeCode == slpCode);

            var total = await SqliteRetry.RunAsync(() => baseQuery.CountAsync());
            var results = await SqliteRetry.RunAsync(() =>
                baseQuery
                    .OrderByDescending(i => i.DocDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync());

            return Ok(new PagedResult<CachedInvoice>
            {
                TotalCount = total,
                Page = page,
                PageSize = pageSize,
                Results = results
            });
        }

        // 📦 Get cached invoice headers
        [HttpGet("cached-invoice-headers")]
        public async Task<IActionResult> GetCachedInvoiceHeaders(
            [FromServices] CacheDbContext db,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] int? paymentNumber = null)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 2000);

            var from = new DateTime(2024, 1, 1);

            await EnsureBusyTimeoutAsync(db);

            var query = db.Invoices
                .AsNoTracking()
                .Where(i => i.DocDate >= from);

            // ⚠️ Use a subquery instead of materializing a list, so it's still a single SQL
            if (paymentNumber.HasValue)
            {
                var sub = db.InvoicePayments
                    .AsNoTracking()
                    .Where(p => p.PaymentNumber == paymentNumber.Value)
                    .Select(p => p.DocEntry)
                    .Distinct();

                query = query.Where(i => sub.Contains(i.DocEntry));
            }

            var total = await SqliteRetry.RunAsync(() => query.CountAsync());
            var results = await SqliteRetry.RunAsync(() =>
                query
                    .OrderByDescending(i => i.DocDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync());

            return Ok(new PagedResult<CachedInvoice>
            {
                TotalCount = total,
                Page = page,
                PageSize = pageSize,
                Results = results
            });
        }

        // 📃 Get cached invoice lines
        [HttpGet("cached-invoice-lines")]
        public async Task<IActionResult> GetCachedInvoiceLines(
            [FromServices] CacheDbContext db,
            [FromQuery] int? docEntry,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 1000)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 5000);

            await EnsureBusyTimeoutAsync(db);

            var query = db.InvoiceLines.AsNoTracking().AsQueryable();
            if (docEntry.HasValue) query = query.Where(l => l.DocEntry == docEntry.Value);

            var total = await SqliteRetry.RunAsync(() => query.CountAsync());
            var results = await SqliteRetry.RunAsync(() =>
                query
                    .OrderBy(l => l.DocEntry)
                    .ThenBy(l => l.LineNum)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync());

            return Ok(new
            {
                TotalCount = total,
                Page = page,
                PageSize = pageSize,
                Lines = results
            });
        }

        // 🔁 Cache filtered invoices and payments manually, then return a page
        [HttpGet("cache-invoices")]
        public async Task<IActionResult> CacheInvoicesFiltered(
            [FromServices] InvoiceCacheService cacheService,
            [FromServices] CacheDbContext db,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 2000);

            // 🔎 Filters (adjust to FromQuery as needed)
            string? status = null;
            string? customer = null;
            string? salesEmployeeName = null;
            int? salesEmployeeCode = null;
            DateTime from = new DateTime(2024, 1, 1);
            DateTime? to = null;

            await cacheService.SyncInvoicesFilteredAsync(
                status, customer, salesEmployeeName, salesEmployeeCode, from, to, page, pageSize);

            await EnsureBusyTimeoutAsync(db);

            var query = db.Invoices
                .AsNoTracking()
                .Where(i => i.DocDate >= from);

            if (to.HasValue) query = query.Where(i => i.DocDate <= to.Value);
            if (salesEmployeeCode.HasValue) query = query.Where(i => i.SalesEmployeeCode == salesEmployeeCode.Value);

            var total = await SqliteRetry.RunAsync(() => query.CountAsync());
            var results = await SqliteRetry.RunAsync(() =>
                query
                    .OrderByDescending(i => i.DocDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Include(i => i.Lines) // still AsNoTracking at root query
                    .ToListAsync());

            return Ok(new PagedResult<CachedInvoice>
            {
                TotalCount = total,
                Page = page,
                PageSize = pageSize,
                Results = results
            });
        }

        // 💳 Get cached payments (from January last year to today)
        [HttpGet("cached-payments")]
        public async Task<IActionResult> GetCachedPayments(
            [FromServices] CacheDbContext db,
            [FromQuery] int? docEntry,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 1000)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 5000);

            var startDate = new DateTime(2024, 1, 1);
            var endDate = DateTime.Now;

            await EnsureBusyTimeoutAsync(db);

            var query = db.InvoicePayments
                .AsNoTracking()
                .Where(p => p.PaymentDate >= startDate && p.PaymentDate <= endDate);

            if (docEntry.HasValue)
                query = query.Where(p => p.DocEntry == docEntry.Value);

            var total = await SqliteRetry.RunAsync(() => query.CountAsync());
            var results = await SqliteRetry.RunAsync(() =>
                query
                    .OrderByDescending(p => p.PaymentDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync());

            return Ok(new PagedResult<CachedInvoicePayment>
            {
                TotalCount = total,
                Page = page,
                PageSize = pageSize,
                Results = results
            });
        }

        // 🔘 Manual invoice and payment sync trigger
        [HttpPost("sync/manual")]
        public IActionResult ManualSyncInvoicesAndPayments()
        {
            _taskQueue.Enqueue(async (sp, token) =>
            {
                var scoped = sp.GetRequiredService<InvoiceCacheService>();
                Console.WriteLine("🔁 Starting invoice & payment sync...");
                await scoped.FullSyncInvoicesAsync();
                Console.WriteLine("✅ Invoice & payment sync complete.");
            });

            return Ok(new { Message = "🕓 Invoice & payment sync has been queued." });
        }

        // ─── POST /api/payments/incoming ─────────────────────────────────────────
        [HttpPost("incoming")]
        [ServiceFilter(typeof(ApiKeyAuthFilter))]
        public async Task<IActionResult> PostIncomingPayment(
            [FromBody] CreateIncomingPaymentDto dto,
            [FromServices] CacheDbContext db)
        {
            // ── Validate payment channel ──────────────────────────────────────
            var validChannels = new[]
            {
                "CashOnHand", "MPesaLipa", "TigoLipa",
                "CRDB", "AALNMB", "AdvanceCustomerPayments"
            };
            if (!validChannels.Contains(dto.PaymentChannel))
                return BadRequest(new { message = $"Invalid PaymentChannel '{dto.PaymentChannel}'. Valid: {string.Join(", ", validChannels)}" });

            // ── AdvanceCustomerPayments is settlement-only (invoices required) ─
            if (dto.PaymentChannel == "AdvanceCustomerPayments" && dto.Invoices.Count == 0)
                return BadRequest(new { message = "AdvanceCustomerPayments is for settling invoices from an advance balance — invoices must be specified. To receive an advance, use a physical channel (CashOnHand, MPesaLipa, etc.) with invoices: []." });

            // ── TransferReference required for non-cash physical channels ──────
            var transferChannels = new[] { "MPesaLipa", "TigoLipa", "CRDB", "AALNMB" };
            if (transferChannels.Contains(dto.PaymentChannel) && string.IsNullOrWhiteSpace(dto.TransferReference))
                return BadRequest(new { message = "TransferReference is required for transfer-based payment channels (MPesaLipa, TigoLipa, CRDB, AALNMB)." });

            // ── Advance receipt: invoices empty → TotalAmount required ────────
            if (dto.Invoices.Count == 0 && (dto.TotalAmount == null || dto.TotalAmount <= 0))
                return BadRequest(new { message = "TotalAmount is required when no invoices are specified." });

            // ── Amount consistency check ──────────────────────────────────────
            if (dto.Invoices.Count > 0)
            {
                decimal invoiceSum = dto.Invoices.Sum(i => i.AmountApplied);
                if (invoiceSum <= 0)
                    return BadRequest(new { message = "Sum of AmountApplied across invoices must be greater than zero." });
            }

            // ── Idempotency check ─────────────────────────────────────────────
            // If clientReference already exists, return the original result without re-posting.
            if (!string.IsNullOrWhiteSpace(dto.ClientReference))
            {
                var existing = await db.PaymentIdempotencyLogs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ClientReference == dto.ClientReference);
                if (existing != null)
                    return Ok(new IncomingPaymentResultDto
                    {
                        Success         = true,
                        PaymentDocEntry = existing.PaymentDocEntry,
                        PaymentDocNum   = existing.PaymentDocNum
                    });
            }

            // ── Advance overdraw guard ────────────────────────────────────────
            if (dto.PaymentChannel == "AdvanceCustomerPayments")
            {
                decimal available = _sapService.GetCustomerAdvanceBalance(dto.CardCode);
                decimal requested = dto.Invoices.Sum(i => i.AmountApplied);
                if (requested > available)
                    return UnprocessableEntity(new
                    {
                        message          = $"Insufficient advance balance for {dto.CardCode}. Available: {available:N2}, Requested: {requested:N2}.",
                        availableBalance = available,
                        requestedAmount  = requested
                    });
            }

            // ── Post to SAP ───────────────────────────────────────────────────
            try
            {
                var result = _sapService.PostIncomingPayment(dto);
                if (!result.Success)
                    return UnprocessableEntity(new { message = result.ErrorMessage, sapErrorCode = result.ErrorCode });

                // ── Record idempotency key so retries return the same result ──
                if (!string.IsNullOrWhiteSpace(dto.ClientReference))
                {
                    try
                    {
                        db.PaymentIdempotencyLogs.Add(new PaymentIdempotencyLog
                        {
                            ClientReference = dto.ClientReference,
                            PaymentDocEntry = result.PaymentDocEntry,
                            PaymentDocNum   = result.PaymentDocNum,
                            CreatedAt       = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync();
                    }
                    catch (Exception idempEx)
                    {
                        Console.WriteLine($"⚠️ Idempotency log write failed (non-fatal): {idempEx.Message}");
                    }
                }

                // ── Optimistic cache update (Option B) ───────────────────────
                // SAP is source of truth — if this fails, the 5-min sync corrects it.
                try
                {
                    await UpdateCacheOptimisticAsync(db, dto, result);
                }
                catch (Exception cacheEx)
                {
                    Console.WriteLine($"⚠️ Cache update after payment failed (non-fatal): {cacheEx.Message}");
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ─── POST /api/incoming-payments ─────────────────────────────────────────
        // Alias that reaches the same handler as /api/payments/incoming.
        [HttpPost("/api/incoming-payments")]
        [ServiceFilter(typeof(ApiKeyAuthFilter))]
        public Task<IActionResult> PostIncomingPaymentAlias(
            [FromBody] CreateIncomingPaymentDto dto,
            [FromServices] CacheDbContext db)
            => PostIncomingPayment(dto, db);

        // ─── POST /api/payments/{docEntry}/cancel ─────────────────────────────────
        [HttpPost("{docEntry:int}/cancel")]
        [ServiceFilter(typeof(ApiKeyAuthFilter))]
        public async Task<IActionResult> CancelPaymentByDocEntry(int docEntry)
        {
            if (docEntry <= 0)
                return BadRequest(new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { "docEntry must be a positive integer." }
                });

            OrctSummary? orct;
            try { orct = await _sapService.GetOrctSummaryByDocEntryAsync(docEntry); }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { $"SAP read failed: {ex.Message}" }
                });
            }

            if (orct is null)
                return NotFound(new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { $"No incoming payment found for DocEntry {docEntry}." }
                });

            if (orct.Canceled)
                return Ok(new
                {
                    success = true,
                    data    = new { doc_entry = orct.DocEntry, doc_num = orct.DocNum, already_cancelled = true },
                    errors  = Array.Empty<string>()
                });

            CancelPaymentResult result;
            try { result = _sapService.CancelIncomingPayment(docEntry); }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { $"SAP cancel call failed: {ex.Message}" }
                });
            }

            if (!result.Success)
                return StatusCode(500, new
                {
                    success        = false,
                    data           = (object?)null,
                    errors         = new[] { result.SapErrorMessage ?? "SAP cancellation refused." },
                    sap_error_code = result.SapErrorCode
                });

            return Ok(new
            {
                success = true,
                data    = new { doc_entry = orct.DocEntry, doc_num = orct.DocNum, already_cancelled = false },
                errors  = Array.Empty<string>()
            });
        }

        // ─── POST /api/payments/cancel-by-invoice/{invoiceDocEntry} ───────────────
        [HttpPost("cancel-by-invoice/{invoiceDocEntry:int}")]
        [ServiceFilter(typeof(ApiKeyAuthFilter))]
        public async Task<IActionResult> CancelPaymentByInvoice(int invoiceDocEntry)
        {
            if (invoiceDocEntry <= 0)
                return BadRequest(new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { "invoiceDocEntry must be a positive integer." }
                });

            List<OrctSummary> payments;
            try { payments = await _sapService.GetOrctsByInvoiceDocEntryAsync(invoiceDocEntry); }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { $"SAP read failed: {ex.Message}" }
                });
            }

            if (payments.Count == 0)
                return NotFound(new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { $"No incoming payment found for invoice DocEntry {invoiceDocEntry}." }
                });

            var active    = payments.Where(p => !p.Canceled).ToList();
            var cancelled = payments.Where(p =>  p.Canceled).ToList();

            // Case C: 0 active, exactly 1 historical cancelled — idempotent success.
            if (active.Count == 0 && cancelled.Count == 1)
                return Ok(new
                {
                    success = true,
                    data    = new { doc_entry = cancelled[0].DocEntry, doc_num = cancelled[0].DocNum, already_cancelled = true },
                    errors  = Array.Empty<string>()
                });

            // Case D: 0 active, 0 total — not found (already handled above).

            // Case E: 0 active, multiple cancelled — ambiguous.
            if (active.Count == 0 && cancelled.Count > 1)
                return Conflict(new
                {
                    success    = false,
                    data       = (object?)null,
                    errors     = new[] { $"Ambiguous: {cancelled.Count} cancelled payments are linked to invoice DocEntry {invoiceDocEntry}. Cannot determine which to report." },
                    candidates = cancelled.Select(p => new { doc_entry = p.DocEntry, doc_num = p.DocNum, card_code = p.CardCode, doc_date = p.DocDate, doc_total = p.DocTotal, counter_ref = p.CounterRef })
                });

            // Case B: 2 or more active — ambiguous, do not cancel.
            if (active.Count >= 2)
                return Conflict(new
                {
                    success    = false,
                    data       = (object?)null,
                    errors     = new[] { $"Ambiguous: {active.Count} active payments are linked to invoice DocEntry {invoiceDocEntry}. Specify a payment DocEntry directly via /api/payments/{{docEntry}}/cancel." },
                    candidates = active.Select(p => new { doc_entry = p.DocEntry, doc_num = p.DocNum, card_code = p.CardCode, doc_date = p.DocDate, doc_total = p.DocTotal, counter_ref = p.CounterRef })
                });

            // Case A: exactly 1 active — cancel it.
            var target = active[0];
            CancelPaymentResult result;
            try { result = _sapService.CancelIncomingPayment(target.DocEntry); }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    data    = (object?)null,
                    errors  = new[] { $"SAP cancel call failed: {ex.Message}" }
                });
            }

            if (!result.Success)
                return StatusCode(500, new
                {
                    success        = false,
                    data           = (object?)null,
                    errors         = new[] { result.SapErrorMessage ?? "SAP cancellation refused." },
                    sap_error_code = result.SapErrorCode
                });

            return Ok(new
            {
                success = true,
                data    = new { doc_entry = target.DocEntry, doc_num = target.DocNum, already_cancelled = false },
                errors  = Array.Empty<string>()
            });
        }

        private static async Task UpdateCacheOptimisticAsync(
            CacheDbContext db,
            CreateIncomingPaymentDto dto,
            IncomingPaymentResultDto result)
        {
            bool isCash = dto.PaymentChannel is "CashOnHand" or "AdvanceCustomerPayments";

            // ── Advance receipt (no invoices) — record to InvoicePayments with DocEntry=0 ──
            if (dto.Invoices.Count == 0)
            {
                db.InvoicePayments.Add(new CachedInvoicePayment
                {
                    DocEntry              = 0,
                    PaymentDocEntry       = result.PaymentDocEntry,
                    PaymentNumber         = result.PaymentDocNum,
                    InvoiceDocNum         = 0,
                    PaymentDate           = dto.PaymentDate,
                    CardCode              = dto.CardCode,
                    CardName              = string.Empty,
                    AmountApplied         = dto.TotalAmount ?? 0m,
                    BankTransferAmount    = isCash ? 0m : (dto.TotalAmount ?? 0m),
                    BankTransferReference = dto.TransferReference ?? string.Empty,
                    DebitAccountCode      = string.Empty,
                    DebitAccountName      = dto.PaymentChannel,
                    SalesEmployeeCode     = string.Empty,
                    SalesEmployeeName     = string.Empty,
                    ClientReference       = dto.ClientReference ?? string.Empty,
                });
                await db.SaveChangesAsync();
                return;
            }

            // ── Invoice settlement ────────────────────────────────────────────
            var docEntries = dto.Invoices.Select(i => i.DocEntry).ToList();

            var cachedInvoices = await db.Invoices
                .AsNoTracking()
                .Where(i => docEntries.Contains(i.DocEntry))
                .ToDictionaryAsync(i => i.DocEntry);

            foreach (var inv in dto.Invoices)
            {
                // Atomic SQL — arithmetic happens inside SQLite, no read-modify-write race
                await db.Database.ExecuteSqlRawAsync(@"
                    UPDATE ""Invoices""
                    SET ""PaidToDate"" = ""PaidToDate"" + {0},
                        ""BalanceDue"" = ""BalanceDue"" - {0},
                        ""DocStatus""  = CASE WHEN (""BalanceDue"" - {0}) <= 0 THEN 'C' ELSE 'O' END
                    WHERE ""DocEntry"" = {1}",
                    (double)inv.AmountApplied,
                    inv.DocEntry);

                if (!cachedInvoices.TryGetValue(inv.DocEntry, out var cached))
                    continue;

                db.InvoicePayments.Add(new CachedInvoicePayment
                {
                    DocEntry              = inv.DocEntry,
                    PaymentDocEntry       = result.PaymentDocEntry,
                    PaymentNumber         = result.PaymentDocNum,
                    InvoiceDocNum         = cached.InvoiceDocNum,
                    PaymentDate           = dto.PaymentDate,
                    CardCode              = dto.CardCode,
                    CardName              = cached.CardName,
                    AmountApplied         = inv.AmountApplied,
                    BankTransferAmount    = isCash ? 0m : inv.AmountApplied,
                    BankTransferReference = dto.TransferReference ?? string.Empty,
                    DebitAccountCode      = string.Empty,
                    DebitAccountName      = dto.PaymentChannel,
                    SalesEmployeeCode     = cached.SalesEmployeeCode.ToString(),
                    SalesEmployeeName     = cached.SalesEmployeeName,
                    ClientReference       = dto.ClientReference ?? string.Empty,
                });
            }

            await db.SaveChangesAsync();
        }
    }
}