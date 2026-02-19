using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class Add_InvoiceDocNum_To_CachedInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvoiceDocNum",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceDocNum",
                table: "Invoices");
        }
    }
}
