using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class RemoveU_MdlTEST_From_OrderLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "U_MdlTEST",
                table: "OrderLines",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "U_MdlTEST",
                table: "OrderLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");
        }
    }
}
