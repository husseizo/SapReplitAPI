using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.Events;
using Xunit;

namespace SapReplitAPI.Tests.Invoice;

/// <summary>
/// BR01–BR15: Invoice base-document reference (BaseType/BaseEntry/BaseLine) tests.
/// Proves that INV1 base refs flow end-to-end from InvoiceLineDto through CachedInvoiceLine,
/// survive SQLite upsert, and do not disturb ReturnedQty / PendingReturnQty.
///
/// No SAP COM calls. No Neon/PostgreSQL dependency.
/// </summary>
public sealed class InvoiceBaseRefTests : IDisposable
{
    private readonly SqliteConnection  _conn;
    private readonly CacheDbContext    _db;
    private readonly InvoiceCacheService _svc;

    public InvoiceBaseRefTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var opts = new DbContextOptionsBuilder<CacheDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db  = new CacheDbContext(opts);
        _db.Database.EnsureCreated();
        _svc = new InvoiceCacheService(_db, null!, NullLogger<InvoiceCacheService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR01–BR03: BaseType value round-trips through CachedInvoiceLine
    // ─────────────────────────────────────────────────────────────────────────

    // BR01 — delivery-based invoice (BaseType=15) written to SQLite and read back
    [Fact]
    public async Task BR01_BaseType15_Delivery_PersistedToSQLite()
    {
        var line = MakeLine(9001, 0, baseType: 15, baseEntry: 200, baseLine: 0);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9001), new[] { line });

        var stored = ReadLine(9001, 0);
        Assert.Equal(15,  stored.BaseType);
        Assert.Equal(200, stored.BaseEntry);
        Assert.Equal(0,   stored.BaseLine);
    }

    // BR02 — sales-order-based invoice (BaseType=17) written to SQLite and read back
    [Fact]
    public async Task BR02_BaseType17_SalesOrder_PersistedToSQLite()
    {
        var line = MakeLine(9002, 0, baseType: 17, baseEntry: 300, baseLine: 2);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9002), new[] { line });

        var stored = ReadLine(9002, 0);
        Assert.Equal(17,  stored.BaseType);
        Assert.Equal(300, stored.BaseEntry);
        Assert.Equal(2,   stored.BaseLine);
    }

    // BR03 — manual invoice (BaseType=-1) written to SQLite and read back
    [Fact]
    public async Task BR03_BaseTypeMinus1_ManualInvoice_PersistedToSQLite()
    {
        var line = MakeLine(9003, 0, baseType: -1, baseEntry: null, baseLine: null);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9003), new[] { line });

        var stored = ReadLine(9003, 0);
        Assert.Equal(-1, stored.BaseType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR04: nullable semantics — BaseEntry/BaseLine null when BaseType=-1
    // ─────────────────────────────────────────────────────────────────────────

    // BR04 — BaseEntry and BaseLine are null when BaseType=-1 (no base document)
    [Fact]
    public async Task BR04_BaseTypeMinus1_NullableColumnsNull()
    {
        var line = MakeLine(9004, 0, baseType: -1, baseEntry: null, baseLine: null);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9004), new[] { line });

        var stored = ReadLine(9004, 0);
        Assert.Null(stored.BaseEntry);
        Assert.Null(stored.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR05: MapToLines copies BaseType/BaseEntry/BaseLine from InvoiceLineDto
    // ─────────────────────────────────────────────────────────────────────────

    // BR05 — InvoiceMirrorRefreshService.MapToLines propagates all three base-ref fields
    [Fact]
    public void BR05_MapToLines_CopiesBaseRefs()
    {
        var dto = new InvoiceDto
        {
            DocEntry = 1,
            DocNum   = 1,
            Lines    = new List<InvoiceLineDto>
            {
                new InvoiceLineDto
                {
                    ItemCode   = "X",
                    Dscription = "X",
                    Quantity   = 1m,
                    Price      = 10m,
                    LineTotal  = 10m,
                    BaseType   = 15,
                    BaseEntry  = 200,
                    BaseLine   = 1,
                }
            }
        };

        var lines = InvoiceMirrorRefreshService.MapToLines(dto);

        Assert.Single(lines);
        Assert.Equal(15,  lines[0].BaseType);
        Assert.Equal(200, lines[0].BaseEntry);
        Assert.Equal(1,   lines[0].BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR06: UpsertSingleInvoiceAsync carries base refs into SQLite
    // ─────────────────────────────────────────────────────────────────────────

    // BR06 — targeted invoice upsert writes BaseType/BaseEntry/BaseLine correctly
    [Fact]
    public async Task BR06_UpsertSingleInvoiceAsync_PersistsBaseRefs()
    {
        var line = MakeLine(9006, 0, baseType: 15, baseEntry: 134, baseLine: 0);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9006), new[] { line });

        var stored = ReadLine(9006, 0);
        Assert.Equal(15,  stored.BaseType);
        Assert.Equal(134, stored.BaseEntry);
        Assert.Equal(0,   stored.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR07: base refs survive a second upsert (idempotent write)
    // ─────────────────────────────────────────────────────────────────────────

    // BR07 — second UpsertSingleInvoiceAsync call leaves base refs unchanged
    [Fact]
    public async Task BR07_UpsertSingleInvoiceAsync_BaseRefsIdempotent()
    {
        var line = MakeLine(9007, 0, baseType: 15, baseEntry: 161, baseLine: 0);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9007), new[] { line });
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9007), new[] { line });

        var stored = ReadLine(9007, 0);
        Assert.Equal(15,  stored.BaseType);
        Assert.Equal(161, stored.BaseEntry);
        Assert.Equal(0,   stored.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR08: multi-line invoice — each line keeps its own base refs
    // ─────────────────────────────────────────────────────────────────────────

    // BR08 — two lines on the same invoice each retain independent base-ref values
    [Fact]
    public async Task BR08_MultiLine_EachLineRetainsOwnBaseRefs()
    {
        var line0 = MakeLine(9008, 0, baseType: 15, baseEntry: 200, baseLine: 0);
        var line1 = MakeLine(9008, 1, baseType: 17, baseEntry: 300, baseLine: 1);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9008), new[] { line0, line1 });

        var s0 = ReadLine(9008, 0);
        var s1 = ReadLine(9008, 1);
        Assert.Equal(15,  s0.BaseType);
        Assert.Equal(200, s0.BaseEntry);
        Assert.Equal(17,  s1.BaseType);
        Assert.Equal(300, s1.BaseEntry);
        Assert.Equal(1,   s1.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR09: backfill raw UPDATE writes correct base refs
    // ─────────────────────────────────────────────────────────────────────────

    // BR09 — raw UPDATE (same SQL as InvoiceBaseRefBackfillService) sets base refs on an existing row
    [Fact]
    public async Task BR09_BackfillUpdate_WritesCorrectBaseRefs()
    {
        // Seed a pre-migration row with default base refs (BaseType=0, nulls)
        SeedLineWithBaseRefs(9009, 0, baseType: 0, baseEntry: null, baseLine: null);

        // Execute the same UPDATE the backfill service would run
        await _conn.ExecuteNonQueryAsync(@"
UPDATE ""InvoiceLines""
SET    ""BaseType""  = 15,
       ""BaseEntry"" = 200,
       ""BaseLine""  = 0
WHERE  ""DocEntry""  = 9009
  AND  ""LineNum""   = 0");

        var stored = ReadLine(9009, 0);
        Assert.Equal(15,  stored.BaseType);
        Assert.Equal(200, stored.BaseEntry);
        Assert.Equal(0,   stored.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR10: backfill idempotent — second run produces same result
    // ─────────────────────────────────────────────────────────────────────────

    // BR10 — running the backfill UPDATE twice leaves values unchanged
    [Fact]
    public async Task BR10_BackfillUpdate_Idempotent()
    {
        SeedLineWithBaseRefs(9010, 0, baseType: 0, baseEntry: null, baseLine: null);

        const string sql = @"
UPDATE ""InvoiceLines""
SET    ""BaseType""  = 15,
       ""BaseEntry"" = 200,
       ""BaseLine""  = 0
WHERE  ""DocEntry""  = 9010
  AND  ""LineNum""   = 0";

        await _conn.ExecuteNonQueryAsync(sql);
        await _conn.ExecuteNonQueryAsync(sql); // second run

        var stored = ReadLine(9010, 0);
        Assert.Equal(15,  stored.BaseType);
        Assert.Equal(200, stored.BaseEntry);
        Assert.Equal(0,   stored.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR11–BR12: base refs do not disturb ReturnedQty / PendingReturnQty
    // ─────────────────────────────────────────────────────────────────────────

    // BR11 — ReturnedQty preserved when UpsertSingleInvoiceAsync writes base refs
    [Fact]
    public async Task BR11_ReturnedQty_Preserved_When_BaseRefsWritten()
    {
        SeedLineWithQuantities(9011, 0, returnedQty: 3m, pendingReturnQty: 0m);

        var line = MakeLine(9011, 0, baseType: 15, baseEntry: 200, baseLine: 0);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9011), new[] { line });

        var stored = ReadLine(9011, 0);
        Assert.Equal(3m, stored.ReturnedQty);
        Assert.Equal(15, stored.BaseType);
    }

    // BR12 — PendingReturnQty preserved when UpsertSingleInvoiceAsync writes base refs
    [Fact]
    public async Task BR12_PendingReturnQty_Preserved_When_BaseRefsWritten()
    {
        SeedLineWithQuantities(9012, 0, returnedQty: 0m, pendingReturnQty: 2m);

        var line = MakeLine(9012, 0, baseType: 17, baseEntry: 300, baseLine: 1);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9012), new[] { line });

        var stored = ReadLine(9012, 0);
        Assert.Equal(2m, stored.PendingReturnQty);
        Assert.Equal(17, stored.BaseType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR13: manual SAP invoice (BaseType=-1) has no ZF dependency
    // ─────────────────────────────────────────────────────────────────────────

    // BR13 — InvoiceLineDto has no ZF-specific properties (ZF lives outside this model)
    [Fact]
    public void BR13_InvoiceLineDto_HasNoZfDependency()
    {
        var lineType = typeof(InvoiceLineDto);
        Assert.Null(lineType.GetProperty("U_ZFRef"));
        Assert.Null(lineType.GetProperty("ZfOrderId"));
        Assert.Null(lineType.GetProperty("U_AppRef")); // U_AppRef lives on order header, not line
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR14: UpsertSingleInvoiceAsync scoped — other DocEntry base refs untouched
    // ─────────────────────────────────────────────────────────────────────────

    // BR14 — targeted upsert of DocEntry A leaves DocEntry B base refs unchanged
    [Fact]
    public async Task BR14_TargetedUpsert_DoesNotTouchOtherDocEntry()
    {
        // Seed DocEntry B (9099) with known base refs
        SeedLineWithBaseRefs(9099, 0, baseType: 17, baseEntry: 300, baseLine: 2);

        // Upsert DocEntry A (9014) — must not affect DocEntry B
        var lineA = MakeLine(9014, 0, baseType: 15, baseEntry: 200, baseLine: 0);
        await _svc.UpsertSingleInvoiceAsync(MakeHeader(9014), new[] { lineA });

        // DocEntry B must be untouched
        var storedB = ReadLine(9099, 0);
        Assert.Equal(17,  storedB.BaseType);
        Assert.Equal(300, storedB.BaseEntry);
        Assert.Equal(2,   storedB.BaseLine);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BR15: CachedDeliveryLine already has all required frontend fields
    // ─────────────────────────────────────────────────────────────────────────

    // BR15 — CachedDeliveryLine model has BaseType, BaseEntry, BaseLine, Dscription, WhsCode
    [Fact]
    public void BR15_CachedDeliveryLine_HasRequiredFrontendFields()
    {
        var t = typeof(CachedDeliveryLine);
        Assert.NotNull(t.GetProperty("BaseType"));
        Assert.NotNull(t.GetProperty("BaseEntry"));
        Assert.NotNull(t.GetProperty("BaseLine"));
        Assert.NotNull(t.GetProperty("Dscription"));
        Assert.NotNull(t.GetProperty("WhsCode"));
        Assert.NotNull(t.GetProperty("LineNum"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

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
        DocTotal          = 100m,
        PaidToDate        = 0m,
        BalanceDue        = 100m,
        DaysOverdue       = 0,
        SalesEmployeeCode = 1,
        SalesEmployeeName = "Agent",
        GroupNum          = 1,
    };

    private static CachedInvoiceLine MakeLine(
        int docEntry, int lineNum,
        int baseType, int? baseEntry, int? baseLine) => new CachedInvoiceLine
    {
        DocEntry         = docEntry,
        LineNum          = lineNum,
        ItemCode         = "TEST",
        Dscription       = "Test",
        Quantity         = 1m,
        Price            = 10m,
        LineTotal        = 10m,
        ReturnedQty      = 0m,
        PendingReturnQty = 0m,
        BaseType         = baseType,
        BaseEntry        = baseEntry,
        BaseLine         = baseLine,
    };

    private void SeedLineWithBaseRefs(
        int docEntry, int lineNum,
        int baseType, int? baseEntry, int? baseLine)
    {
        EnsureHeader(docEntry);
        _db.InvoiceLines.Add(new CachedInvoiceLine
        {
            DocEntry         = docEntry,
            LineNum          = lineNum,
            ItemCode         = "TEST",
            Dscription       = "Test",
            Quantity         = 1m,
            Price            = 10m,
            LineTotal        = 10m,
            ReturnedQty      = 0m,
            PendingReturnQty = 0m,
            BaseType         = baseType,
            BaseEntry        = baseEntry,
            BaseLine         = baseLine,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void SeedLineWithQuantities(
        int docEntry, int lineNum,
        decimal returnedQty, decimal pendingReturnQty)
    {
        EnsureHeader(docEntry);
        _db.InvoiceLines.Add(new CachedInvoiceLine
        {
            DocEntry         = docEntry,
            LineNum          = lineNum,
            ItemCode         = "TEST",
            Dscription       = "Test",
            Quantity         = 1m,
            Price            = 10m,
            LineTotal        = 10m,
            ReturnedQty      = returnedQty,
            PendingReturnQty = pendingReturnQty,
            BaseType         = 0,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void EnsureHeader(int docEntry)
    {
        if (!_db.Invoices.Any(i => i.DocEntry == docEntry))
        {
            _db.Invoices.Add(MakeHeader(docEntry));
            _db.SaveChanges();
        }
    }

    private CachedInvoiceLine ReadLine(int docEntry, int lineNum)
    {
        _db.ChangeTracker.Clear();
        return _db.InvoiceLines
            .Single(l => l.DocEntry == docEntry && l.LineNum == lineNum);
    }
}

/// <summary>Helper extension to run a raw SQL command against an open SqliteConnection.</summary>
internal static class SqliteConnectionExtensions
{
    internal static async Task ExecuteNonQueryAsync(this SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
