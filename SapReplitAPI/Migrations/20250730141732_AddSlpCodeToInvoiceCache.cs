using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddSlpCodeToInvoiceCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SlpCode",
                table: "DetailedInvoiceStatusCache",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SlpCode",
                table: "DetailedInvoiceStatusCache");
        }
    }
}
