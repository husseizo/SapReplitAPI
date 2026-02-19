using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddDetailedInvoiceStatusCacheTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DetailedInvoiceStatusCache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SalesName = table.Column<string>(type: "TEXT", nullable: false),
                    PostingDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InvoiceNo = table.Column<string>(type: "TEXT", nullable: false),
                    ReinvoicedFrom = table.Column<string>(type: "TEXT", nullable: false),
                    InvoiceStatus = table.Column<string>(type: "TEXT", nullable: false),
                    PaidDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Customer = table.Column<string>(type: "TEXT", nullable: false),
                    CashSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreditSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReturnedCashInvoice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaymentsStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CancellationStatus = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetailedInvoiceStatusCache", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetailedInvoiceStatusCache");
        }
    }
}
