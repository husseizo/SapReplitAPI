using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class Remove_U_Item_Name_From_OrderLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "U_Item_Name",
                table: "OrderLines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "U_Item_Name",
                table: "OrderLines",
                type: "TEXT",
                nullable: true,
                defaultValue: "");
        }
    }
}
