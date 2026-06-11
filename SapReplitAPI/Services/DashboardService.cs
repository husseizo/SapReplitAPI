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
using SapReplitAPI.Models.InvoiceLifecycle;

namespace SapReplitAPI.Services
{
    public class DashboardService
    {
        private readonly CacheDbContext _db;
        private readonly ILogger<DashboardService> _logger;
        private readonly InvoiceLifecycleStatusService _invoiceLifecycleStatusService;
        private readonly string _sqliteConnectionString;
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _writeLocks = new();

        public DashboardService(
            CacheDbContext db,
            ILogger<DashboardService> logger,
            IConfiguration configuration,
            InvoiceLifecycleStatusService invoiceLifecycleStatusService)
        {
            _db = db;
            _logger = logger;
            _invoiceLifecycleStatusService = invoiceLifecycleStatusService;
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

                // Refresh the salesperson/day snapshot so reruns replace stale data instead of
                // skipping rows that were inserted by an earlier job attempt.
                using (var deleteCmd = conn.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = @"
DELETE FROM DetailedInvoiceStatusCache
WHERE SlpCode = @SlpCode
  AND DATE(PostingDate) = DATE(@PostingDate);";
                    deleteCmd.Parameters.AddWithValue("@SlpCode", slpCode);
                    deleteCmd.Parameters.AddWithValue("@PostingDate",
                        (object?)rows.First().PostingDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

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
VALUES (
    @SalesName, @PostingDate, @InvoiceNo, @ReinvoicedFrom, @InvoiceStatus, @PaidDate,
    @Customer, @CashSales, @CreditSales, @ReturnedCashInvoice, @PaymentsStatus, @CancellationStatus,
    @SlpCode
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
                await UpdateSyncMetadataAsync("InvoiceStatusCache");
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

        private async Task UpdateSyncMetadataAsync(string type)
        {
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == type);
            if (meta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = type, LastSyncedAt = DateTime.UtcNow });
            else
                meta.LastSyncedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }

        // ==========================================================
        // 2) Centralized invoice status interpretation
        //    ALIGNED with your CachedInvoice & Canceled values
        // ==========================================================
        private InvoiceLifecycleStatus GetLifecycleStatus(CachedInvoice inv)
        {
            return _invoiceLifecycleStatusService.ParseStatusDisplay(
                inv.DocStatusDisplay,
                inv.Canceled,
                inv.DocStatus);
        }

        private string GetInvoiceStatus(CachedInvoice inv)
        {
            return _invoiceLifecycleStatusService.ToDashboardStatus(GetLifecycleStatus(inv));
        }

        private decimal GetOutstandingAmount(CachedInvoice inv)
        {
            return _invoiceLifecycleStatusService.GetOutstandingAmount(inv);
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

                if (status == "Closed" || status == "Paid")
                {
                    cash += i.DocTotal;
                }
                else if (status == "Open" || status == "Partially Paid")
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

                    var amount = status == "Partially Paid" ? GetOutstandingAmount(i) : i.DocTotal;
                    if (isAged) credit += amount;
                    else pending += amount;
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

                if (status == "Closed" || status == "Paid")
                {
                    cash += i.DocTotal;
                    continue;
                }

                if (status == "Open" || status == "Partially Paid")
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

                    var amount = status == "Partially Paid" ? GetOutstandingAmount(i) : i.DocTotal;
                    if (isAged) credit += amount;
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
                        (!slpCode.HasValue || x.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var processed = invoices
                .Select(x =>
                {
                    var status = GetInvoiceStatus(x);
                    if (status == "Cancelled" || status == "Replaced")
                        return (decimal?)null;

                    decimal amount = x.DocTotal;

                    if (status == "Cancelled-Reversal")
                        amount = -amount;

                    return (decimal?)amount;
                })
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
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
                        x.DocDate >= startOfMonth &&
                        (!slpCode.HasValue || x.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            var result = invoices
                .Select(i =>
                {
                    var status = GetInvoiceStatus(i);
                    var outstanding = GetOutstandingAmount(i);

                    return new { status, outstanding };
                })
                .Where(x =>
                    x.status != "Cancelled" &&
                    x.status != "Cancelled-Reversal" &&
                    x.status != "Replaced" &&
                    x.status != "Paid" &&
                    x.outstanding > 0)
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
                        i.BalanceDue > 0)
                    .ToListAsync()
            );

            var filtered = invoices.Where(i =>
            {
                var status = GetInvoiceStatus(i);
                return status != "Cancelled" &&
                       status != "Cancelled-Reversal" &&
                       status != "Replaced" &&
                       status != "Paid";
            });
            if (slpCode.HasValue)
                filtered = filtered.Where(i => i.SalesEmployeeCode == slpCode.Value);

            return filtered
                .Select(i =>
                {
                    var status = GetInvoiceStatus(i);
                    var total = status == "Partially Paid"
                        ? GetOutstandingAmount(i)
                        : status == "Cancelled-Reversal"
                            ? -i.DocTotal
                            : i.DocTotal;

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
                .Where(x => x.Status != "Cancelled" && x.Status != "Replaced")
                .GroupBy(x => x.DocDate.ToString("yyyy-MM"))
                .Select(g => new RevenueTrendDto
                {
                    Period = g.Key,
                    OpenRevenue = g.Where(x => x.Status == "Open" || x.Status == "Partially Paid").Sum(x => x.DocTotal),
                    ClosedRevenue = g.Where(x => x.Status == "Closed" || x.Status == "Paid" || x.Status == "Cancelled-Reversal")
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
                .Where(x => x.Status != "Cancelled" && x.Status != "Replaced")
                .GroupBy(x => x.DocDate.ToString("yyyy-MM"))
                .Select(g => new RevenueTrendDto
                {
                    Period = g.Key,
                    OpenRevenue = g.Where(x => x.Status == "Open" || x.Status == "Partially Paid").Sum(x => x.DocTotal),
                    ClosedRevenue = g.Where(x => x.Status == "Closed" || x.Status == "Paid" || x.Status == "Cancelled-Reversal")
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

                if (status == "Closed" || status == "Paid")
                {
                    cash += i.DocTotal;
                    continue;
                }

                if (status == "Open" || status == "Partially Paid")
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

                    var amount = status == "Partially Paid" ? GetOutstandingAmount(i) : i.DocTotal;
                    if (isAged) credit += amount;
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

                if (status == "Closed" || status == "Paid")
                {
                    agg.cash += inv.DocTotal;
                }
                else if (status == "Open" || status == "Partially Paid")
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

                    var amount = status == "Partially Paid" ? GetOutstandingAmount(inv) : inv.DocTotal;
                    if (isAged) agg.credit += amount;
                    else agg.pending += amount;
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
                        (!slpCode.HasValue || i.SalesEmployeeCode == slpCode.Value))
                    .ToListAsync()
            );

            invoices = invoices
                .Where(i =>
                {
                    var status = GetInvoiceStatus(i);
                    return status == "Open" || status == "Partially Paid";
                })
                .ToList();

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
                    Aged = isAged ? GetOutstandingAmount(inv) : 0m,
                    Pending = isAged ? 0m : GetOutstandingAmount(inv)
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
