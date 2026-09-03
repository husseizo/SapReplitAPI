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
using SapReplitAPI.Models.InvoiceLifecycle;
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

    private static string ComputeLegacyStatusDisplay(string docStatus, string canceled)
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

                var payments = _sap.GetInvoicePaymentsForSync(monthStart, monthEnd, isDelta: false) ?? new List<InvoicePaymentDto>();
                if (payments.Count > 0)
                {
                    var pCount = await UpsertPaymentsAsync(payments);
                    totalPayments += pCount;
                }

                current = current.AddMonths(1);
            }

            // Update both watermarks once at the end
            var now = DateTime.Now;
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Invoice");
            if (meta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "Invoice", LastSyncedAt = now });
            else
                meta.LastSyncedAt = now;

            var payMeta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "InvoicePayment");
            if (payMeta == null)
                await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "InvoicePayment", LastSyncedAt = now });
            else
                payMeta.LastSyncedAt = now;

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
            // Independent watermarks: Invoice (UpdateDate-based) and InvoicePayment (UpdateDate+UpdateTS-based).
            var invMeta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Invoice");
            var payMeta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "InvoicePayment");

            DateTime invFrom = invMeta?.LastSyncedAt ?? new DateTime(2024, 1, 1);
            DateTime payFrom = payMeta?.LastSyncedAt ?? new DateTime(2024, 1, 1);
            DateTime syncTo  = DateTime.Today.AddDays(1).AddTicks(-1);

            _logger.LogInformation(
                "⏳ [InvoiceCache] DELTA sync: invoices from {InvFrom}, payments from {PayFrom}",
                invFrom, payFrom);

            var invoices = _sap.GetInvoices(status: null, customer: null, from: invFrom, to: syncTo, isDelta: true)
                           ?? new List<InvoiceDto>();
            var payments = _sap.GetInvoicePaymentsForSync(payFrom, syncTo, isDelta: true)
                           ?? new List<InvoicePaymentDto>();

            if (invoices.Count == 0 && payments.Count == 0)
            {
                sw.Stop();
                _logger.LogInformation(
                    "ℹ️ [InvoiceCache] DELTA sync: SAP returned nothing – watermarks unchanged ({Sec:F2}s).",
                    sw.Elapsed.TotalSeconds);
                return;
            }

            var invCount  = 0;
            var lineCount = 0;
            var payCount  = 0;
            var now       = DateTime.Now;

            if (invoices.Count > 0)
            {
                (invCount, lineCount) = await UpsertInvoicesAndLinesAsync(invoices);
                if (invMeta == null)
                    await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "Invoice", LastSyncedAt = now });
                else
                    invMeta.LastSyncedAt = now;
            }

            if (payments.Count > 0)
            {
                payCount = await UpsertPaymentsAsync(payments);
                if (payMeta == null)
                    await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = "InvoicePayment", LastSyncedAt = now });
                else
                    payMeta.LastSyncedAt = now;
            }

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
            var payments = _sap.GetInvoicePaymentsForSync(syncFrom, syncTo, isDelta: false) ?? new List<InvoicePaymentDto>();
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

        var lifecycleResults = _sap.GetInvoiceLifecycleStatusResults(invoices.Select(i => i.DocEntry));

        // Flatten header DTOs -> CachedInvoice
        var headerRows = invoices.Select(i => new CachedInvoice
        {
            DocEntry = i.DocEntry,
            DocNum = i.DocNum,
            InvoiceDocNum = i.DocNum,
            DocDate = i.DocDate,
            DocStatus = i.Status ?? "",
            Canceled = i.Canceled ?? "",
            DocStatusDisplay = lifecycleResults.TryGetValue(i.DocEntry, out var lifecycle)
                ? lifecycle.DocStatusDisplay
                : ComputeLegacyStatusDisplay(i.Status ?? "", i.Canceled ?? ""),
            CardCode = i.CardCode ?? "",
            CardName = i.CardName ?? "",
            DocTotal = i.DocTotal,
            PaidToDate = i.PaidToDate,
            BalanceDue = i.BalanceDue,
            DaysOverdue = i.DaysOverdue,
            SalesEmployeeCode = i.SalesEmployeeCode,
            SalesEmployeeName = i.SalesEmployeeName ?? "",
            GroupNum = i.GroupNum,
            ZoneRef          = i.ZoneRef,
            U_ReplitId       = i.U_ReplitId,
            DeliveryLocation = i.DeliveryLocation
        }).ToList();

        foreach (var invoice in invoices)
        {
            var legacyStatus = ComputeLegacyStatusDisplay(invoice.Status ?? "", invoice.Canceled ?? "");
            if (!lifecycleResults.TryGetValue(invoice.DocEntry, out var lifecycle))
                continue;

            if (!string.Equals(legacyStatus, lifecycle.DocStatusDisplay, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[InvoiceCache] Canonical lifecycle override for invoice DocEntry={DocEntry}, DocNum={DocNum}: {LegacyStatus} -> {CanonicalStatus}. PaidToDate={PaidToDate}, PaymentSum={PaymentSum}, CreditMemoCount={CreditMemoCount}, Replacement={HasReplacement}",
                    invoice.DocEntry,
                    invoice.DocNum,
                    legacyStatus,
                    lifecycle.DocStatusDisplay,
                    invoice.PaidToDate,
                    lifecycle.AppliedPaymentSum,
                    lifecycle.CreditMemoCount,
                    lifecycle.HasReplacement);
            }
        }

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
     ""DaysOverdue"", ""SalesEmployeeCode"", ""SalesEmployeeName"", ""GroupNum"",
     ""ZoneRef"", ""U_ReplitId"", ""DeliveryLocation"")
VALUES
    ($DocEntry, $DocNum, $InvoiceDocNum, $DocDate, $DocStatus, $Canceled,
     $DocStatusDisplay, $CardCode, $CardName, $DocTotal, $PaidToDate, $BalanceDue,
     $DaysOverdue, $SalesEmployeeCode, $SalesEmployeeName, $GroupNum,
     $ZoneRef, $U_ReplitId, $DeliveryLocation)
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
    ""GroupNum"" = excluded.""GroupNum"",
    ""ZoneRef"" = excluded.""ZoneRef"",
    ""U_ReplitId"" = excluded.""U_ReplitId"",
    ""DeliveryLocation"" = excluded.""DeliveryLocation"";";

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
                var pZoneRef = cmd.Parameters.Add("$ZoneRef", SqliteType.Text);
                var pU_ReplitId = cmd.Parameters.Add("$U_ReplitId", SqliteType.Text);
                var pDeliveryLocation = cmd.Parameters.Add("$DeliveryLocation", SqliteType.Text);

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
                    pZoneRef.Value = (object?)h.ZoneRef ?? DBNull.Value;
                    pU_ReplitId.Value = (object?)h.U_ReplitId ?? DBNull.Value;
                    pDeliveryLocation.Value = (object?)h.DeliveryLocation ?? DBNull.Value;

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
    ""SalesEmployeeCode"", ""SalesEmployeeName"",
    ""ClientReference"", ""Canceled"", ""CounterRef"", ""LastUpdated""
)
VALUES
(
    $DocEntry, $InvoiceDocNum, $PaymentDocEntry, $PaymentNumber,
    $PaymentDate, $CardCode, $CardName, $AmountApplied,
    $BankTransferAmount, $BankTransferReference,
    $DebitAccountCode, $DebitAccountName,
    $SalesEmployeeCode, $SalesEmployeeName,
    $ClientReference, $Canceled, $CounterRef, $LastUpdated
)
ON CONFLICT(""DocEntry"", ""PaymentDocEntry"") DO UPDATE SET
    ""InvoiceDocNum""         = excluded.""InvoiceDocNum"",
    ""PaymentNumber""         = excluded.""PaymentNumber"",
    ""PaymentDate""           = excluded.""PaymentDate"",
    ""CardCode""              = excluded.""CardCode"",
    ""CardName""              = excluded.""CardName"",
    ""AmountApplied""         = excluded.""AmountApplied"",
    ""BankTransferAmount""    = excluded.""BankTransferAmount"",
    ""BankTransferReference"" = excluded.""BankTransferReference"",
    ""DebitAccountCode""      = excluded.""DebitAccountCode"",
    ""DebitAccountName""      = excluded.""DebitAccountName"",
    ""SalesEmployeeCode""     = excluded.""SalesEmployeeCode"",
    ""SalesEmployeeName""     = excluded.""SalesEmployeeName"",
    ""ClientReference""       = excluded.""ClientReference"",
    ""Canceled""              = excluded.""Canceled"",
    ""CounterRef""            = excluded.""CounterRef"",
    ""LastUpdated""           = excluded.""LastUpdated"";
";

                var pDocEntry             = cmd.Parameters.Add("$DocEntry",             SqliteType.Integer);
                var pInvoiceDocNum        = cmd.Parameters.Add("$InvoiceDocNum",        SqliteType.Integer);
                var pPaymentDocEntry      = cmd.Parameters.Add("$PaymentDocEntry",      SqliteType.Integer);
                var pPaymentNumber        = cmd.Parameters.Add("$PaymentNumber",        SqliteType.Integer);
                var pPaymentDate          = cmd.Parameters.Add("$PaymentDate",          SqliteType.Text);
                var pCardCode             = cmd.Parameters.Add("$CardCode",             SqliteType.Text);
                var pCardName             = cmd.Parameters.Add("$CardName",             SqliteType.Text);
                var pAmountApplied        = cmd.Parameters.Add("$AmountApplied",        SqliteType.Real);
                var pBankTransferAmount   = cmd.Parameters.Add("$BankTransferAmount",   SqliteType.Real);
                var pBankTransferReference= cmd.Parameters.Add("$BankTransferReference",SqliteType.Text);
                var pDebitAccountCode     = cmd.Parameters.Add("$DebitAccountCode",     SqliteType.Text);
                var pDebitAccountName     = cmd.Parameters.Add("$DebitAccountName",     SqliteType.Text);
                var pSalesEmployeeCode    = cmd.Parameters.Add("$SalesEmployeeCode",    SqliteType.Text);
                var pSalesEmployeeName    = cmd.Parameters.Add("$SalesEmployeeName",    SqliteType.Text);
                var pClientReference      = cmd.Parameters.Add("$ClientReference",      SqliteType.Text);
                var pCanceled             = cmd.Parameters.Add("$Canceled",             SqliteType.Integer);
                var pCounterRef           = cmd.Parameters.Add("$CounterRef",           SqliteType.Text);
                var pLastUpdated          = cmd.Parameters.Add("$LastUpdated",          SqliteType.Text);

                foreach (var p in payments)
                {
                    pDocEntry.Value              = p.DocEntry;
                    pInvoiceDocNum.Value          = p.InvoiceDocNum;
                    pPaymentDocEntry.Value        = p.PaymentDocEntry;
                    pPaymentNumber.Value          = p.PaymentNumber;
                    pPaymentDate.Value            = p.PaymentDate.ToString("yyyy-MM-dd");
                    pCardCode.Value              = p.CardCode ?? "";
                    pCardName.Value              = p.CardName ?? "";
                    pAmountApplied.Value          = p.AmountApplied;
                    pBankTransferAmount.Value     = p.BankTransferAmount;
                    pBankTransferReference.Value  = p.BankTransferReference ?? "";
                    pDebitAccountCode.Value       = p.DebitAccountCode ?? "";
                    pDebitAccountName.Value       = p.DebitAccountName ?? "";
                    pSalesEmployeeCode.Value      = p.SalesEmployeeCode ?? "";
                    pSalesEmployeeName.Value      = p.SalesEmployeeName ?? "";
                    pClientReference.Value        = p.ClientReference ?? "";
                    pCanceled.Value              = p.Canceled ? 1 : 0;
                    pCounterRef.Value            = p.CounterRef ?? "";
                    pLastUpdated.Value            = p.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
        }

        return payments.Count;
    }

    #endregion

    // ─── Event-driven targeted writes (OutboxPoller) ──────────────────────────
    // One invoice or one payment's worth of data, in a single SQLite transaction.
    // These methods MUST NOT touch SyncMetadata — those keys are polling cursors
    // owned by InvoiceDeltaSyncJob and NeonSyncJob exclusively.

    /// <summary>
    /// UPSERT a single invoice header + replace its lines — all in one SQLite tx.
    /// The caller (InvoiceEventHandler / CreditMemoEventHandler) is responsible for
    /// mapping InvoiceDto + lifecycle status to CachedInvoice + CachedInvoiceLine.
    /// </summary>
    public async Task UpsertSingleInvoiceAsync(
        CachedInvoice header,
        IEnumerable<CachedInvoiceLine> lines,
        CancellationToken ct = default)
    {
        var lineList = lines.ToList();
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        using var tx       = await _db.Database.BeginTransactionAsync(ct);
        var sqliteConn     = (SqliteConnection)connection;
        var sqliteTx       = (SqliteTransaction)tx.GetDbTransaction();

        // 1) UPSERT header (19 columns, ON CONFLICT on DocEntry)
        using (var cmd = sqliteConn.CreateCommand())
        {
            cmd.Transaction  = sqliteTx;
            cmd.CommandText  = @"
INSERT INTO ""Invoices""
    (""DocEntry"", ""DocNum"", ""InvoiceDocNum"", ""DocDate"", ""DocStatus"", ""Canceled"",
     ""DocStatusDisplay"", ""CardCode"", ""CardName"", ""DocTotal"", ""PaidToDate"", ""BalanceDue"",
     ""DaysOverdue"", ""SalesEmployeeCode"", ""SalesEmployeeName"", ""GroupNum"",
     ""ZoneRef"", ""U_ReplitId"", ""DeliveryLocation"")
VALUES
    ($DocEntry, $DocNum, $InvoiceDocNum, $DocDate, $DocStatus, $Canceled,
     $DocStatusDisplay, $CardCode, $CardName, $DocTotal, $PaidToDate, $BalanceDue,
     $DaysOverdue, $SalesEmployeeCode, $SalesEmployeeName, $GroupNum,
     $ZoneRef, $U_ReplitId, $DeliveryLocation)
ON CONFLICT(""DocEntry"") DO UPDATE SET
    ""DocNum""            = excluded.""DocNum"",
    ""InvoiceDocNum""     = excluded.""InvoiceDocNum"",
    ""DocDate""           = excluded.""DocDate"",
    ""DocStatus""         = excluded.""DocStatus"",
    ""Canceled""          = excluded.""Canceled"",
    ""DocStatusDisplay""  = excluded.""DocStatusDisplay"",
    ""CardCode""          = excluded.""CardCode"",
    ""CardName""          = excluded.""CardName"",
    ""DocTotal""          = excluded.""DocTotal"",
    ""PaidToDate""        = excluded.""PaidToDate"",
    ""BalanceDue""        = excluded.""BalanceDue"",
    ""DaysOverdue""       = excluded.""DaysOverdue"",
    ""SalesEmployeeCode"" = excluded.""SalesEmployeeCode"",
    ""SalesEmployeeName"" = excluded.""SalesEmployeeName"",
    ""GroupNum""          = excluded.""GroupNum"",
    ""ZoneRef""           = excluded.""ZoneRef"",
    ""U_ReplitId""        = excluded.""U_ReplitId"",
    ""DeliveryLocation""  = excluded.""DeliveryLocation"";";

            cmd.Parameters.AddWithValue("$DocEntry",          header.DocEntry);
            cmd.Parameters.AddWithValue("$DocNum",            header.DocNum);
            cmd.Parameters.AddWithValue("$InvoiceDocNum",     header.InvoiceDocNum);
            cmd.Parameters.AddWithValue("$DocDate",           header.DocDate.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$DocStatus",         header.DocStatus ?? "");
            cmd.Parameters.AddWithValue("$Canceled",          header.Canceled ?? "");
            cmd.Parameters.AddWithValue("$DocStatusDisplay",  header.DocStatusDisplay ?? "");
            cmd.Parameters.AddWithValue("$CardCode",          header.CardCode ?? "");
            cmd.Parameters.AddWithValue("$CardName",          header.CardName ?? "");
            cmd.Parameters.AddWithValue("$DocTotal",          (double)header.DocTotal);
            cmd.Parameters.AddWithValue("$PaidToDate",        (double)header.PaidToDate);
            cmd.Parameters.AddWithValue("$BalanceDue",        (double)header.BalanceDue);
            cmd.Parameters.AddWithValue("$DaysOverdue",       header.DaysOverdue);
            cmd.Parameters.AddWithValue("$SalesEmployeeCode", header.SalesEmployeeCode);
            cmd.Parameters.AddWithValue("$SalesEmployeeName", header.SalesEmployeeName ?? "");
            cmd.Parameters.AddWithValue("$GroupNum",          header.GroupNum);
            cmd.Parameters.AddWithValue("$ZoneRef",           (object?)header.ZoneRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$U_ReplitId",        (object?)header.U_ReplitId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$DeliveryLocation",  (object?)header.DeliveryLocation ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2) Replace lines: DELETE existing then INSERT current
        using (var delCmd = sqliteConn.CreateCommand())
        {
            delCmd.Transaction  = sqliteTx;
            delCmd.CommandText  = @"DELETE FROM ""InvoiceLines"" WHERE ""DocEntry"" = $DocEntry";
            delCmd.Parameters.AddWithValue("$DocEntry", header.DocEntry);
            await delCmd.ExecuteNonQueryAsync(ct);
        }

        if (lineList.Count > 0)
        {
            using var insCmd = sqliteConn.CreateCommand();
            insCmd.Transaction = sqliteTx;
            insCmd.CommandText = @"
INSERT INTO ""InvoiceLines""
    (""DocEntry"", ""LineNum"", ""ItemCode"", ""Dscription"",
     ""Quantity"", ""Price"", ""LineTotal"", ""U_Item_Name"", ""U_MDLTsT"", ""U_MdlTEST"")
VALUES
    ($DocEntry, $LineNum, $ItemCode, $Dscription,
     $Quantity, $Price, $LineTotal, $U_Item_Name, $U_MDLTsT, $U_MdlTEST)";

            var pDocEntry   = insCmd.Parameters.Add("$DocEntry",   SqliteType.Integer);
            var pLineNum    = insCmd.Parameters.Add("$LineNum",    SqliteType.Integer);
            var pItemCode   = insCmd.Parameters.Add("$ItemCode",   SqliteType.Text);
            var pDscription = insCmd.Parameters.Add("$Dscription", SqliteType.Text);
            var pQuantity   = insCmd.Parameters.Add("$Quantity",   SqliteType.Real);
            var pPrice      = insCmd.Parameters.Add("$Price",      SqliteType.Real);
            var pLineTotal  = insCmd.Parameters.Add("$LineTotal",  SqliteType.Real);
            var pUItemName  = insCmd.Parameters.Add("$U_Item_Name",SqliteType.Text);
            var pUMDLTsT    = insCmd.Parameters.Add("$U_MDLTsT",   SqliteType.Text);
            var pUMdlTEST   = insCmd.Parameters.Add("$U_MdlTEST",  SqliteType.Text);

            foreach (var l in lineList)
            {
                pDocEntry.Value   = l.DocEntry;
                pLineNum.Value    = l.LineNum;
                pItemCode.Value   = l.ItemCode ?? "";
                pDscription.Value = l.Dscription ?? "";
                pQuantity.Value   = (double)l.Quantity;
                pPrice.Value      = (double)l.Price;
                pLineTotal.Value  = (double)l.LineTotal;
                pUItemName.Value  = l.U_Item_Name ?? "";
                pUMDLTsT.Value    = l.U_MDLTsT ?? "";
                pUMdlTEST.Value   = l.U_MdlTEST ?? "";
                await insCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Reads the invoice DocEntries currently cached for a given paymentDocEntry.
    /// Must be called BEFORE ReconcilePaymentsForPaymentDocEntryAsync so the handler
    /// has a pre-deletion snapshot to compute the full set of affected invoices.
    /// </summary>
    public Task<List<int>> GetExistingInvoiceDocEntriesForPaymentAsync(int paymentDocEntry)
    {
        return _db.InvoicePayments
            .Where(p => p.PaymentDocEntry == paymentDocEntry)
            .Select(p => p.DocEntry)
            .ToListAsync();
    }

    /// <summary>
    /// For a single paymentDocEntry: UPSERT all rows in currentPayments, then DELETE
    /// any stale rows no longer present in SAP. Both steps run in one SQLite tx.
    /// Empty-set safety: if currentPayments is empty, ALL rows for this paymentDocEntry
    /// are deleted (the payment was voided / zero-RCT2 — caller should already have
    /// handled the zero-RCT2 early-exit; this is a defensive clean-up path).
    /// </summary>
    public async Task ReconcilePaymentsForPaymentDocEntryAsync(
        int paymentDocEntry,
        IEnumerable<CachedInvoicePayment> currentPayments,
        CancellationToken ct = default)
    {
        var current    = currentPayments.ToList();
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        using var tx   = await _db.Database.BeginTransactionAsync(ct);
        var sqliteConn = (SqliteConnection)connection;
        var sqliteTx   = (SqliteTransaction)tx.GetDbTransaction();

        // 1) UPSERT current rows
        if (current.Count > 0)
        {
            using var upsertCmd = sqliteConn.CreateCommand();
            upsertCmd.Transaction = sqliteTx;
            upsertCmd.CommandText = @"
INSERT INTO ""InvoicePayments""
(
    ""DocEntry"", ""InvoiceDocNum"", ""PaymentDocEntry"", ""PaymentNumber"",
    ""PaymentDate"", ""CardCode"", ""CardName"", ""AmountApplied"",
    ""BankTransferAmount"", ""BankTransferReference"",
    ""DebitAccountCode"", ""DebitAccountName"",
    ""SalesEmployeeCode"", ""SalesEmployeeName"",
    ""ClientReference"", ""Canceled"", ""CounterRef"", ""LastUpdated""
)
VALUES
(
    $DocEntry, $InvoiceDocNum, $PaymentDocEntry, $PaymentNumber,
    $PaymentDate, $CardCode, $CardName, $AmountApplied,
    $BankTransferAmount, $BankTransferReference,
    $DebitAccountCode, $DebitAccountName,
    $SalesEmployeeCode, $SalesEmployeeName,
    $ClientReference, $Canceled, $CounterRef, $LastUpdated
)
ON CONFLICT(""DocEntry"", ""PaymentDocEntry"") DO UPDATE SET
    ""InvoiceDocNum""         = excluded.""InvoiceDocNum"",
    ""PaymentNumber""         = excluded.""PaymentNumber"",
    ""PaymentDate""           = excluded.""PaymentDate"",
    ""CardCode""              = excluded.""CardCode"",
    ""CardName""              = excluded.""CardName"",
    ""AmountApplied""         = excluded.""AmountApplied"",
    ""BankTransferAmount""    = excluded.""BankTransferAmount"",
    ""BankTransferReference"" = excluded.""BankTransferReference"",
    ""DebitAccountCode""      = excluded.""DebitAccountCode"",
    ""DebitAccountName""      = excluded.""DebitAccountName"",
    ""SalesEmployeeCode""     = excluded.""SalesEmployeeCode"",
    ""SalesEmployeeName""     = excluded.""SalesEmployeeName"",
    ""ClientReference""       = excluded.""ClientReference"",
    ""Canceled""              = excluded.""Canceled"",
    ""CounterRef""            = excluded.""CounterRef"",
    ""LastUpdated""           = excluded.""LastUpdated"";";

            var pDocEntry             = upsertCmd.Parameters.Add("$DocEntry",             SqliteType.Integer);
            var pInvoiceDocNum        = upsertCmd.Parameters.Add("$InvoiceDocNum",        SqliteType.Integer);
            var pPaymentDocEntry      = upsertCmd.Parameters.Add("$PaymentDocEntry",      SqliteType.Integer);
            var pPaymentNumber        = upsertCmd.Parameters.Add("$PaymentNumber",        SqliteType.Integer);
            var pPaymentDate          = upsertCmd.Parameters.Add("$PaymentDate",          SqliteType.Text);
            var pCardCode             = upsertCmd.Parameters.Add("$CardCode",             SqliteType.Text);
            var pCardName             = upsertCmd.Parameters.Add("$CardName",             SqliteType.Text);
            var pAmountApplied        = upsertCmd.Parameters.Add("$AmountApplied",        SqliteType.Real);
            var pBankTransferAmount   = upsertCmd.Parameters.Add("$BankTransferAmount",   SqliteType.Real);
            var pBankTransferReference= upsertCmd.Parameters.Add("$BankTransferReference",SqliteType.Text);
            var pDebitAccountCode     = upsertCmd.Parameters.Add("$DebitAccountCode",     SqliteType.Text);
            var pDebitAccountName     = upsertCmd.Parameters.Add("$DebitAccountName",     SqliteType.Text);
            var pSalesEmployeeCode    = upsertCmd.Parameters.Add("$SalesEmployeeCode",    SqliteType.Text);
            var pSalesEmployeeName    = upsertCmd.Parameters.Add("$SalesEmployeeName",    SqliteType.Text);
            var pClientReference      = upsertCmd.Parameters.Add("$ClientReference",      SqliteType.Text);
            var pCanceled             = upsertCmd.Parameters.Add("$Canceled",             SqliteType.Integer);
            var pCounterRef           = upsertCmd.Parameters.Add("$CounterRef",           SqliteType.Text);
            var pLastUpdated          = upsertCmd.Parameters.Add("$LastUpdated",          SqliteType.Text);

            foreach (var p in current)
            {
                pDocEntry.Value              = p.DocEntry;
                pInvoiceDocNum.Value         = p.InvoiceDocNum;
                pPaymentDocEntry.Value       = p.PaymentDocEntry;
                pPaymentNumber.Value         = p.PaymentNumber;
                pPaymentDate.Value           = p.PaymentDate.ToString("yyyy-MM-dd");
                pCardCode.Value              = p.CardCode ?? "";
                pCardName.Value              = p.CardName ?? "";
                pAmountApplied.Value         = (double)p.AmountApplied;
                pBankTransferAmount.Value    = (double)p.BankTransferAmount;
                pBankTransferReference.Value = p.BankTransferReference ?? "";
                pDebitAccountCode.Value      = p.DebitAccountCode ?? "";
                pDebitAccountName.Value      = p.DebitAccountName ?? "";
                pSalesEmployeeCode.Value     = p.SalesEmployeeCode ?? "";
                pSalesEmployeeName.Value     = p.SalesEmployeeName ?? "";
                pClientReference.Value       = p.ClientReference ?? "";
                pCanceled.Value              = p.Canceled ? 1 : 0;
                pCounterRef.Value            = p.CounterRef ?? "";
                pLastUpdated.Value           = p.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss");
                await upsertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        // 2) DELETE stale rows
        using (var delCmd = sqliteConn.CreateCommand())
        {
            delCmd.Transaction = sqliteTx;

            if (current.Count == 0)
            {
                // Zero-RCT2 safety branch: remove all cached rows for this payment
                delCmd.CommandText = @"DELETE FROM ""InvoicePayments"" WHERE ""PaymentDocEntry"" = $PaymentDocEntry";
                delCmd.Parameters.AddWithValue("$PaymentDocEntry", paymentDocEntry);
            }
            else
            {
                var currentDocEntries = current.Select(p => p.DocEntry).Distinct().ToList();
                var inClause          = string.Join(",", currentDocEntries);
                delCmd.CommandText    = $@"
DELETE FROM ""InvoicePayments""
WHERE ""PaymentDocEntry"" = $PaymentDocEntry
  AND ""DocEntry"" NOT IN ({inClause})";
                delCmd.Parameters.AddWithValue("$PaymentDocEntry", paymentDocEntry);
            }

            await delCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
