using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddSoDeliveryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SoDeliveryRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProcessingDate         = table.Column<DateTime>(type: "TEXT",    nullable: false),
                    StartTime              = table.Column<DateTime>(type: "TEXT",    nullable: false),
                    EndTime                = table.Column<DateTime>(type: "TEXT",    nullable: true),
                    Status                 = table.Column<string>(type: "TEXT",      nullable: false, defaultValue: "Running"),
                    TriggeredBy            = table.Column<string>(type: "TEXT",      nullable: false, defaultValue: "Job"),
                    IsForced               = table.Column<bool>(type: "INTEGER",     nullable: false, defaultValue: false),
                    TotalOrders            = table.Column<int>(type: "INTEGER",      nullable: false, defaultValue: 0),
                    SuccessCount           = table.Column<int>(type: "INTEGER",      nullable: false, defaultValue: 0),
                    FailedCount            = table.Column<int>(type: "INTEGER",      nullable: false, defaultValue: 0),
                    SkippedCount           = table.Column<int>(type: "INTEGER",      nullable: false, defaultValue: 0),
                    ExceptionCount         = table.Column<int>(type: "INTEGER",      nullable: false, defaultValue: 0),
                    TotalDeliveriesCreated = table.Column<int>(type: "INTEGER",      nullable: false, defaultValue: 0),
                    PdfPath                = table.Column<string>(type: "TEXT",      nullable: true),
                    ErrorMessage           = table.Column<string>(type: "TEXT",      nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoDeliveryRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SoDeliveryLogs",
                columns: table => new
                {
                    Id               = table.Column<int>(type: "INTEGER",  nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId            = table.Column<int>(type: "INTEGER",  nullable: false),
                    SoDocEntry       = table.Column<int>(type: "INTEGER",  nullable: false),
                    SoDocNum         = table.Column<int>(type: "INTEGER",  nullable: false),
                    CustomerCode     = table.Column<string>(type: "TEXT",  nullable: false, defaultValue: ""),
                    CustomerName     = table.Column<string>(type: "TEXT",  nullable: false, defaultValue: ""),
                    Status           = table.Column<string>(type: "TEXT",  nullable: false),
                    DeliveryDocEntry = table.Column<int>(type: "INTEGER",  nullable: true),
                    DeliveryDocNum   = table.Column<int>(type: "INTEGER",  nullable: true),
                    ErrorMessage     = table.Column<string>(type: "TEXT",  nullable: true),
                    SapErrorCode     = table.Column<string>(type: "TEXT",  nullable: true),
                    SapErrorMessage  = table.Column<string>(type: "TEXT",  nullable: true),
                    ProcessedAt      = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs       = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoDeliveryLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoDeliveryLogs_SoDeliveryRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "SoDeliveryRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SoDeliveryLineLogs",
                columns: table => new
                {
                    Id              = table.Column<int>(type: "INTEGER",           nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LogId           = table.Column<int>(type: "INTEGER",           nullable: false),
                    LineNum         = table.Column<int>(type: "INTEGER",           nullable: false),
                    ItemCode        = table.Column<string>(type: "TEXT",           nullable: false, defaultValue: ""),
                    ItemDescription = table.Column<string>(type: "TEXT",           nullable: false, defaultValue: ""),
                    Quantity        = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OpenQuantity    = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    WarehouseCode   = table.Column<string>(type: "TEXT",           nullable: false, defaultValue: ""),
                    OnHandBefore    = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OnHandAfter     = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Status          = table.Column<string>(type: "TEXT",           nullable: false),
                    ErrorMessage    = table.Column<string>(type: "TEXT",           nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoDeliveryLineLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoDeliveryLineLogs_SoDeliveryLogs_LogId",
                        column: x => x.LogId,
                        principalTable: "SoDeliveryLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // ── SoDeliveryRuns indexes ─────────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_SoDeliveryRuns_ProcessingDate",
                table: "SoDeliveryRuns",
                column: "ProcessingDate");

            migrationBuilder.CreateIndex(
                name: "IX_SoDeliveryRuns_Status",
                table: "SoDeliveryRuns",
                column: "Status");

            // ── SoDeliveryLogs indexes ─────────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_SoDeliveryLogs_RunId",
                table: "SoDeliveryLogs",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SoDeliveryLogs_SoDocEntry",
                table: "SoDeliveryLogs",
                column: "SoDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_SoDeliveryLogs_Status",
                table: "SoDeliveryLogs",
                column: "Status");

            // Unique: one SO cannot be logged twice within the same run
            migrationBuilder.CreateIndex(
                name: "UX_SoDeliveryLogs_RunId_SoDocEntry",
                table: "SoDeliveryLogs",
                columns: new[] { "RunId", "SoDocEntry" },
                unique: true);

            // ── SoDeliveryLineLogs indexes ─────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_SoDeliveryLineLogs_LogId",
                table: "SoDeliveryLineLogs",
                column: "LogId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop child tables first to respect FK constraints
            migrationBuilder.DropTable(name: "SoDeliveryLineLogs");
            migrationBuilder.DropTable(name: "SoDeliveryLogs");
            migrationBuilder.DropTable(name: "SoDeliveryRuns");
        }
    }
}
