using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddU_MdlTESTToInvoiceLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "U_MDLTsT",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "U_Manufacturer",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "U_MdlTEST",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "U_Manufacturer",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "U_MdlTEST",
                table: "InvoiceLines");

            migrationBuilder.AlterColumn<string>(
                name: "U_MDLTsT",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");
        }
    }
}
