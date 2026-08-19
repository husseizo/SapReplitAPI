using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddBinInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BinInventory",
                columns: table => new
                {
                    Id          = table.Column<int>(type: "INTEGER",           nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemCode    = table.Column<string>(type: "TEXT",           nullable: false),
                    WhsCode     = table.Column<string>(type: "TEXT",           nullable: false),
                    BinAbsEntry = table.Column<int>(type: "INTEGER",           nullable: false),
                    BinCode     = table.Column<string>(type: "TEXT",           nullable: false),
                    BinOnHand   = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT",         nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BinInventory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_BinInventory_ItemCode_WhsCode_BinAbsEntry",
                table: "BinInventory",
                columns: new[] { "ItemCode", "WhsCode", "BinAbsEntry" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BinInventory_ItemCode",
                table: "BinInventory",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_BinInventory_WhsCode",
                table: "BinInventory",
                column: "WhsCode");

            migrationBuilder.CreateIndex(
                name: "IX_BinInventory_ItemCode_WhsCode",
                table: "BinInventory",
                columns: new[] { "ItemCode", "WhsCode" });

            migrationBuilder.CreateIndex(
                name: "IX_BinInventory_LastUpdated",
                table: "BinInventory",
                column: "LastUpdated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BinInventory");
        }
    }
}
