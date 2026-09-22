using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddBaseRefsToInvoiceLines : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use IF NOT EXISTS for idempotency in case the columns were added via a prior startup script.
            migrationBuilder.Sql(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseType""  INTEGER NOT NULL DEFAULT 0");
            migrationBuilder.Sql(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseEntry"" INTEGER");
            migrationBuilder.Sql(@"ALTER TABLE ""InvoiceLines"" ADD COLUMN IF NOT EXISTS ""BaseLine""  INTEGER");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BaseType",  table: "InvoiceLines");
            migrationBuilder.DropColumn(name: "BaseEntry", table: "InvoiceLines");
            migrationBuilder.DropColumn(name: "BaseLine",  table: "InvoiceLines");
        }
    }
}
