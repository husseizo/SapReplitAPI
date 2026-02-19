using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddTodayOrdersTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CachedTodayOrderDocEntry",
                table: "TodayOrderLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TodayOrderLines_CachedTodayOrderDocEntry",
                table: "TodayOrderLines",
                column: "CachedTodayOrderDocEntry");

            migrationBuilder.AddForeignKey(
                name: "FK_TodayOrderLines_TodayOrderHeaders_CachedTodayOrderDocEntry",
                table: "TodayOrderLines",
                column: "CachedTodayOrderDocEntry",
                principalTable: "TodayOrderHeaders",
                principalColumn: "DocEntry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TodayOrderLines_TodayOrderHeaders_CachedTodayOrderDocEntry",
                table: "TodayOrderLines");

            migrationBuilder.DropIndex(
                name: "IX_TodayOrderLines_CachedTodayOrderDocEntry",
                table: "TodayOrderLines");

            migrationBuilder.DropColumn(
                name: "CachedTodayOrderDocEntry",
                table: "TodayOrderLines");
        }
    }
}
