using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class FixCacheDbChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoicePayments_PaymentDocEntry",
                table: "InvoicePayments");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_DocEntry",
                table: "InvoicePayments",
                column: "DocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_DocEntry_PaymentDocEntry",
                table: "InvoicePayments",
                columns: new[] { "DocEntry", "PaymentDocEntry" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_PaymentDocEntry",
                table: "InvoicePayments",
                column: "PaymentDocEntry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoicePayments_DocEntry",
                table: "InvoicePayments");

            migrationBuilder.DropIndex(
                name: "IX_InvoicePayments_DocEntry_PaymentDocEntry",
                table: "InvoicePayments");

            migrationBuilder.DropIndex(
                name: "IX_InvoicePayments_PaymentDocEntry",
                table: "InvoicePayments");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_PaymentDocEntry",
                table: "InvoicePayments",
                column: "PaymentDocEntry",
                unique: true);
        }
    }
}
