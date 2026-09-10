using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddPickListLineTimestamps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime?>(
                name: "CreatedTime",
                table: "PickListLines",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<DateTime?>(
                name: "PickedTime",
                table: "PickListLines",
                type: "TEXT",
                nullable: true,
                defaultValue: null);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CreatedTime", table: "PickListLines");
            migrationBuilder.DropColumn(name: "PickedTime",  table: "PickListLines");
        }
    }
}
