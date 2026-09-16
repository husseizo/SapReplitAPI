using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.Events;
using Xunit;

namespace SapReplitAPI.Tests.Invoice;

/// <summary>
/// IF01–IF18: Invoice mirror freshness — mapping and contract tests.
///
/// Covers:
///   - MapToLines UDF fields (IF01-IF07)
///   - MapToHeader DocStatusDisplay logic (IF08-IF14)
///   - Multi-line mapping correctness (IF15-IF16)
///   - RefreshResult record contract (IF17-IF18)
///
/// No SAP COM calls, no SQLite, no Neon — pure unit tests on internal static methods.
/// No SAP mutations (0 OINV/ODLN/ORDR created).
/// </summary>
public sealed class InvoiceMirrorFreshnessTests
{
    // ────────────────────────────────────────────────────────────────────────
    // MapToLines — UDF field mapping (IF01–IF07)
    // ────────────────────────────────────────────────────────────────────────

    // IF01: U_MDLTsT is populated from the line dto
    [Fact]
    public void IF01_MapToLines_U_MDLTsT_Populated()
    {
        var dto = MakeDto(1, new[] { MakeLine(0, u_mdltst: "MDL-001") });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Single(lines);
        Assert.Equal("MDL-001", lines[0].U_MDLTsT);
    }

    // IF02: U_ItemName is populated from the line dto
    [Fact]
    public void IF02_MapToLines_U_ItemName_Populated()
    {
        var dto = MakeDto(1, new[] { MakeLine(0, u_itemName: "Brake Pad") });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal("Brake Pad", lines[0].U_ItemName);
    }

    // IF03: U_Manufacturer is populated from the line dto
    [Fact]
    public void IF03_MapToLines_U_Manufacturer_Populated()
    {
        var dto = MakeDto(1, new[] { MakeLine(0, u_mfr: "Bosch") });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal("Bosch", lines[0].U_Manufacturer);
    }

    // IF04: null U_MDLTsT on the line dto coerces to empty string (no NullReferenceException)
    [Fact]
    public void IF04_MapToLines_Null_U_MDLTsT_CoercesToEmpty()
    {
        var dto = MakeDto(1, new[] { MakeLine(0, u_mdltst: null) });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal("", lines[0].U_MDLTsT);
    }

    // IF05: null U_ItemName on the line dto coerces to empty string
    [Fact]
    public void IF05_MapToLines_Null_U_ItemName_CoercesToEmpty()
    {
        var dto = MakeDto(1, new[] { MakeLine(0, u_itemName: null) });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal("", lines[0].U_ItemName);
    }

    // IF06: U_Manufacturer is mapped through: populated value appears on output line
    [Fact]
    public void IF06_MapToLines_U_Manufacturer_MapsThrough()
    {
        var dto = MakeDto(1, new[] { MakeLine(0, u_mfr: "Bosch") });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal("Bosch", lines[0].U_Manufacturer);
    }

    // IF07: null dto.Lines produces an empty result (no exception)
    [Fact]
    public void IF07_MapToLines_NullLines_ReturnsEmpty()
    {
        var dto = new InvoiceDto { DocEntry = 99, Lines = null };
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Empty(lines);
    }

    // ────────────────────────────────────────────────────────────────────────
    // MapToHeader — DocStatusDisplay logic (IF08–IF14)
    // ────────────────────────────────────────────────────────────────────────

    // IF08: Status="O" → DocStatusDisplay="Open"
    [Fact]
    public void IF08_MapToHeader_Open_Status()
    {
        var dto = MakeHeaderDto(1, status: "O", canceled: "N");
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, EmptyLifecycle());
        Assert.Equal("Open", header.DocStatusDisplay);
    }

    // IF09: Status="C", Canceled="Canceled" → DocStatusDisplay="Cancelled"
    [Fact]
    public void IF09_MapToHeader_Canceled_Status()
    {
        var dto = MakeHeaderDto(1, status: "C", canceled: "Canceled");
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, EmptyLifecycle());
        Assert.Equal("Cancelled", header.DocStatusDisplay);
    }

    // IF10: Status="C", Canceled="Cancellation" → DocStatusDisplay="Cancellation"
    [Fact]
    public void IF10_MapToHeader_Cancellation_Status()
    {
        var dto = MakeHeaderDto(1, status: "C", canceled: "Cancellation");
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, EmptyLifecycle());
        Assert.Equal("Cancellation", header.DocStatusDisplay);
    }

    // IF11: Status="C", Canceled="Not Canceled" → DocStatusDisplay="Closed"
    [Fact]
    public void IF11_MapToHeader_Closed_Status()
    {
        var dto = MakeHeaderDto(1, status: "C", canceled: "Not Canceled");
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, EmptyLifecycle());
        Assert.Equal("Closed", header.DocStatusDisplay);
    }

    // IF12: lifecycle dict entry overrides the legacy compute (e.g. lifecycle says "PaidAndClosed")
    [Fact]
    public void IF12_MapToHeader_Lifecycle_Override_Wins()
    {
        var dto = MakeHeaderDto(42, status: "O", canceled: "N");
        var lifecycle = new Dictionary<int, InvoiceLifecycleStatusResult>
        {
            [42] = new InvoiceLifecycleStatusResult { DocStatusDisplay = "PaidAndClosed" }
        };
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, lifecycle);
        Assert.Equal("PaidAndClosed", header.DocStatusDisplay);
    }

    // IF13: blank DocStatus → DocStatusDisplay="Unknown"
    [Fact]
    public void IF13_MapToHeader_BlankStatus_Unknown()
    {
        var dto = MakeHeaderDto(1, status: "", canceled: "");
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, EmptyLifecycle());
        Assert.Equal("Unknown", header.DocStatusDisplay);
    }

    // IF14: all scalar header fields are mapped from the dto
    [Fact]
    public void IF14_MapToHeader_AllScalarFields_Mapped()
    {
        var dto = new InvoiceDto
        {
            DocEntry = 500, DocNum = 1234, DocDate = new DateTime(2026, 9, 1),
            Status = "O", Canceled = "Not Canceled",
            CardCode = "C001", CardName = "Acme Ltd",
            DocTotal = 10000m, PaidToDate = 5000m, BalanceDue = 5000m,
            DaysOverdue = 3, SalesEmployeeCode = 7, SalesEmployeeName = "Alice",
            GroupNum = 2, ZoneRef = "ZoneFulfillment",
            U_ReplitId = "rpl-001", DeliveryLocation = "Dar es Salaam"
        };
        var header = InvoiceMirrorRefreshService.MapToHeader(dto, EmptyLifecycle());

        Assert.Equal(500,  header.DocEntry);
        Assert.Equal(1234, header.DocNum);
        Assert.Equal("C001",            header.CardCode);
        Assert.Equal("Acme Ltd",        header.CardName);
        Assert.Equal(10000m,            header.DocTotal);
        Assert.Equal(5000m,             header.PaidToDate);
        Assert.Equal(5000m,             header.BalanceDue);
        Assert.Equal(3,                 header.DaysOverdue);
        Assert.Equal(7,                 header.SalesEmployeeCode);
        Assert.Equal("Alice",           header.SalesEmployeeName);
        Assert.Equal(2,                 header.GroupNum);
        Assert.Equal("ZoneFulfillment", header.ZoneRef);
        Assert.Equal("rpl-001",         header.U_ReplitId);
        Assert.Equal("Dar es Salaam",   header.DeliveryLocation);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Multi-line mapping correctness (IF15–IF16)
    // ────────────────────────────────────────────────────────────────────────

    // IF15: all lines in the output carry the dto's DocEntry (not the line's own)
    [Fact]
    public void IF15_MapToLines_MultiLine_AllCarryDocEntry()
    {
        var dto = MakeDto(99, new[]
        {
            MakeLine(0, itemCode: "A"),
            MakeLine(1, itemCode: "B"),
            MakeLine(2, itemCode: "C"),
        });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(99, l.DocEntry));
    }

    // IF16: LineNum is cast correctly from the dto's decimal LineNum
    [Fact]
    public void IF16_MapToLines_LineNum_CastCorrectly()
    {
        var dto = MakeDto(1, new[] { MakeLine(lineNum: 7) });
        var lines = InvoiceMirrorRefreshService.MapToLines(dto);
        Assert.Equal(7, lines[0].LineNum);
    }

    // ────────────────────────────────────────────────────────────────────────
    // RefreshResult record contract (IF17–IF18)
    // ────────────────────────────────────────────────────────────────────────

    // IF17: RefreshResult record with Ok=false has null Dto/Header/Lines and non-null Error
    [Fact]
    public void IF17_RefreshResult_FailedRecord_NullPayload()
    {
        var result = new RefreshResult(
            Ok: false, Error: "Not found",
            DocEntry: 1, DocNum: 0, LineCount: 0,
            SapReadMs: 0, SqliteMs: 0, NeonMs: 0, TotalMs: 5,
            Dto: null, Header: null, Lines: null);

        Assert.False(result.Ok);
        Assert.Equal("Not found", result.Error);
        Assert.Null(result.Dto);
        Assert.Null(result.Header);
        Assert.Null(result.Lines);
    }

    // IF18: RefreshResult record with Ok=true has non-null Dto/Header/Lines
    [Fact]
    public void IF18_RefreshResult_SuccessRecord_NonNullPayload()
    {
        var dto    = new InvoiceDto { DocEntry = 1 };
        var header = new CachedInvoice { DocEntry = 1 };
        var lines  = new List<CachedInvoiceLine> { new() { DocEntry = 1 } };

        var result = new RefreshResult(
            Ok: true, Error: null,
            DocEntry: 1, DocNum: 100, LineCount: 1,
            SapReadMs: 10, SqliteMs: 20, NeonMs: 30, TotalMs: 60,
            Dto: dto, Header: header, Lines: lines);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.NotNull(result.Dto);
        Assert.NotNull(result.Header);
        Assert.NotNull(result.Lines);
        Assert.Equal(1,  result.LineCount);
        Assert.Equal(10, result.SapReadMs);
        Assert.Equal(20, result.SqliteMs);
        Assert.Equal(30, result.NeonMs);
        Assert.Equal(60, result.TotalMs);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static InvoiceDto MakeDto(int docEntry, InvoiceLineDto[] lines)
        => new() { DocEntry = docEntry, Lines = lines.ToList() };

    private static InvoiceLineDto MakeLine(
        decimal lineNum = 0,
        string? itemCode = "ITEM",
        string? u_mdltst = "",
        string? u_itemName = "",
        string? u_mfr = "")
        => new()
        {
            LineNum        = lineNum,
            ItemCode       = itemCode,
            Dscription     = "Test item",
            Quantity       = 1,
            Price          = 100,
            LineTotal      = 100,
            U_Item_Name    = "",
            U_MdlTEST      = "",
            U_MDLTsT       = u_mdltst,
            U_ItemName     = u_itemName,
            U_Manufacturer = u_mfr ?? ""
        };

    private static InvoiceDto MakeHeaderDto(int docEntry, string status, string canceled)
        => new()
        {
            DocEntry = docEntry, DocNum = docEntry * 10,
            Status = status, Canceled = canceled,
            CardCode = "C001", CardName = "Test",
            DocTotal = 1000m, PaidToDate = 0m, BalanceDue = 1000m,
            SalesEmployeeName = "", SalesEmployeeCode = 1
        };

    private static IReadOnlyDictionary<int, InvoiceLifecycleStatusResult> EmptyLifecycle()
        => new Dictionary<int, InvoiceLifecycleStatusResult>();
}
