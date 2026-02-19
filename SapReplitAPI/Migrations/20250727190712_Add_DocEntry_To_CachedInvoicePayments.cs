using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class Add_DocEntry_To_CachedInvoicePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SalesEmployeeName",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "SalesEmployeeCode",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "DebitAccountName",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "DebitAccountCode",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "BankTransferReference",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "DocEntry",
                table: "InvoicePayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocEntry",
                table: "InvoicePayments");

            migrationBuilder.AlterColumn<string>(
                name: "SalesEmployeeName",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "SalesEmployeeCode",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "DebitAccountName",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "DebitAccountCode",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "BankTransferReference",
                table: "InvoicePayments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "");
        }
    }
}
