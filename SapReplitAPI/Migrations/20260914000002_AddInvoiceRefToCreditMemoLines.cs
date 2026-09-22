using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddInvoiceRefToCreditMemoLines : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Columns may already exist from a manual startup ALTER TABLE; IF NOT EXISTS is idempotent.
            migrationBuilder.Sql(@"ALTER TABLE ""CreditMemoLines"" ADD COLUMN IF NOT EXISTS ""InvoiceDocEntry"" INTEGER");
            migrationBuilder.Sql(@"ALTER TABLE ""CreditMemoLines"" ADD COLUMN IF NOT EXISTS ""InvoiceLineNum""  INTEGER");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_CreditMemoLines_InvoiceDocEntry"" ON ""CreditMemoLines""(""InvoiceDocEntry"")");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_CreditMemoLines_InvoiceDocEntry", table: "CreditMemoLines");
            migrationBuilder.DropColumn(name: "InvoiceDocEntry", table: "CreditMemoLines");
            migrationBuilder.DropColumn(name: "InvoiceLineNum",  table: "CreditMemoLines");
        }
    }
}
