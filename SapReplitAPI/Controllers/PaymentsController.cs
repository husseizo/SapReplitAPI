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
    }
}