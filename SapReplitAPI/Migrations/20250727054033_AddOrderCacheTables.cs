using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCacheTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Whs_004",
                table: "Products",
                type: "REAL",
                nullable: true,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Whs_003",
                table: "Products",
                type: "REAL",
                nullable: true,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Whs_002",
                table: "Products",
                type: "REAL",
                nullable: true,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Whs_001",
                table: "Products",
                type: "REAL",
                nullable: true,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "OrderHeaders",
                columns: table => new
                {
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DocNum = table.Column<int>(type: "INTEGER", nullable: false),
                    DocDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", nullable: false),
                    OrderValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderHeaders", x => x.DocEntry);
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                    Dscription = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "decimal(18,2)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WhsCode = table.Column<string>(type: "TEXT", nullable: false),
                    U_ItemName = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    U_Manufacturer = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderLines_OrderHeaders_DocEntry",
                        column: x => x.DocEntry,
                        principalTable: "OrderHeaders",
                        principalColumn: "DocEntry",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_DocEntry",
                table: "OrderLines",
                column: "DocEntry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderLines");

            migrationBuilder.DropTable(
                name: "OrderHeaders");

            migrationBuilder.AlterColumn<int>(
                name: "Whs_004",
                table: "Products",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "Whs_003",
                table: "Products",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "Whs_002",
                table: "Products",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "Whs_001",
                table: "Products",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 0);
        }
    }
}
