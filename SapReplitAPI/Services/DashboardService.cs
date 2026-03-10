using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SapReplitAPI.DTOs.Dashboard;
using SapReplitAPI.Models.Cache;

namespace SapReplitAPI.Services
{
    public class DashboardService
    {
        private readonly CacheDbContext _db;
        private readonly ILogger<DashboardService> _logger;
        private readonly string _sqliteConnectionString;
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _writeLocks = new();

        public DashboardService(
            CacheDbContext db,
            ILogger<DashboardService> logger,
            IConfiguration configuration)
        {
            _db = db;
            _logger = logger;
            _sqliteConnectionString = configuration.GetConnectionString("CacheDB")!;
        }

        // ---------------------------
        // Generic SQLite retry helper
        // ---------------------------
        public static class SqliteRetry
        {
            public static async Task<T> RunAsync<T>(Func<Task<T>> op, int attempts = 3, int initialDelayMs = 150)
            {
                var delay = initialDelayMs;
                for (int i = 1; i <= attempts; i++)
                {
                    try
                    {
                        return await op();
                    }
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

        // ==========================================================
        // 1) CACHE DETAILED INVOICE STATUS REPORT (raw SQLite insert)
        // ==========================================================
        public async Task CacheInvoiceStatusReportAsync(
            string salesName,
            DateTime docDate,
            IEnumerable<DetailedInvoiceReportRow> reportRows)
        {
            var rows = reportRows?.ToList() ?? new List<DetailedInvoiceReportRow>();
            if (rows.Count == 0)
            {
                _logger.LogWarning("⚠️ No rows supplied for {SalesName} on {DocDate}",
                    salesName, docDate.ToShortDateString());
                return;
            }

            var slpCode = rows.First().SlpCode;
            var semaphore = _writeLocks.GetOrAdd(slpCode, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_sqliteConnectionString);
                await conn.OpenAsync();

                // WAL + busy timeout for write-heavy reports
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
                    pragma.ExecuteNonQuery();
                }

                using var tx = conn.BeginTransaction();

                foreach (var row in rows)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;

                    cmd.CommandText = @"
INSERT INTO DetailedInvoiceStatusCache (
    SalesName, PostingDate, InvoiceNo, ReinvoicedFrom, InvoiceStatus, PaidDate,
    Customer, CashSales, CreditSales, ReturnedCashInvoice, PaymentsStatus, CancellationStatus,
    SlpCode
)
SELECT @SalesName, @PostingDate, @InvoiceNo, @ReinvoicedFrom, @InvoiceStatus, @PaidDate,
       @Customer, @CashSales, @CreditSales, @ReturnedCashInvoice, @PaymentsStatus, @CancellationStatus,
       @SlpCode
WHERE NOT EXISTS (
    SELECT 1 FROM DetailedInvoiceStatusCache
    WHERE SlpCode = @SlpCode
      AND DATE(PostingDate) = DATE(@PostingDate)
      AND InvoiceNo = @InvoiceNo
);";

                    cmd.Parameters.AddWithValue("@SalesName", row.SalesName ?? "");
                    cmd.Parameters.AddWithValue("@PostingDate",
                        (object?)row.PostingDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@InvoiceNo", row.InvoiceNo ?? "");
                    cmd.Parameters.AddWithValue("@ReinvoicedFrom", row.ReinvoicedFrom ?? "");
                    cmd.Parameters.AddWithValue("@InvoiceStatus", row.InvoiceStatus ?? "");
                    cmd.Parameters.AddWithValue("@PaidDate",
                        (object?)row.PaidDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Customer", row.Customer ?? "");
                    cmd.Parameters.AddWithValue("@CashSales", row.CashSales);
                    cmd.Parameters.AddWithValue("@CreditSales", row.CreditSales);
                    cmd.Parameters.AddWithValue("@ReturnedCashInvoice", row.ReturnedCashInvoice);
                    cmd.Parameters.AddWithValue("@PaymentsStatus", row.PaymentsStatus ?? "");
                    cmd.Parameters.AddWithValue("@CancellationStatus", row.CancellationStatus ?? "");
                    cmd.Parameters.AddWithValue("@SlpCode", row.SlpCode);

                    const int maxRetries = 3;
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try
                        {
                            await cmd.ExecuteNonQueryAsync();
                            break;
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                        {
                            if (attempt == maxRetries) throw;
                            _logger.LogWarning("🔁 Retry {Attempt} due to lock: {Message}", attempt, ex.Message);
                            await Task.Delay(100 * attempt);
                        }
                    }
                }

                await tx.CommitAsync();
                _logger.LogInformation("✅ Cached {RowCount} invoice report rows for SlpCode {SlpCode}",
                    rows.Count, slpCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to cache invoice report for {SalesName}", salesName);
                throw;
            }
            finally
            {
                semaphore.Release();
            }
        }

        // ==========================================================
        // 2) Centralized invoice status interpretation
        //    ALIGNED with your CachedInvoice & Canceled values
        // ==========================================================
        private string GetInvoiceStatus(CachedInvoice inv)
        {
            // From your InvoiceCacheService:
            // Canceled values: "Not Canceled", "Canceled", "Cancellation"
            //
            // We normalize:
            //  - "Cancellation" => Cancelled-Reversal (negative impact)
            //  - "Canceled"     => Cancelled (ignore for most KPIs)
            //  - "Not Canceled" + DocStatus C => Closed
            //  - "Not Canceled" + DocStatus O => Open

            if (string.Equals(inv.Canceled, "Cancellation", StringComparison.OrdinalIgnoreCase))
                return "Cancelled-Reversal";

            if (string.Equals(inv.Canceled, "Canceled", StringComparison.OrdinalIgnoreCase))
                return "Cancelled";

            if (inv.DocStatus == "C")
                return "Closed";

            if (inv.DocStatus == "O")
                return "Open";

            return "Unknown";
        }

        // ==========================================================
        // 3) Month-to-date sales (current month, cash / credit / pending)
        // ==========================================================
        public async Task<List<MonthToDateSalesDto>> GetMonthToDateSalesAsync(int? slpCode)
        {
            var today = DateTime.Today;

            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(i =>
                        i.DocDate.Year == today.Year &&
                        i.DocDate.Month == today.Month &&
                        i.Canceled == "Not Canceled" &&
                        (i.DocStatus == "O" || i.DocStatus == "C") &&
                        (!slpCode.HasValue || i.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var regionByCard = await SqliteRetry.RunAsync(async () =>
                await _db.Customers
                    .AsNoTracking()
                    .Select(c => new { c.CardCode, Region = (c.Region ?? "").Trim().ToUpper() })
                    .ToDictionaryAsync(x => x.CardCode, x => x.Region)
            );

            var payDates = await SqliteRetry.RunAsync(async () =>
                await _db.InvoicePayments
                    .AsNoTracking()
                    .GroupBy(p => p.DocEntry)
                    .Select(g => new { DocEntry = g.Key, PaymentDate = g.Max(x => x.PaymentDate) })
                    .ToDictionaryAsync(x => x.DocEntry, x => (DateTime?)x.PaymentDate)
            );

            var perInvoice = invoices.Select(i =>
            {
                decimal cash = 0m, credit = 0m, pending = 0m;
                var status = GetInvoiceStatus(i);

                if (status == "Closed")
                {
                    cash += i.DocTotal;
                }
                else if (status == "Open")
                {
                    var slpName = (i.SalesEmployeeName ?? "").Trim();
                    var region = regionByCard.TryGetValue(i.CardCode ?? "", out var r) ? r : "";

                    var baseDate = (payDates.TryGetValue(i.DocEntry, out var pd) && pd.HasValue)
                        ? pd.Value.Date
                        : i.DocDate.Date;

                    var days = (today - baseDate).TotalDays;

                    bool isAged =
                        slpName.Equals("Mohamed Rashid", StringComparison.OrdinalIgnoreCase) ||
                        slpName.Equals("Mohamed Laseko", StringComparison.OrdinalIgnoreCase)
                            ? days > 0
                            : (region == "DAR ES SALAAM" ? days > 2 : days > 5);

                    if (isAged) credit += i.DocTotal;
                    else pending += i.DocTotal;
                }
                else if (status == "Cancelled-Reversal")
                {
                    // treat as negative closed
                    cash -= i.DocTotal;
                }

                return new
                {
                    i.SalesEmployeeCode,
                    Cash = cash,
                    Credit = credit,
                    Pending = pending
                };
            });

            return perInvoice
                .GroupBy(x => x.SalesEmployeeCode)
                .Select(g => new MonthToDateSalesDto
                {
                    SlpCode = g.Key,
                    CashSales = g.Sum(r => r.Cash),
                    CreditSales = g.Sum(r => r.Credit),
                    PendingSales = g.Sum(r => r.Pending)
                })
                .ToList();
        }

        // ==========================================================
        // 4) Cash vs Credit (SAP-aligned) for a month
        // ==========================================================
        public async Task<CashVsCreditSummaryDto> GetCashVsCreditSapAlignedAsync(int? slpCode, int? year, int? month)
        {
            var today = DateTime.Today;
            int y = year ?? today.Year;
            int m = month ?? today.Month;

            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(i =>
                        i.DocDate.Year == y &&
                        i.DocDate.Month == m &&
                        i.Canceled == "Not Canceled" &&
                        (i.DocStatus == "O" || i.DocStatus == "C") &&
                        (!slpCode.HasValue || i.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var customerRegion = await SqliteRetry.RunAsync(async () =>
                await _db.Customers
                    .AsNoTracking()
                    .Select(c => new { c.CardCode, c.Region })
                    .ToDictionaryAsync(
                        x => x.CardCode,
                        x => (x.Region ?? "").Trim().ToUpperInvariant())
            );

            var payDates = await SqliteRetry.RunAsync(async () =>
                await _db.InvoicePayments
                    .AsNoTracking()
                    .GroupBy(p => p.DocEntry)
                    .Select(g => new { DocEntry = g.Key, PaymentDate = g.Max(x => x.PaymentDate) })
                    .ToDictionaryAsync(x => x.DocEntry, x => (DateTime?)x.PaymentDate)
            );

            decimal cash = 0m, credit = 0m;
            var agingRef = new DateTime(y, m, DateTime.DaysInMonth(y, m));

            foreach (var i in invoices)
            {
                var status = GetInvoiceStatus(i);

                if (status == "Closed")
                {
                    cash += i.DocTotal;
                    continue;
                }

                if (status == "Open")
                {
                    var slpName = (i.SalesEmployeeName ?? "").Trim();
                    var region = customerRegion.TryGetValue(i.CardCode ?? "", out var r)
                        ? r
                        : "";

                    var baseDate = payDates.TryGetValue(i.DocEntry, out var pd) && pd.HasValue
                        ? pd.Value.Date
                        : i.DocDate.Date;

                    var days = (agingRef - baseDate).TotalDays;

                    bool isAged =
                        slpName.Equals("Mohamed Rashid", StringComparison.OrdinalIgnoreCase) ||
                        slpName.Equals("Mohamed Laseko", StringComparison.OrdinalIgnoreCase)
                            ? days > 0
                            : (region == "DAR ES SALAAM" ? days > 2 : days > 5);

                    if (isAged) credit += i.DocTotal;
                }
                else if (status == "Cancelled-Reversal")
                {
                    cash -= i.DocTotal;
                }
            }

            return new CashVsCreditSummaryDto { Cash = cash, Credit = credit };
        }

        // ==========================================================
        // 5) Average order value (range + current month)
        // ==========================================================
        public async Task<AvgOrderValueDto> GetAverageOrderValueRangeAsync(
            int? slpCode,
            DateTime startDate,
            DateTime endDate)
        {
            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(x =>
                        x.DocDate >= startDate &&
                        x.DocDate <= endDate &&
                        x.Canceled == "Not Canceled" &&
                        (x.DocStatus == "O" || x.DocStatus == "C") &&
                        (!slpCode.HasValue || x.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var processed = invoices
                .Select(x =>
                {
                    var status = GetInvoiceStatus(x);
                    decimal amount = x.DocTotal;

                    if (status == "Cancelled-Reversal")
                        amount = -amount;

                    return amount;
                })
                .ToList();

            int count = processed.Count;
            decimal total = processed.Sum();
            decimal avg = count == 0 ? 0 : total / count;

            return new AvgOrderValueDto
            {
                AverageOrderValue = avg,
                SlpCode = slpCode?.ToString() ?? "all"
            };
        }

        public async Task<AvgOrderValueDto> GetAverageOrderValueAsync(int? slpCode)
        {
            var today = DateTime.Today;
            var start = new DateTime(today.Year, today.Month, 1);
            var end = start.AddMonths(1).AddTicks(-1);

            return await GetAverageOrderValueRangeAsync(slpCode, start, end);
        }

        // ==========================================================
        // 6) Unpaid orders (current month + range)
        // ==========================================================
        public async Task<UnpaidOrdersDto> GetUnpaidOrdersAsync(int? slpCode)
        {
            var now = DateTime.Now;
            var startOfMonth = new DateTime(now.Year, now.Month, 1);

            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(x =>
                        x.Canceled == "Not Canceled" &&
                        (x.DocStatus == "O" || x.DocStatus == "C") &&
                        x.DocDate >= startOfMonth &&
                        (!slpCode.HasValue || x.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var result = invoices
                .Select(i =>
                {
                    var status = GetInvoiceStatus(i);
                    var outstanding = i.DocTotal - i.PaidToDate;

                    return new { status, outstanding };
                })
                .Where(x => x.status != "Cancelled" && x.outstanding > 0)
                .ToList();

            return new UnpaidOrdersDto
            {
                UnpaidCount = result.Count,
                TotalUnpaid = result.Sum(x =>
                    x.status == "Cancelled-Reversal" ? -x.outstanding : x.outstanding)
            };
        }

        public async Task<List<UnpaidInvoiceDto>> GetUnpaidOrdersByDateRangeAsync(
            int? slpCode,
            DateTime startDate,
            DateTime endDate)
        {
            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(i =>
                        i.DocDate >= startDate &&
                        i.DocDate <= endDate &&
                        i.Canceled == "Not Canceled" &&
                        (i.DocStatus == "O" || i.DocStatus == "C") &&
                        i.BalanceDue > 0)
                    .ToListAsync()
            );

            var filtered = invoices.Where(i => GetInvoiceStatus(i) != "Cancelled");
            if (slpCode.HasValue)
                filtered = filtered.Where(i => i.SalesEmployeeCode == slpCode.Value);

            return filtered
                .Select(i =>
                {
                    var status = GetInvoiceStatus(i);
                    var total = status == "Cancelled-Reversal" ? -i.DocTotal : i.DocTotal;

                    return new UnpaidInvoiceDto
                    {
                        DocNum = i.DocNum,
                        CardName = i.CardName,
                        DocDate = i.DocDate,
                        DocTotal = total,
                        PaidToDate = i.PaidToDate,
                        SlpCode = i.SalesEmployeeCode,
                        SlpName = i.SalesEmployeeName
                    };
                })
                .ToList();
        }

        // ==========================================================
        // 7) Revenue trend (global + per salesperson)
        // ==========================================================
        public async Task<List<RevenueTrendDto>> GetRevenueTrendAsync()
        {
            var endDate = DateTime.Today;
            var startDate = endDate.AddMonths(-5).AddDays(-endDate.Day + 1);

            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(x =>
                        x.DocDate >= startDate &&
                        x.DocDate <= endDate)
                    .ToListAsync()
            );

            return invoices
                .Select(x => new
                {
                    x.DocDate,
                    x.DocTotal,
                    Status = GetInvoiceStatus(x)
                })
                .Where(x => x.Status != "Cancelled")
                .GroupBy(x => x.DocDate.ToString("yyyy-MM"))
                .Select(g => new RevenueTrendDto
                {
                    Period = g.Key,
                    OpenRevenue = g.Where(x => x.Status == "Open").Sum(x => x.DocTotal),
                    ClosedRevenue = g.Where(x => x.Status == "Closed" || x.Status == "Cancelled-Reversal")
                                     .Sum(x => x.Status == "Cancelled-Reversal" ? -x.DocTotal : x.DocTotal)
                })
                .OrderBy(x => x.Period)
                .ToList();
        }

        public async Task<List<RevenueTrendDto>> GetRevenueTrendBySlpCodeAsync(int slpCode)
        {
            var endDate = DateTime.Today;
            var startDate = endDate.AddMonths(-5).AddDays(-endDate.Day + 1);

            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(x =>
                        x.DocDate >= startDate &&
                        x.DocDate <= endDate &&
                        x.SalesEmployeeCode == slpCode)
                    .ToListAsync()
            );

            return invoices
                .Select(x => new
                {
                    x.DocDate,
                    x.DocTotal,
                    Status = GetInvoiceStatus(x)
                })
                .Where(x => x.Status != "Cancelled")
                .GroupBy(x => x.DocDate.ToString("yyyy-MM"))
                .Select(g => new RevenueTrendDto
                {
                    Period = g.Key,
                    OpenRevenue = g.Where(x => x.Status == "Open").Sum(x => x.DocTotal),
                    ClosedRevenue = g.Where(x => x.Status == "Closed" || x.Status == "Cancelled-Reversal")
                                     .Sum(x => x.Status == "Cancelled-Reversal" ? -x.DocTotal : x.DocTotal)
                })
                .OrderBy(x => x.Period)
                .ToList();
        }

        // ==========================================================
        // 8) Cash vs Credit over arbitrary date range
        // ==========================================================
        public async Task<CashVsCreditSummaryDto> GetCashVsCreditRangeAsync(
            int? slpCode,
            DateTime startDate,
            DateTime endDate)
        {
            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(i =>
                        i.DocDate >= startDate &&
                        i.DocDate <= endDate &&
                        i.Canceled == "Not Canceled" &&
                        (i.DocStatus == "O" || i.DocStatus == "C") &&
                        (!slpCode.HasValue || i.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var payDates = await SqliteRetry.RunAsync(async () =>
                await _db.InvoicePayments
                    .AsNoTracking()
                    .GroupBy(p => p.DocEntry)
                    .Select(g => new { DocEntry = g.Key, PaymentDate = g.Max(x => x.PaymentDate) })
                    .ToDictionaryAsync(x => x.DocEntry, x => (DateTime?)x.PaymentDate)
            );

            var regionByCard = await SqliteRetry.RunAsync(async () =>
                await _db.Customers
                    .AsNoTracking()
                    .Select(c => new { c.CardCode, Region = (c.Region ?? "").Trim().ToUpper() })
                    .ToDictionaryAsync(x => x.CardCode, x => x.Region)
            );

            decimal cash = 0m, credit = 0m;
            var agingRef = endDate.Date;

            foreach (var i in invoices)
            {
                var status = GetInvoiceStatus(i);

                if (status == "Closed")
                {
                    cash += i.DocTotal;
                    continue;
                }

                if (status == "Open")
                {
                    var slpName = (i.SalesEmployeeName ?? "").Trim();
                    var region = regionByCard.TryGetValue(i.CardCode ?? "", out var r) ? r : "";

                    var baseDate = (payDates.TryGetValue(i.DocEntry, out var pd) && pd.HasValue)
                        ? pd.Value.Date
                        : i.DocDate.Date;

                    var days = (agingRef - baseDate).TotalDays;

                    bool isAged =
                        slpName.Equals("Mohamed Rashid", StringComparison.OrdinalIgnoreCase) ||
                        slpName.Equals("Mohamed Laseko", StringComparison.OrdinalIgnoreCase)
                            ? days > 0
                            : (region == "DAR ES SALAAM" ? days > 2 : days > 5);

                    if (isAged) credit += i.DocTotal;
                }
                else if (status == "Cancelled-Reversal")
                {
                    cash -= i.DocTotal;
                }
            }

            return new CashVsCreditSummaryDto { Cash = cash, Credit = credit };
        }

        // ==========================================================
        // 9) Month-to-date sales RANGE (for admin + per SLP)
        // ==========================================================
        public async Task<object> GetMonthToDateSalesRangeAsync(
            int? slpCode,
            DateTime startDate,
            DateTime endDate)
        {
            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(i =>
                        i.DocDate >= startDate &&
                        i.DocDate <= endDate &&
                        i.Canceled == "Not Canceled" &&
                        (i.DocStatus == "O" || i.DocStatus == "C") &&
                        (!slpCode.HasValue || i.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var regionByCard = await SqliteRetry.RunAsync(async () =>
                await _db.Customers
                    .AsNoTracking()
                    .Select(c => new { c.CardCode, Region = (c.Region ?? "").Trim().ToUpper() })
                    .ToDictionaryAsync(x => x.CardCode, x => x.Region)
            );

            var payDates = await SqliteRetry.RunAsync(async () =>
                await _db.InvoicePayments
                    .AsNoTracking()
                    .GroupBy(p => p.DocEntry)
                    .Select(g => new { DocEntry = g.Key, PaymentDate = g.Max(x => x.PaymentDate) })
                    .ToDictionaryAsync(x => x.DocEntry, x => (DateTime?)x.PaymentDate)
            );

            var agingRef = endDate.Date;
            var buckets = new Dictionary<int, (decimal cash, decimal credit, decimal pending)>();

            foreach (var inv in invoices)
            {
                var status = GetInvoiceStatus(inv);
                var key = inv.SalesEmployeeCode;
                if (!buckets.TryGetValue(key, out var agg))
                    agg = (0m, 0m, 0m);

                if (status == "Closed")
                {
                    agg.cash += inv.DocTotal;
                }
                else if (status == "Open")
                {
                    var slpName = (inv.SalesEmployeeName ?? "").Trim();
                    var region = regionByCard.TryGetValue(inv.CardCode ?? "", out var r) ? r : "";

                    var baseDate = (payDates.TryGetValue(inv.DocEntry, out var pd) && pd.HasValue)
                        ? pd.Value.Date
                        : inv.DocDate.Date;

                    var days = (agingRef - baseDate).TotalDays;

                    bool isAged =
                        slpName.Equals("Mohamed Rashid", StringComparison.OrdinalIgnoreCase) ||
                        slpName.Equals("Mohamed Laseko", StringComparison.OrdinalIgnoreCase)
                            ? days > 0
                            : (region == "DAR ES SALAAM" ? days > 2 : days > 5);

                    if (isAged) agg.credit += inv.DocTotal;
                    else agg.pending += inv.DocTotal;
                }
                else if (status == "Cancelled-Reversal")
                {
                    agg.cash -= inv.DocTotal;
                }

                buckets[key] = agg;
            }

            if (slpCode.HasValue)
            {
                var (cash, credit, pending) = buckets.TryGetValue(slpCode.Value, out var v)
                    ? v
                    : (0m, 0m, 0m);

                return new MonthToDateSalesDto
                {
                    SlpCode = slpCode.Value,
                    CashSales = cash,
                    CreditSales = credit,
                    PendingSales = pending
                };
            }

            var list = buckets
                .Select(kv => new MonthToDateSalesDto
                {
                    SlpCode = kv.Key,
                    CashSales = kv.Value.cash,
                    CreditSales = kv.Value.credit,
                    PendingSales = kv.Value.pending
                })
                .OrderBy(x => x.SlpCode)
                .ToList();

            return list;
        }

        // ==========================================================
        // 10) Open docs (all years, aged vs pending)
        // Previously filtered to current year only, which hid unpaid
        // invoices from prior years — now returns ALL outstanding open invoices.
        // ==========================================================
        public async Task<List<OpenDocsBreakdownDto>> GetOpenDocsCurrentYearAsync(int? slpCode)
        {
            var today = DateTime.Today;

            var invoices = await SqliteRetry.RunAsync(async () =>
                await _db.Invoices
                    .AsNoTracking()
                    .Where(i =>
                        i.DocStatus == "O" &&
                        i.Canceled == "Not Canceled" &&
                        (!slpCode.HasValue || i.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            if (invoices.Count == 0)
                return new List<OpenDocsBreakdownDto>();

            var regionByCard = await SqliteRetry.RunAsync(async () =>
                await _db.Customers
                    .AsNoTracking()
                    .Select(c => new { c.CardCode, Region = (c.Region ?? "").Trim().ToUpper() })
                    .ToDictionaryAsync(x => x.CardCode, x => x.Region)
            );

            var payDates = await SqliteRetry.RunAsync(async () =>
                await _db.InvoicePayments
                    .AsNoTracking()
                    .GroupBy(p => p.DocEntry)
                    .Select(g => new { DocEntry = g.Key, PaymentDate = g.Max(x => x.PaymentDate) })
                    .ToDictionaryAsync(x => x.DocEntry, x => (DateTime?)x.PaymentDate)
            );

            var agingRef = today.Date;

            var perInvoice = invoices.Select(inv =>
            {
                var baseDate = (payDates.TryGetValue(inv.DocEntry, out var pd) && pd.HasValue)
                    ? pd.Value.Date
                    : inv.DocDate.Date;

                var slpName = (inv.SalesEmployeeName ?? "").Trim();
                var region = regionByCard.TryGetValue(inv.CardCode ?? "", out var r) ? r : "";

                var days = (agingRef - baseDate).TotalDays;

                bool isAged =
                    slpName.Equals("Mohamed Rashid", StringComparison.OrdinalIgnoreCase) ||
                    slpName.Equals("Mohamed Laseko", StringComparison.OrdinalIgnoreCase)
                        ? days > 0
                        : (region == "DAR ES SALAAM" ? days > 2 : days > 5);

                return new
                {
                    inv.SalesEmployeeCode,
                    Aged = isAged ? inv.DocTotal : 0m,
                    Pending = isAged ? 0m : inv.DocTotal
                };
            });

            return perInvoice
                .GroupBy(x => x.SalesEmployeeCode)
                .Select(g => new OpenDocsBreakdownDto
                {
                    SlpCode = g.Key,
                    AgedOpen = g.Sum(x => x.Aged),
                    PendingOpen = g.Sum(x => x.Pending)
                    
                })
                .OrderBy(x => x.SlpCode)
                .ToList();
        }
    }
}