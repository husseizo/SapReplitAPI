using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedOpenOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpenOrderHeaders",
                columns: table => new
                {
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocNum = table.Column<int>(type: "INTEGER", nullable: false),
                    CardCode = table.Column<string>(type: "TEXT", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", nullable: false),
                    DocDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OrderTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SlpCode = table.Column<int>(type: "INTEGER", nullable: false),
                    SlpName = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenOrderHeaders", x => x.DocEntry);
                });

            migrationBuilder.CreateTable(
                name: "OpenOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNum = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                    Dscription = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WhsCode = table.Column<string>(type: "TEXT", nullable: false),
                    DocDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenOrderLines_OpenOrderHeaders_DocEntry",
                        column: x => x.DocEntry,
                        principalTable: "OpenOrderHeaders",
                        principalColumn: "DocEntry",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenOrderLines_DocEntry",
                table: "OpenOrderLines",
                column: "DocEntry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenOrderLines");

            migrationBuilder.DropTable(
                name: "OpenOrderHeaders");
        }
    }
}
