using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddZfFieldsToCachedDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZoneRef",
                table: "Deliveries",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryLocation",
                table: "Deliveries",
                type: "TEXT",
                nullable: true,
                defaultValue: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ZoneRef",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "DeliveryLocation",
                table: "Deliveries");
        }
    }
}
