using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Cache;
using Xunit;

namespace SapReplitAPI.Tests.Invoice;

/// <summary>
/// SQ01–SQ10: SQLite quantity preservation — proves that UpsertSingleInvoiceAsync
/// preserves ReturnedQty and PendingReturnQty via ReturnedQuantityMirror.PreserveAsync
/// and PendingReturnQuantityMirror.PreserveAsync, so that invoice 13/A event refresh
/// and 15-minute drift repair do not erase mirror-derived return state.
///
/// No SAP COM calls. Uses SQLite in-memory.
/// No SAP mutations (0 OINV/ODLN/ORDR created).
/// </summary>
public sealed class InvoiceSqliteQuantityTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly CacheDbContext   _db;
    private readonly InvoiceCacheService _svc;

    public InvoiceSqliteQuantityTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var opts = new DbContextOptionsBuilder<CacheDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new CacheDbContext(opts);
        _db.Database.EnsureCreated();

        // SapService is not used by UpsertSingleInvoiceAsync; null is safe for these tests.
        _svc = new InvoiceCacheService(_db, null!, NullLogger<InvoiceCacheService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ01–SQ03: single-line preservation
    // ─────────────────────────────────────────────────────────────────────────

    // SQ01 — existing ReturnedQty=3 survives a targeted invoice refresh
    [Fact]
    public async Task SQ01_ReturnedQty_Preserved_On_Refresh()
    {
        SeedLine(docEntry: 1001, lineNum: 0, returnedQty: 3m, pendingReturnQty: 0m);

        await _svc.UpsertSingleInvoiceAsync(MakeHeader(1001), new[] { MakeLine(1001, 0) });

        Assert.Equal(3m, ReadReturnedQty(1001, 0));
    }

    // SQ02 — existing PendingReturnQty=2 survives a targeted invoice refresh
    [Fact]
    public async Task SQ02_PendingReturnQty_Preserved_On_Refresh()
    {
        SeedLine(docEntry: 1002, lineNum: 0, returnedQty: 0m, pendingReturnQty: 2m);

        await _svc.UpsertSingleInvoiceAsync(MakeHeader(1002), new[] { MakeLine(1002, 0) });

        Assert.Equal(2m, ReadPendingReturnQty(1002, 0));
    }

    // SQ03 — both ReturnedQty and PendingReturnQty preserved simultaneously
    [Fact]
    public async Task SQ03_BothQuantities_Preserved_On_Refresh()
    {
        SeedLine(docEntry: 1003, lineNum: 0, returnedQty: 5m, pendingReturnQty: 3m);

        await _svc.UpsertSingleInvoiceAsync(MakeHeader(1003), new[] { MakeLine(1003, 0) });

        Assert.Equal(5m, ReadReturnedQty(1003, 0));
        Assert.Equal(3m, ReadPendingReturnQty(1003, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ04: new invoice (no prior SQLite state)
    // ─────────────────────────────────────────────────────────────────────────

    // SQ04 — new invoice with no existing SQLite line defaults to zero
    [Fact]
    public async Task SQ04_NewInvoice_NoExistingLine_QuantitiesZero()
    {
        // No prior seed — DocEntry 1004 does not exist yet
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(1004), new[] { MakeLine(1004, 0) });

        Assert.Equal(0m, ReadReturnedQty(1004, 0));
        Assert.Equal(0m, ReadPendingReturnQty(1004, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ05: multi-line preservation keyed by LineNum
    // ─────────────────────────────────────────────────────────────────────────

    // SQ05 — two invoice lines with different quantities each preserved by LineNum
    [Fact]
    public async Task SQ05_TwoLines_QuantitiesPreservedByLineNum()
    {
        SeedLine(docEntry: 1005, lineNum: 0, returnedQty: 2m, pendingReturnQty: 1m);
        SeedLine(docEntry: 1005, lineNum: 1, returnedQty: 4m, pendingReturnQty: 0m);

        await _svc.UpsertSingleInvoiceAsync(MakeHeader(1005),
            new[] { MakeLine(1005, 0), MakeLine(1005, 1) });

        Assert.Equal(2m, ReadReturnedQty(1005, 0));
        Assert.Equal(1m, ReadPendingReturnQty(1005, 0));
        Assert.Equal(4m, ReadReturnedQty(1005, 1));
        Assert.Equal(0m, ReadPendingReturnQty(1005, 1));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ06: DocEntry scope — other invoices untouched
    // ─────────────────────────────────────────────────────────────────────────

    // SQ06 — refreshing DocEntry A leaves DocEntry B completely untouched
    [Fact]
    public async Task SQ06_ScopedToDocEntry_OtherDocEntryUntouched()
    {
        SeedLine(docEntry: 2001, lineNum: 0, returnedQty: 7m, pendingReturnQty: 2m);
        SeedLine(docEntry: 2002, lineNum: 0, returnedQty: 9m, pendingReturnQty: 3m);

        // Refresh only DocEntry 2001
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(2001), new[] { MakeLine(2001, 0) });

        // DocEntry 2001: quantities preserved
        Assert.Equal(7m, ReadReturnedQty(2001, 0));
        Assert.Equal(2m, ReadPendingReturnQty(2001, 0));

        // DocEntry 2002: untouched
        Assert.Equal(9m, ReadReturnedQty(2002, 0));
        Assert.Equal(3m, ReadPendingReturnQty(2002, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ07: idempotency — repeated refreshes do not inflate or reset
    // ─────────────────────────────────────────────────────────────────────────

    // SQ07 — repeated targeted refresh twice leaves quantities unchanged
    [Fact]
    public async Task SQ07_RepeatedRefresh_QuantitiesIdempotent()
    {
        SeedLine(docEntry: 3001, lineNum: 0, returnedQty: 4m, pendingReturnQty: 1m);

        var header = MakeHeader(3001);
        var lines  = new[] { MakeLine(3001, 0) };

        await _svc.UpsertSingleInvoiceAsync(header, lines);
        await _svc.UpsertSingleInvoiceAsync(header, lines);

        Assert.Equal(4m, ReadReturnedQty(3001, 0));
        Assert.Equal(1m, ReadPendingReturnQty(3001, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ08: drift path — same preservation via same code path
    // ─────────────────────────────────────────────────────────────────────────

    // SQ08 — drift repair (InvoiceDriftDetectionJob → RefreshAsync → UpsertSingleInvoiceAsync)
    //         uses the same preservation invariant
    [Fact]
    public async Task SQ08_DriftPath_UsesPreservation()
    {
        // InvoiceDriftDetectionJob calls InvoiceMirrorRefreshService.RefreshAsync,
        // which calls InvoiceCacheService.UpsertSingleInvoiceAsync — the same method under test.
        SeedLine(docEntry: 4001, lineNum: 0, returnedQty: 6m, pendingReturnQty: 2m);

        await _svc.UpsertSingleInvoiceAsync(MakeHeader(4001), new[] { MakeLine(4001, 0) });

        Assert.Equal(6m, ReadReturnedQty(4001, 0));
        Assert.Equal(2m, ReadPendingReturnQty(4001, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQ09–SQ10: authority invariants — mirror owner survives later refresh
    // ─────────────────────────────────────────────────────────────────────────

    // SQ09 — credit memo-reconciled ReturnedQty=3 survives a subsequent 13/A event refresh
    [Fact]
    public async Task SQ09_CreditMemo_UpdatedReturnedQty_Survives_EventRefresh()
    {
        // Credit memo reconciliation set ReturnedQty=3 for this line
        SeedLine(docEntry: 5001, lineNum: 0, returnedQty: 3m, pendingReturnQty: 0m);

        // 13/A event arrives → fresh SAP read → MapToLines returns ReturnedQty=0
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(5001), new[] { MakeLine(5001, 0) });

        Assert.Equal(3m, ReadReturnedQty(5001, 0));
    }

    // SQ10 — open ORRR PendingReturnQty=2 survives a subsequent invoice event/drift refresh
    [Fact]
    public async Task SQ10_OpenORRR_PendingReturnQty_Survives_Refresh()
    {
        // Open return request set PendingReturnQty=2 for this line
        SeedLine(docEntry: 6001, lineNum: 0, returnedQty: 0m, pendingReturnQty: 2m);

        // Invoice refresh arrives → fresh SAP read → MapToLines returns PendingReturnQty=0
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(6001), new[] { MakeLine(6001, 0) });

        Assert.Equal(2m, ReadPendingReturnQty(6001, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SeedLine(int docEntry, int lineNum, decimal returnedQty, decimal pendingReturnQty)
    {
        if (!_db.Invoices.Any(i => i.DocEntry == docEntry))
        {
            _db.Invoices.Add(MakeHeader(docEntry));
            _db.SaveChanges();
        }

        _db.InvoiceLines.Add(new CachedInvoiceLine
        {
            DocEntry         = docEntry,
            LineNum          = lineNum,
            ItemCode         = "TEST-ITEM",
            Dscription       = "Test item",
            Quantity         = 10m,
            Price            = 100m,
            LineTotal        = 1000m,
            ReturnedQty      = returnedQty,
            PendingReturnQty = pendingReturnQty,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private static CachedInvoice MakeHeader(int docEntry) => new CachedInvoice
    {
        DocEntry          = docEntry,
        DocNum            = docEntry,
        InvoiceDocNum     = docEntry,
        DocDate           = new DateTime(2026, 1, 1),
        DocStatus         = "O",
        Canceled          = "N",
        DocStatusDisplay  = "Open",
        CardCode          = "C001",
        CardName          = "Test Customer",
        DocTotal          = 1000m,
        PaidToDate        = 0m,
        BalanceDue        = 1000m,
        DaysOverdue       = 0,
        SalesEmployeeCode = 1,
        SalesEmployeeName = "Agent",
        GroupNum          = 1,
    };

    // Simulates what InvoiceMirrorRefreshService.MapToLines produces:
    // ReturnedQty and PendingReturnQty are 0 because SAP INV1 is not the authority.
    private static CachedInvoiceLine MakeLine(int docEntry, int lineNum) => new CachedInvoiceLine
    {
        DocEntry         = docEntry,
        LineNum          = lineNum,
        ItemCode         = "TEST-ITEM",
        Dscription       = "Test item",
        Quantity         = 10m,
        Price            = 100m,
        LineTotal        = 1000m,
        ReturnedQty      = 0m,
        PendingReturnQty = 0m,
    };

    private decimal ReadReturnedQty(int docEntry, int lineNum)
    {
        _db.ChangeTracker.Clear();
        return _db.InvoiceLines
            .Where(l => l.DocEntry == docEntry && l.LineNum == lineNum)
            .Select(l => l.ReturnedQty)
            .Single();
    }

    private decimal ReadPendingReturnQty(int docEntry, int lineNum)
    {
        _db.ChangeTracker.Clear();
        return _db.InvoiceLines
            .Where(l => l.DocEntry == docEntry && l.LineNum == lineNum)
            .Select(l => l.PendingReturnQty)
            .Single();
    }
}
