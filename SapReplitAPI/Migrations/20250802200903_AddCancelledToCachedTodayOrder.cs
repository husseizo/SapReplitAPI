using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCancelledToCachedTodayOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationStatus",
                table: "OrderHeaders",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "TodayOrderHeaders",
                columns: table => new
                {
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocNum = table.Column<int>(type: "INTEGER", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", nullable: false),
                    DocDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Cancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrderValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SlpCode = table.Column<int>(type: "INTEGER", nullable: true),
                    SlpName = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodayOrderHeaders", x => x.DocEntry);
                });

            migrationBuilder.CreateTable(
                name: "TodayOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                    Dscription = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WhsCode = table.Column<string>(type: "TEXT", nullable: false),
                    U_ItemName = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    U_Manufacturer = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    DocDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodayOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodayOrderLines_TodayOrderHeaders_DocEntry",
                        column: x => x.DocEntry,
                        principalTable: "TodayOrderHeaders",
                        principalColumn: "DocEntry",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodayOrderLines_DocEntry",
                table: "TodayOrderLines",
                column: "DocEntry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodayOrderLines");

            migrationBuilder.DropTable(
                name: "TodayOrderHeaders");

            migrationBuilder.DropColumn(
                name: "CancellationStatus",
                table: "OrderHeaders");
        }
    }
}
