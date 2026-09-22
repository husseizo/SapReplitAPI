using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddInvoiceRefToCreditMemoLines : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvoiceDocEntry",
                table: "CreditMemoLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoiceLineNum",
                table: "CreditMemoLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoLines_InvoiceDocEntry",
                table: "CreditMemoLines",
                column: "InvoiceDocEntry");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_CreditMemoLines_InvoiceDocEntry", table: "CreditMemoLines");
            migrationBuilder.DropColumn(name: "InvoiceDocEntry", table: "CreditMemoLines");
            migrationBuilder.DropColumn(name: "InvoiceLineNum",  table: "CreditMemoLines");
        }
    }
}
