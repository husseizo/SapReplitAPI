using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddBaseRefsToInvoiceLines : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseType",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BaseEntry",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BaseLine",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BaseType",  table: "InvoiceLines");
            migrationBuilder.DropColumn(name: "BaseEntry", table: "InvoiceLines");
            migrationBuilder.DropColumn(name: "BaseLine",  table: "InvoiceLines");
        }
    }
}
