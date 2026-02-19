using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class InitCacheSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoicePayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PaymentDocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CardCode = table.Column<string>(type: "TEXT", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", nullable: false),
                    AmountApplied = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BankTransferAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BankTransferReference = table.Column<string>(type: "TEXT", nullable: false),
                    DebitAccountCode = table.Column<string>(type: "TEXT", nullable: false),
                    DebitAccountName = table.Column<string>(type: "TEXT", nullable: false),
                    SalesEmployeeCode = table.Column<string>(type: "TEXT", nullable: false),
                    SalesEmployeeName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoicePayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    DocNum = table.Column<int>(type: "INTEGER", nullable: false),
                    DocDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CardCode = table.Column<string>(type: "TEXT", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", nullable: false),
                    DocTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaidToDate = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BalanceDue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SalesEmployeeCode = table.Column<int>(type: "INTEGER", nullable: false),
                    SalesEmployeeName = table.Column<string>(type: "TEXT", nullable: false),
                    DaysOverdue = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.DocEntry);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                    ItemName = table.Column<string>(type: "TEXT", nullable: false),
                    U_Article_No = table.Column<string>(type: "TEXT", nullable: true),
                    U_MdlTEST = table.Column<string>(type: "TEXT", nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OnHandQty = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OnHand = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WhsCode = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    U_Item_Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMetadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNum = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                    Dscription = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    U_Item_Name = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    U_MDLTsT = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_DocEntry",
                        column: x => x.DocEntry,
                        principalTable: "Invoices",
                        principalColumn: "DocEntry",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_DocEntry",
                table: "InvoiceLines",
                column: "DocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMetadata_Type",
                table: "SyncMetadata",
                column: "Type",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceLines");

            migrationBuilder.DropTable(
                name: "InvoicePayments");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "SyncMetadata");

            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
