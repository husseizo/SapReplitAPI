// 📁 Path: Controllers/PaymentsController.cs

#pragma warning disable EF1002 // Possible null argument

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.Queue;
using SapReplitAPI.Services;
using Microsoft.Data.Sqlite;

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

            // ── TransferReference required for non-cash channels ──────────────
            if (dto.PaymentChannel != "CashOnHand" && string.IsNullOrWhiteSpace(dto.TransferReference))
                return BadRequest(new { message = "TransferReference is required for transfer-based payment channels." });

            // ── Advance payment: invoices empty → TotalAmount required ────────
            if (dto.Invoices.Count == 0 && (dto.TotalAmount == null || dto.TotalAmount <= 0))
                return BadRequest(new { message = "TotalAmount is required when no invoices are specified (advance payment)." });

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
                if (dto.Invoices.Count > 0)
                {
                    try
                    {
                        await UpdateCacheOptimisticAsync(db, dto, result);
                    }
                    catch (Exception cacheEx)
                    {
                        Console.WriteLine($"⚠️ Cache update after payment failed (non-fatal): {cacheEx.Message}");
                    }
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        private static async Task UpdateCacheOptimisticAsync(
            CacheDbContext db,
            CreateIncomingPaymentDto dto,
            IncomingPaymentResultDto result)
        {
            var docEntries = dto.Invoices.Select(i => i.DocEntry).ToList();
            bool isCash = dto.PaymentChannel == "CashOnHand";

            // Read the invoices we need metadata from (InvoiceDocNum, CardName, SalesEmployee)
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
                });
            }

            await db.SaveChangesAsync();
        }
    }
}