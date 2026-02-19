using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class Add_U_Item_Name_To_CachedOrderLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "U_Item_Name",
                table: "OrderLines",
                type: "TEXT",
                nullable: true,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "U_MdlTEST",
                table: "OrderLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "U_Manufacturer",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "U_ItemName",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "U_Item_Name",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "U_MdlTEST",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "U_ItemName",
                table: "InvoiceLines");

            migrationBuilder.AlterColumn<string>(
                name: "U_Manufacturer",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");
        }
    }
}
