using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Controllers;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Returns;
using Xunit;

namespace SapReplitAPI.Tests.Returns;

public sealed class InvoiceReturnsContractTests : IDisposable
{
    private readonly SqliteConnection db = new("Data Source=:memory:");

    public InvoiceReturnsContractTests()
    {
        db.Open();
        Exec("""
            CREATE TABLE OINV (DocEntry INTEGER, DocNum INTEGER, DocDate TEXT, CardCode TEXT, CardName TEXT,
                DocStatus TEXT, CANCELED TEXT, DocTotal NUMERIC, PaidToDate NUMERIC);
            CREATE TABLE INV1 (DocEntry INTEGER, LineNum INTEGER, ItemCode TEXT, Dscription TEXT,
                Quantity NUMERIC, Price NUMERIC, WhsCode TEXT, OpenQty NUMERIC);
            CREATE TABLE ORIN (DocEntry INTEGER, CANCELED TEXT, DocTotal NUMERIC);
            CREATE TABLE RIN1 (DocEntry INTEGER, LineNum INTEGER, BaseType INTEGER, BaseEntry INTEGER, BaseLine INTEGER, Quantity NUMERIC);
            CREATE TABLE ORRR (DocEntry INTEGER, CANCELED TEXT, DocStatus TEXT);
            CREATE TABLE RRR1 (DocEntry INTEGER, LineNum INTEGER, BaseType INTEGER, BaseEntry INTEGER, BaseLine INTEGER,
                Quantity NUMERIC, OpenQty NUMERIC, LineStatus TEXT);
            CREATE TABLE InvoiceLines (DocEntry INTEGER, LineNum INTEGER, ReturnedQty NUMERIC NOT NULL DEFAULT 0);
            CREATE TABLE CreditMemoHeaders (DocEntry INTEGER, Canceled TEXT);
            CREATE TABLE CreditMemoLines (DocEntry INTEGER, InvoiceDocEntry INTEGER, InvoiceLineNum INTEGER, BaseType INTEGER, Quantity NUMERIC);
            CREATE TABLE InvoicePayments (DocEntry INTEGER, Amount NUMERIC);
            INSERT INTO OINV VALUES (28571,28571,'2026-09-01','CUS001181','Customer','C','N',105000,105000);
            INSERT INTO INV1 VALUES (28571,0,'ITEM','Part',3,35000,'001',0);
            INSERT INTO InvoiceLines VALUES (28571,0,0);
            INSERT INTO InvoicePayments VALUES (28571,105000);
            """);
    }

    private void Exec(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private decimal Scalar(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToDecimal(cmd.ExecuteScalar()); }
    private void Request(int id = 46, int qty = 3, int open = 3)
        => Exec($"INSERT INTO ORRR VALUES ({id},'N','O'); INSERT INTO RRR1 VALUES ({id},0,13,28571,0,{qty},{open},'O');");
    private void Credit(int id = 81, int qty = 1, int path = 13, string canceled = "N", int request = 46)
    {
        var parent = path == 13 ? 28571 : request;
        Exec($"INSERT INTO ORIN VALUES ({id},'{canceled}',{qty * 35000}); INSERT INTO RIN1 VALUES ({id},0,{path},{parent},0,{qty});");
        Exec($"INSERT INTO CreditMemoHeaders VALUES ({id},'{canceled}'); INSERT INTO CreditMemoLines VALUES ({id},28571,0,{path},{qty});");
    }
    private InvoiceReturnsLineDto Read()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = InvoiceReturnsSql.Quantities;
        using var reader = cmd.ExecuteReader(); Assert.True(reader.Read());
        return new() { InvoiceDocEntry = 28571, InvoiceLineNum = 0,
            InvoicedQty = Convert.ToDecimal(reader["Quantity"]), ReturnedQty = Convert.ToDecimal(reader["ReturnedQty"]),
            PendingReturnQty = Convert.ToDecimal(reader["PendingQty"]) };
    }
    private async Task PreserveReplacement()
    {
        await using var tx = await db.BeginTransactionAsync();
        var lines = new[] { new CachedInvoiceLine { DocEntry = 28571, LineNum = 0 } };
        await ReturnedQuantityMirror.PreserveAsync(db, tx, lines);
        using var cmd = db.CreateCommand(); cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "DELETE FROM InvoiceLines; INSERT INTO InvoiceLines VALUES (28571,0,$qty)";
        cmd.Parameters.AddWithValue("$qty", lines[0].ReturnedQty); await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
    private decimal Mirrored => Scalar("SELECT ReturnedQty FROM InvoiceLines WHERE DocEntry=28571 AND LineNum=0");

    [Fact] public async Task RQ01_ReconcilePreservesHistoricalQuantity()
    { Exec("UPDATE InvoiceLines SET ReturnedQty=3"); await PreserveReplacement(); Assert.Equal(3, Mirrored); }
    [Fact] public async Task RQ02_ReconcileCanceledCreditExcluded()
    { Credit(canceled: "Y"); await ReturnedQtyBackfillService.RecomputeAsync(db); await PreserveReplacement(); Assert.Equal(0, Mirrored); }
    [Fact] public async Task RQ03_ReconcilePathAIncluded()
    { Credit(qty: 3); await ReturnedQtyBackfillService.RecomputeAsync(db); await PreserveReplacement(); Assert.Equal(3, Mirrored); }
    [Fact] public async Task RQ04_ReconcilePathBIncluded()
    { Request(); Credit(qty: 3, path: 234000031); await ReturnedQtyBackfillService.RecomputeAsync(db); await PreserveReplacement(); Assert.Equal(3, Mirrored); }
    [Fact] public async Task RQ05_ReconcileWithoutCreditIsZero()
    { await ReturnedQtyBackfillService.RecomputeAsync(db); await PreserveReplacement(); Assert.Equal(0, Mirrored); }
    [Fact] public async Task RQ06_HistoricalPathABackfill()
    { Credit(qty: 3); var r = await ReturnedQtyBackfillService.RecomputeAsync(db); Assert.Equal(3, r.PathAQty); Assert.Equal(1, r.InvoiceLinesChanged); Assert.Equal(Read().ReturnedQty, Mirrored); }
    [Fact] public async Task RQ07_HistoricalPathBBackfill()
    { Request(open: 0); Credit(qty: 3, path: 234000031); var r = await ReturnedQtyBackfillService.RecomputeAsync(db); Assert.Equal(3, r.PathBQty); Assert.Equal(Read().ReturnedQty, Mirrored); }
    [Fact] public void RQ08_CanceledAndCancellationDocumentsExcluded()
    { Credit(canceled: "Y"); Credit(id: 82, canceled: "C"); Assert.Equal(0, Read().ReturnedQty); }
    [Fact] public void RQ09_OpenRequestClaimsQuantity()
    { Request(); var l = Read(); Assert.Equal(3, l.PendingReturnQty); Assert.Equal(0, l.ReturnableQty); }
    [Fact] public void RQ10_PartialCreditMovesPendingToReturned()
    { Request(open: 2); Credit(path: 234000031); var l = Read(); Assert.Equal(1, l.ReturnedQty); Assert.Equal(2, l.PendingReturnQty); Assert.Equal(0, l.ReturnableQty); }
    [Fact] public void RQ11_FinalCreditHasZeroReturnable()
    { Request(open: 0); Credit(qty: 3, path: 234000031); Exec("UPDATE ORRR SET DocStatus='C'; UPDATE RRR1 SET LineStatus='C'"); var l = Read(); Assert.Equal(3, l.ReturnedQty); Assert.Equal(0, l.PendingReturnQty); Assert.Equal(0, l.ReturnableQty); }
    [Fact] public async Task RQ12_RepeatedBackfillIsIdempotent()
    { Credit(qty: 3); var a = await ReturnedQtyBackfillService.RecomputeAsync(db); var b = await ReturnedQtyBackfillService.RecomputeAsync(db); Assert.Equal(1, a.InvoiceLinesChanged); Assert.Equal(0, b.InvoiceLinesChanged); Assert.Equal(1, b.InvoiceLinesScanned); Assert.Equal(3, Mirrored); }
    [Fact] public void RQ13_ExactSnakeCaseContract()
    {
        var dto = new InvoiceReturnsDto { Lines = new() { Read() } };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        Assert.Equal(new[] { "balance_due", "canceled", "card_code", "card_name", "credit_memo_count", "credit_memo_total", "doc_date", "doc_entry", "doc_num", "doc_status", "doc_total", "lines", "outstanding_amount", "paid_to_date" }, doc.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(new[] { "description", "invoice_doc_entry", "invoice_line_num", "invoiced_qty", "item_code", "pending_return_qty", "returnable_qty", "returned_qty", "unit_price", "whs_code" }, doc.RootElement.GetProperty("lines")[0].EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }
    [Fact] public void RQ14_NoReturnsUsesOriginalQuantityEvenIfInvoiceClosed()
    { var l = Read(); Assert.Equal(3, l.InvoicedQty); Assert.Equal(3, l.ReturnableQty); Assert.Equal(0, l.ReturnedQty); Assert.Equal(0, l.PendingReturnQty); }
    [Fact] public void RQ15_MultipleRequestsSameLine()
    { Request(qty: 1, open: 1); Request(id: 47, qty: 1, open: 1); Assert.Equal(2, Read().PendingReturnQty); Assert.Equal(1, Read().ReturnableQty); }
    [Fact] public void RQ16_MultipleCreditsSameLine()
    { Credit(); Credit(id: 82); Credit(id: 83); Assert.Equal(3, Read().ReturnedQty); Assert.Equal(0, Read().ReturnableQty); }
    [Fact] public void RQ17_MixedPathsDoNotMultiplyRows()
    { Request(open: 1); Credit(); Credit(id: 82, path: 234000031); Assert.Equal(2, Read().ReturnedQty); Assert.Equal(1, Read().PendingReturnQty); }
    [Fact] public void RQ18_CreditTotalsDeduplicateLinesAndPaths()
    {
        Request(open: 0); Credit(); Credit(id: 82, path: 234000031); Credit(id: 83, canceled: "Y");
        Exec("INSERT INTO RIN1 VALUES (81,1,234000031,46,0,1)");
        using var cmd = db.CreateCommand(); cmd.CommandText = InvoiceReturnsSql.CreditMemoEvidence("28571");
        using var reader = cmd.ExecuteReader(); Assert.True(reader.Read());
        var dto = new InvoiceReturnsDto(); dto.ApplyLifecycle(new() { CreditMemoCount = Convert.ToInt32(reader["CreditMemoCount"]), CreditMemoTotal = Convert.ToDecimal(reader["CreditMemoTotal"]) });
        Assert.Equal(2, dto.CreditMemoCount); Assert.Equal(70000, dto.CreditMemoTotal);
    }
    [Fact] public void RQ19_OutstandingUsesExistingLifecycleSemantics()
    {
        var lifecycle = new InvoiceLifecycleStatusService(NullLogger<InvoiceLifecycleStatusService>.Instance);
        var result = lifecycle.ComputeStatus(new InvoiceLifecycleEvidence { DocTotal = 105000, PaidToDate = 35000, AppliedPaymentSum = 50000, BalanceDue = 70000, CreditMemoTotal = 35000 });
        var dto = new InvoiceReturnsDto(); dto.ApplyLifecycle(result); Assert.Equal(70000, dto.OutstandingAmount);
    }
    [Fact] public async Task RQ20_ReadAndBackfillNeverMutatePayments()
    { Credit(); _ = Read(); await ReturnedQtyBackfillService.RecomputeAsync(db); Assert.Equal(105000, Scalar("SELECT Amount FROM InvoicePayments")); }

    [Fact] public void ClosedOrCanceledRequestsDoNotClaimQuantity()
    { Request(); Exec("UPDATE RRR1 SET LineStatus='C'"); Assert.Equal(0, Read().PendingReturnQty); Exec("UPDATE RRR1 SET LineStatus='O'; UPDATE ORRR SET CANCELED='Y'"); Assert.Equal(0, Read().PendingReturnQty); }
    [Fact] public void PathBMatchesExactRequestLine()
    { Request(); Credit(path: 234000031); Exec("INSERT INTO RRR1 VALUES (46,1,13,999,0,9,9,'O')"); Assert.Equal(1, Read().ReturnedQty); }
    [Fact] public void ReturnableClampedWhenClaimsExceedInvoice()
    { Request(); Credit(qty: 3); Assert.Equal(0, Read().ReturnableQty); }
    [Fact] public void InvalidApiFiltersRejectedBeforeSapRead()
    {
        var controller = new ReturnsController(null!, NullLogger<ReturnsController>.Instance);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(controller.ListInvoices(null));
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(controller.ListInvoices("CUS001181", "closed"));
    }
    [Fact] public void SapUnavailableReturns503()
    { var c = new ReturnsController(null!, NullLogger<ReturnsController>.Instance); Assert.Equal(503, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(c.ListInvoices("CUS001181")).StatusCode); }

    [Fact] public async Task SqliteInvoiceEventRefreshPreservesReturnedQty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var context = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        context.Invoices.Add(new CachedInvoice { DocEntry = 28571 });
        context.InvoiceLines.Add(new() { DocEntry = 28571, LineNum = 0, ReturnedQty = 3 }); await context.SaveChangesAsync();
        var service = new InvoiceCacheService(context, null!, NullLogger<InvoiceCacheService>.Instance);
        await service.UpsertSingleInvoiceAsync(new CachedInvoice { DocEntry = 28571 }, new[] { new CachedInvoiceLine { DocEntry = 28571, LineNum = 0, Quantity = 3 } });
        context.ChangeTracker.Clear(); Assert.Equal(3, (await context.InvoiceLines.SingleAsync()).ReturnedQty);
    }
    public void Dispose() => db.Dispose();
}
