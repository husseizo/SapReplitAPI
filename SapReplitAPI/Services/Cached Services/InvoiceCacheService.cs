using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Payments;

public class InvoiceCacheService
{
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly ILogger<InvoiceCacheService> _logger;

    // Keep a single lock to prevent overlapping full/delta syncs from different triggers.
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public InvoiceCacheService(CacheDbContext db, SapService sap, ILogger<InvoiceCacheService> logger)
    {
        _db = db;
        _sap = sap;
        _logger = logger;
    }

    private static string ComputeStatusDisplay(string docStatus, string canceled)
    {
        if (docStatus == "O") return "Open";

        if (docStatus == "C")
        {
            if (string.Equals(canceled, "Canceled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
            if (string.Equals(canceled, "Cancellation", StringComparison.OrdinalIgnoreCase)) return "Cancellation";
            return "Closed";
        }

        return string.IsNullOrWhiteSpace(docStatus) ? "Unknown" : docStatus;
    }

    #region FULL SYNC (2024 → Today, chunked by month, UPSERT + batch writes)

    public async Task FullSyncInvoicesAsync()
    {
        await _syncLock.WaitAsync();
        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("🔄 [InvoiceCache] Starting FULL invoice + payment sync (2024-01-01 → today, monthly chunks)...");

            var fromDate = new DateTime(2024, 1, 1);
            var finalTo = DateTime.Today;

            var current = fromDate;
            var totalInv = 0;
            var totalLines = 0;
            var totalPayments = 0;

            while (current <= finalTo)
            {
                var monthStart = current;
                var monthEnd = current.AddMonths(1).AddTicks(-1);
                if (monthEnd > finalTo) monthEnd = finalTo.AddDays(1).AddTicks(-1);

                _logger.LogInformation("📅 [InvoiceCache] Sync window: {From} - {To}", monthStart.ToShortDateString(), monthEnd.ToShortDateString());

                var invoices = _sap.GetInvoices(status: null, customer: null, from: monthStart, to: monthEnd) ?? new List<InvoiceDto>();
                if (invoices.Count > 0)
                {
                    var (hCount, lCount) = await UpsertInvoicesAndLinesAsync(invoices);
                    totalInv += hCount;
                    totalLines += lCount;
                }

                var payments = _sap.GetInvoicePayments(from: monthStart, to: monthEnd) ?? new List<InvoicePaymentDto>();
                if (payments.Count > 0)
                {
                    var pCount = await UpsertPaymentsAsync(payments);
                    totalPayments += pCount;
                }

                current = current.AddMonths(1);
            }

            // Update sync metadata once at the end
            var now = DateTime.Now;
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Invoice");
            if (meta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "Invoice", LastSyncedAt = now });
            else
                meta.LastSyncedAt = now;

            await _db.SaveChangesAsync();

            sw.Stop();
            _logger.LogInformation(
                "✅ [InvoiceCache] FULL sync completed: {Inv} headers, {Lines} lines, {Pay} payments in {Sec:F2}s.",
                totalInv, totalLines, totalPayments, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ [InvoiceCache] FULL sync failed after {Sec:F2}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region DELTA SYNC (incremental, based on LastSyncedAt, UPSERT + batch writes)

    /// <summary>
    /// Dedicated delta sync for the scheduled job.
    /// Uses a lock timeout so it never blocks indefinitely behind a running full sync.
    /// Metadata is only advanced when SAP actually returns and we successfully persist data.
    /// </summary>
    public async Task DeltaSyncInvoicesAsync()
    {
        var sw = Stopwatch.StartNew();

        // Skip rather than queue behind a long-running full sync.
        bool acquired = await _syncLock.WaitAsync(TimeSpan.FromSeconds(45));
        if (!acquired)
        {
            _logger.LogWarning("⏭️ [InvoiceCache] DELTA sync skipped – full sync is currently holding the lock.");
            return;
        }

        try
        {
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Invoice");
            DateTime syncFrom = meta?.LastSyncedAt ?? new DateTime(2024, 1, 1);
            DateTime syncTo = DateTime.Today.AddDays(1).AddTicks(-1);

            _logger.LogInformation("⏳ [InvoiceCache] DELTA sync window: {From} → {To}", syncFrom, syncTo);

            var invoices = _sap.GetInvoices(status: null, customer: null, from: syncFrom, to: syncTo)
                           ?? new List<InvoiceDto>();
            var payments = _sap.GetInvoicePayments(from: syncFrom, to: syncTo)
                           ?? new List<InvoicePaymentDto>();

            if (invoices.Count == 0 && payments.Count == 0)
            {
                sw.Stop();
                _logger.LogInformation(
                    "ℹ️ [InvoiceCache] DELTA sync: SAP returned no invoices or payments in window – metadata unchanged ({Sec:F2}s).",
                    sw.Elapsed.TotalSeconds);
                return;
            }

            var invCount = 0;
            var lineCount = 0;
            var payCount = 0;

            if (invoices.Count > 0)
                (invCount, lineCount) = await UpsertInvoicesAndLinesAsync(invoices);

            if (payments.Count > 0)
                payCount = await UpsertPaymentsAsync(payments);

            // Advance the watermark only after a successful write.
            var now = DateTime.Now;
            if (meta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "Invoice", LastSyncedAt = now });
            else
                meta.LastSyncedAt = now;

            await _db.SaveChangesAsync();

            sw.Stop();
            _logger.LogInformation(
                "✅ [InvoiceCache] DELTA sync done: {Inv} headers, {Lines} lines, {Pay} payments in {Sec:F2}s.",
                invCount, lineCount, payCount, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ [InvoiceCache] DELTA sync failed after {Sec:F2}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SyncInvoicesFilteredAsync(
        string? status,
        string? customer,
        string? salesEmployeeName,
        int? salesEmployeeCode,
        DateTime? from,
        DateTime? to,
        int page,
        int pageSize)
    {
        await _syncLock.WaitAsync();
        var sw = Stopwatch.StartNew();

        try
        {
            // Determine delta window
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Invoice");
            DateTime syncFrom = from ?? meta?.LastSyncedAt ?? new DateTime(2024, 1, 1);
            DateTime syncTo = (to ?? DateTime.Today).AddDays(1).AddTicks(-1);

            _logger.LogInformation("⏳ [InvoiceCache] DELTA invoice sync from {From} to {To}", syncFrom, syncTo);

            var invoices = _sap.GetInvoices(status, customer, syncFrom, syncTo) ?? new List<InvoiceDto>();

            if (!string.IsNullOrWhiteSpace(salesEmployeeName))
                invoices = invoices
                    .Where(i => (i.SalesEmployeeName ?? "").Contains(salesEmployeeName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (salesEmployeeCode.HasValue)
                invoices = invoices
                    .Where(i => i.SalesEmployeeCode == salesEmployeeCode.Value)
                    .ToList();

            if (invoices.Count == 0)
            {
                _logger.LogInformation("⚠️ [InvoiceCache] DELTA sync: no invoices matched filters; only updating metadata.");
                // still bump metadata so we don't keep hitting the same window
                if (meta == null)
                    await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "Invoice", LastSyncedAt = DateTime.Now });
                else
                    meta.LastSyncedAt = DateTime.Now;

                await _db.SaveChangesAsync();
                return;
            }

            var (invCount, lineCount) = await UpsertInvoicesAndLinesAsync(invoices);

            // Payments for the same window
            var payments = _sap.GetInvoicePayments(from: syncFrom, to: syncTo) ?? new List<InvoicePaymentDto>();
            var payCount = 0;
            if (payments.Count > 0)
            {
                payCount = await UpsertPaymentsAsync(payments);
            }

            // Update sync metadata
            var now = DateTime.Now;
            if (meta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "Invoice", LastSyncedAt = now });
            else
                meta.LastSyncedAt = now;

            await _db.SaveChangesAsync();

            sw.Stop();
            _logger.LogInformation(
                "✅ [InvoiceCache] DELTA sync completed: {Inv} headers, {Lines} lines, {Pay} payments in {Sec:F2}s.",
                invCount, lineCount, payCount, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ [InvoiceCache] DELTA sync failed after {Sec:F2}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #endregion

    #region INTERNAL HELPERS – UPSERT + batch writes

    /// <summary>
    /// Upserts invoice headers via raw SQLite UPSERT, and replaces lines per DocEntry.
    /// </summary>
    private async Task<(int invoiceCount, int lineCount)> UpsertInvoicesAndLinesAsync(List<InvoiceDto> invoices)
    {
        if (invoices == null || invoices.Count == 0)
            return (0, 0);

        // Flatten header DTOs → CachedInvoice
        var headerRows = invoices.Select(i => new CachedInvoice
        {
            DocEntry = i.DocEntry,
            DocNum = i.DocNum,
            InvoiceDocNum = i.DocNum,
            DocDate = i.DocDate,
            DocStatus = i.Status ?? "",
            Canceled = i.Canceled ?? "",
            DocStatusDisplay = ComputeStatusDisplay(i.Status ?? "", i.Canceled ?? ""),
            CardCode = i.CardCode ?? "",
            CardName = i.CardName ?? "",
            DocTotal = i.DocTotal,
            PaidToDate = i.PaidToDate,
            BalanceDue = i.BalanceDue,
            DaysOverdue = i.DaysOverdue,
            SalesEmployeeCode = i.SalesEmployeeCode,
            SalesEmployeeName = i.SalesEmployeeName ?? "",
            GroupNum = i.GroupNum
        }).ToList();

        // Flatten lines; we will delete existing lines for these DocEntry values
        var flatLines = new List<CachedInvoiceLine>();
        foreach (var inv in invoices)
        {
            foreach (var line in inv.Lines ?? Enumerable.Empty<InvoiceLineDto>())
            {
                flatLines.Add(new CachedInvoiceLine
                {
                    DocEntry = inv.DocEntry,
                    LineNum = (int)line.LineNum,
                    ItemCode = line.ItemCode ?? "",
                    Dscription = line.Dscription ?? "",
                    Quantity = line.Quantity,
                    Price = line.Price,
                    LineTotal = line.LineTotal,
                    U_Item_Name = line.U_Item_Name ?? "",
                    U_MdlTEST = line.U_MdlTEST ?? ""
                });
            }
        }

        var docEntries = headerRows.Select(h => h.DocEntry).Distinct().ToList();
        var headerCount = headerRows.Count;
        var lineCount = flatLines.Count;

        // Use the existing EF connection + transaction
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var sqliteConn = (SqliteConnection)connection;
            var sqliteTx = (SqliteTransaction)tx.GetDbTransaction();

            // 1) UPSERT headers via raw SQLite
            using (var cmd = sqliteConn.CreateCommand())
            {
                cmd.Transaction = sqliteTx;
                cmd.CommandText = @"
INSERT INTO ""Invoices""
    (""DocEntry"", ""DocNum"", ""InvoiceDocNum"", ""DocDate"", ""DocStatus"", ""Canceled"",
     ""DocStatusDisplay"", ""CardCode"", ""CardName"", ""DocTotal"", ""PaidToDate"", ""BalanceDue"",
     ""DaysOverdue"", ""SalesEmployeeCode"", ""SalesEmployeeName"", ""GroupNum"")
VALUES
    ($DocEntry, $DocNum, $InvoiceDocNum, $DocDate, $DocStatus, $Canceled,
     $DocStatusDisplay, $CardCode, $CardName, $DocTotal, $PaidToDate, $BalanceDue,
     $DaysOverdue, $SalesEmployeeCode, $SalesEmployeeName, $GroupNum)
ON CONFLICT(""DocEntry"") DO UPDATE SET
    ""DocNum"" = excluded.""DocNum"",
    ""InvoiceDocNum"" = excluded.""InvoiceDocNum"",
    ""DocDate"" = excluded.""DocDate"",
    ""DocStatus"" = excluded.""DocStatus"",
    ""Canceled"" = excluded.""Canceled"",
    ""DocStatusDisplay"" = excluded.""DocStatusDisplay"",
    ""CardCode"" = excluded.""CardCode"",
    ""CardName"" = excluded.""CardName"",
    ""DocTotal"" = excluded.""DocTotal"",
    ""PaidToDate"" = excluded.""PaidToDate"",
    ""BalanceDue"" = excluded.""BalanceDue"",
    ""DaysOverdue"" = excluded.""DaysOverdue"",
    ""SalesEmployeeCode"" = excluded.""SalesEmployeeCode"",
    ""SalesEmployeeName"" = excluded.""SalesEmployeeName"",
    ""GroupNum"" = excluded.""GroupNum"";";

                // Prepare parameters once
                var pDocEntry = cmd.Parameters.Add("$DocEntry", SqliteType.Integer);
                var pDocNum = cmd.Parameters.Add("$DocNum", SqliteType.Integer);
                var pInvoiceDocNum = cmd.Parameters.Add("$InvoiceDocNum", SqliteType.Integer);
                var pDocDate = cmd.Parameters.Add("$DocDate", SqliteType.Text);
                var pDocStatus = cmd.Parameters.Add("$DocStatus", SqliteType.Text);
                var pCanceled = cmd.Parameters.Add("$Canceled", SqliteType.Text);
                var pDocStatusDisplay = cmd.Parameters.Add("$DocStatusDisplay", SqliteType.Text);
                var pCardCode = cmd.Parameters.Add("$CardCode", SqliteType.Text);
                var pCardName = cmd.Parameters.Add("$CardName", SqliteType.Text);
                var pDocTotal = cmd.Parameters.Add("$DocTotal", SqliteType.Real);
                var pPaidToDate = cmd.Parameters.Add("$PaidToDate", SqliteType.Real);
                var pBalanceDue = cmd.Parameters.Add("$BalanceDue", SqliteType.Real);
                var pDaysOverdue = cmd.Parameters.Add("$DaysOverdue", SqliteType.Integer);
                var pSalesEmployeeCode = cmd.Parameters.Add("$SalesEmployeeCode", SqliteType.Integer);
                var pSalesEmployeeName = cmd.Parameters.Add("$SalesEmployeeName", SqliteType.Text);
                var pGroupNum = cmd.Parameters.Add("$GroupNum", SqliteType.Integer);

                foreach (var h in headerRows)
                {
                    pDocEntry.Value = h.DocEntry;
                    pDocNum.Value = h.DocNum;
                    pInvoiceDocNum.Value = h.InvoiceDocNum;
                    pDocDate.Value = h.DocDate.ToString("yyyy-MM-dd");
                    pDocStatus.Value = h.DocStatus ?? "";
                    pCanceled.Value = h.Canceled ?? "";
                    pDocStatusDisplay.Value = h.DocStatusDisplay ?? "";
                    pCardCode.Value = h.CardCode ?? "";
                    pCardName.Value = h.CardName ?? "";
                    pDocTotal.Value = h.DocTotal;
                    pPaidToDate.Value = h.PaidToDate;
                    pBalanceDue.Value = h.BalanceDue;
                    pDaysOverdue.Value = h.DaysOverdue;
                    pSalesEmployeeCode.Value = h.SalesEmployeeCode;
                    pSalesEmployeeName.Value = h.SalesEmployeeName ?? "";
                    pGroupNum.Value = h.GroupNum;

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // 2) Replace lines for these invoices (delete-by-docEntry + insert new)
            if (docEntries.Count > 0)
            {
                var oldLines = _db.InvoiceLines.Where(l => docEntries.Contains(l.DocEntry));
                _db.InvoiceLines.RemoveRange(oldLines);

                var oldDetect = _db.ChangeTracker.AutoDetectChangesEnabled;
                _db.ChangeTracker.AutoDetectChangesEnabled = false;
                try
                {
                    await _db.InvoiceLines.AddRangeAsync(flatLines);
                    await _db.SaveChangesAsync();
                }
                finally
                {
                    _db.ChangeTracker.AutoDetectChangesEnabled = oldDetect;
                }
            }

            await tx.CommitAsync();
        }

        return (headerCount, lineCount);
    }

    /// <summary>
    /// Upserts payments via raw SQLite UPSERT on PaymentDocEntry.
    /// </summary>
    private async Task<int> UpsertPaymentsAsync(List<InvoicePaymentDto> payments)
    {
        if (payments == null || payments.Count == 0)
            return 0;

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var sqliteConn = (SqliteConnection)connection;
            var sqliteTx = (SqliteTransaction)tx.GetDbTransaction();

            using (var cmd = sqliteConn.CreateCommand())
            {
                cmd.Transaction = sqliteTx;

                cmd.CommandText = @"
INSERT INTO ""InvoicePayments""
(
    ""DocEntry"", ""InvoiceDocNum"", ""PaymentDocEntry"", ""PaymentNumber"",
    ""PaymentDate"", ""CardCode"", ""CardName"", ""AmountApplied"",
    ""BankTransferAmount"", ""BankTransferReference"",
    ""DebitAccountCode"", ""DebitAccountName"",
    ""SalesEmployeeCode"", ""SalesEmployeeName""
)
VALUES
(
    $DocEntry, $InvoiceDocNum, $PaymentDocEntry, $PaymentNumber,
    $PaymentDate, $CardCode, $CardName, $AmountApplied,
    $BankTransferAmount, $BankTransferReference,
    $DebitAccountCode, $DebitAccountName,
    $SalesEmployeeCode, $SalesEmployeeName
)
ON CONFLICT(""PaymentDocEntry"") DO UPDATE SET
    ""DocEntry"" = excluded.""DocEntry"",
    ""InvoiceDocNum"" = excluded.""InvoiceDocNum"",
    ""PaymentNumber"" = excluded.""PaymentNumber"",
    ""PaymentDate"" = excluded.""PaymentDate"",
    ""CardCode"" = excluded.""CardCode"",
    ""CardName"" = excluded.""CardName"",
    ""AmountApplied"" = excluded.""AmountApplied"",
    ""BankTransferAmount"" = excluded.""BankTransferAmount"",
    ""BankTransferReference"" = excluded.""BankTransferReference"",
    ""DebitAccountCode"" = excluded.""DebitAccountCode"",
    ""DebitAccountName"" = excluded.""DebitAccountName"",
    ""SalesEmployeeCode"" = excluded.""SalesEmployeeCode"",
    ""SalesEmployeeName"" = excluded.""SalesEmployeeName"";
";

                var pDocEntry = cmd.Parameters.Add("$DocEntry", SqliteType.Integer);
                var pInvoiceDocNum = cmd.Parameters.Add("$InvoiceDocNum", SqliteType.Integer);
                var pPaymentDocEntry = cmd.Parameters.Add("$PaymentDocEntry", SqliteType.Integer);
                var pPaymentNumber = cmd.Parameters.Add("$PaymentNumber", SqliteType.Integer);
                var pPaymentDate = cmd.Parameters.Add("$PaymentDate", SqliteType.Text);
                var pCardCode = cmd.Parameters.Add("$CardCode", SqliteType.Text);
                var pCardName = cmd.Parameters.Add("$CardName", SqliteType.Text);
                var pAmountApplied = cmd.Parameters.Add("$AmountApplied", SqliteType.Real);
                var pBankTransferAmount = cmd.Parameters.Add("$BankTransferAmount", SqliteType.Real);
                var pBankTransferReference = cmd.Parameters.Add("$BankTransferReference", SqliteType.Text);
                var pDebitAccountCode = cmd.Parameters.Add("$DebitAccountCode", SqliteType.Text);
                var pDebitAccountName = cmd.Parameters.Add("$DebitAccountName", SqliteType.Text);
                var pSalesEmployeeCode = cmd.Parameters.Add("$SalesEmployeeCode", SqliteType.Text);
                var pSalesEmployeeName = cmd.Parameters.Add("$SalesEmployeeName", SqliteType.Text);

                foreach (var p in payments)
                {
                    pDocEntry.Value = p.DocEntry;
                    pInvoiceDocNum.Value = p.InvoiceDocNum;
                    pPaymentDocEntry.Value = p.PaymentDocEntry;
                    pPaymentNumber.Value = p.PaymentNumber;
                    pPaymentDate.Value = p.PaymentDate.ToString("yyyy-MM-dd");
                    pCardCode.Value = p.CardCode ?? "";
                    pCardName.Value = p.CardName ?? "";
                    pAmountApplied.Value = p.AmountApplied;
                    pBankTransferAmount.Value = p.BankTransferAmount;
                    pBankTransferReference.Value = p.BankTransferReference ?? "";
                    pDebitAccountCode.Value = p.DebitAccountCode ?? "";
                    pDebitAccountName.Value = p.DebitAccountName ?? "";
                    pSalesEmployeeCode.Value = p.SalesEmployeeCode ?? "";
                    pSalesEmployeeName.Value = p.SalesEmployeeName ?? "";

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
        }

        return payments.Count;
    }

    #endregion
}