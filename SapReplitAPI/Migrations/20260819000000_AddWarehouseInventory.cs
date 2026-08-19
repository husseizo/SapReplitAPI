using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WarehouseInventory",
                columns: table => new
                {
                    Id              = table.Column<int>(type: "INTEGER",           nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemCode        = table.Column<string>(type: "TEXT",           nullable: false),
                    WhsCode         = table.Column<string>(type: "TEXT",           nullable: false),
                    WarehouseName   = table.Column<string>(type: "TEXT",           nullable: false),
                    OnHand          = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IsCommitted     = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OnOrder         = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AvailableToSell = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IsBinManaged    = table.Column<bool>(type: "INTEGER",          nullable: false, defaultValue: false),
                    LastUpdated     = table.Column<DateTime>(type: "TEXT",         nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WarehouseInventory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_WarehouseInventory_ItemCode_WhsCode",
                table: "WarehouseInventory",
                columns: new[] { "ItemCode", "WhsCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WarehouseInventory_ItemCode",
                table: "WarehouseInventory",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_WarehouseInventory_WhsCode",
                table: "WarehouseInventory",
                column: "WhsCode");

            migrationBuilder.CreateIndex(
                name: "IX_WarehouseInventory_LastUpdated",
                table: "WarehouseInventory",
                column: "LastUpdated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WarehouseInventory");
        }
    }
}
