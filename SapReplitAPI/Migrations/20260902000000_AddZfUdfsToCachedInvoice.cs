using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddZfUdfsToCachedInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZoneRef",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "U_ReplitId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryLocation",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ZoneRef",
                table: "Invoices",
                column: "ZoneRef");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_ZoneRef",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryLocation",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "U_ReplitId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ZoneRef",
                table: "Invoices");
        }
    }
}
