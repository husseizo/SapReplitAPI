using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddZfReportCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ZoneFulfillmentReports",
                columns: table => new
                {
                    ReportId          = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId         = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrchestrationId   = table.Column<long>(type: "INTEGER", nullable: false),
                    ReportType        = table.Column<string>(type: "TEXT", nullable: false),
                    Status            = table.Column<string>(type: "TEXT", nullable: false),
                    SalesOrderDocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    SalesOrderDocNum  = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryDocEntry  = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryDocNum    = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceDocEntry   = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceDocNum     = table.Column<int>(type: "INTEGER", nullable: true),
                    CardCode          = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveryLocation  = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    ZoneRef           = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    U_ReplitId        = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    SnapshotJson      = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotSha256    = table.Column<string>(type: "TEXT", nullable: true),
                    FileName          = table.Column<string>(type: "TEXT", nullable: true),
                    MimeType          = table.Column<string>(type: "TEXT", nullable: true),
                    FileSize          = table.Column<long>(type: "INTEGER", nullable: true),
                    Sha256            = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedAtUtc    = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc      = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorMessage      = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneFulfillmentReports", x => x.ReportId);
                });

            migrationBuilder.CreateTable(
                name: "ZoneFulfillmentReportLines",
                columns: table => new
                {
                    Id                 = table.Column<int>(type: "INTEGER", nullable: false)
                                             .Annotation("Sqlite:Autoincrement", true),
                    ReportId           = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineSeq            = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode           = table.Column<string>(type: "TEXT", nullable: false),
                    Description        = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedQty       = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PickedQty          = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DeliveredQty       = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitPrice          = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal          = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WhsCode            = table.Column<string>(type: "TEXT", nullable: true),
                    OpklAbsEntry       = table.Column<int>(type: "INTEGER", nullable: true),
                    PickerUserId       = table.Column<int>(type: "INTEGER", nullable: true),
                    PickerUserCode     = table.Column<string>(type: "TEXT", nullable: true),
                    PickerName         = table.Column<string>(type: "TEXT", nullable: true),
                    BinAbsEntry        = table.Column<int>(type: "INTEGER", nullable: true),
                    BinCode            = table.Column<string>(type: "TEXT", nullable: true),
                    BinQty             = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    SalesOrderBaseLine = table.Column<int>(type: "INTEGER", nullable: true),
                    DeliveryLineNum    = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceLineNum     = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceBaseType    = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceBaseEntry   = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceBaseLine    = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneFulfillmentReportLines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ZfReports_RequestId",
                table: "ZoneFulfillmentReports",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ZfReports_DeliveryDocEntry",
                table: "ZoneFulfillmentReports",
                column: "DeliveryDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_ZfReports_InvoiceDocEntry",
                table: "ZoneFulfillmentReports",
                column: "InvoiceDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_ZfReports_Status",
                table: "ZoneFulfillmentReports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ZfReports_UpdatedAtUtc",
                table: "ZoneFulfillmentReports",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ZfReportLines_ReportId",
                table: "ZoneFulfillmentReportLines",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "UX_ZfReportLines_ReportId_LineSeq",
                table: "ZoneFulfillmentReportLines",
                columns: new[] { "ReportId", "LineSeq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ZoneFulfillmentReportLines");
            migrationBuilder.DropTable(name: "ZoneFulfillmentReports");
        }
    }
}
