using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseColumnsToCachedProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Whs_001",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Whs_002",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Whs_003",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Whs_004",
                table: "Products",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Whs_001",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Whs_002",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Whs_003",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Whs_004",
                table: "Products");
        }
    }
}