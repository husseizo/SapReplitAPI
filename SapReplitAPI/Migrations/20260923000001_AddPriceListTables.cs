using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddPriceListTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PriceLists — SAP OPLN master data
            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    PriceListNum  = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceListName = table.Column<string>(type: "TEXT", nullable: false),
                    BasePriceList = table.Column<int>(type: "INTEGER", nullable: true),
                    Factor        = table.Column<decimal>(type: "decimal(18,6)", nullable: false, defaultValue: 1m),
                    Currency      = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive      = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    LastUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.PriceListNum);
                });

            // ItemPriceLists — SAP ITM1 normalized: one row per (ItemCode, PriceListNum)
            migrationBuilder.CreateTable(
                name: "ItemPriceLists",
                columns: table => new
                {
                    ItemCode      = table.Column<string>(type: "TEXT", nullable: false),
                    PriceListNum  = table.Column<int>(type: "INTEGER", nullable: false),
                    Price         = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Currency      = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPriceLists", x => new { x.ItemCode, x.PriceListNum });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemPriceLists_ItemCode",
                table: "ItemPriceLists",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_ItemPriceLists_PriceListNum",
                table: "ItemPriceLists",
                column: "PriceListNum");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ItemPriceLists");
            migrationBuilder.DropTable(name: "PriceLists");
        }
    }
}
