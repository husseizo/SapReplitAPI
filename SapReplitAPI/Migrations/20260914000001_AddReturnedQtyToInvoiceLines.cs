using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddReturnedQtyToInvoiceLines : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ReturnedQty may already exist from a manual startup ALTER TABLE; ADD COLUMN IF NOT EXISTS is idempotent.
            migrationBuilder.Sql(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""ReturnedQty"" REAL NOT NULL DEFAULT 0");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReturnedQty", table: "InvoiceLines");
        }
    }
}
