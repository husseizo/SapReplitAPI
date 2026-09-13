using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditMemoCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreditMemoHeaders",
                columns: table => new
                {
                    DocEntry   = table.Column<int>(type: "INTEGER", nullable: false),
                    DocNum     = table.Column<int>(type: "INTEGER", nullable: false),
                    CardCode   = table.Column<string>(type: "TEXT", nullable: false),
                    CardName   = table.Column<string>(type: "TEXT", nullable: false),
                    DocDate    = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocDueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocStatus  = table.Column<string>(type: "TEXT", nullable: false),
                    Canceled   = table.Column<string>(type: "TEXT", nullable: false),
                    DocTotal   = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Comments   = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    SlpCode    = table.Column<int>(type: "INTEGER", nullable: false),
                    SlpName    = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    U_AppRef   = table.Column<string>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditMemoHeaders", x => x.DocEntry);
                });

            migrationBuilder.CreateTable(
                name: "CreditMemoLines",
                columns: table => new
                {
                    Id        = table.Column<int>(type: "INTEGER", nullable: false)
                                    .Annotation("Sqlite:Autoincrement", true),
                    DocEntry  = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNum   = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode  = table.Column<string>(type: "TEXT", nullable: false),
                    Dscription= table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Quantity  = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Price     = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    WhsCode   = table.Column<string>(type: "TEXT", nullable: false),
                    BaseType  = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseLine  = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditMemoLines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoHeaders_CardCode",
                table: "CreditMemoHeaders",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoHeaders_DocDate",
                table: "CreditMemoHeaders",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoLines_DocEntry",
                table: "CreditMemoLines",
                column: "DocEntry");

            migrationBuilder.CreateIndex(
                name: "UX_CreditMemoLines_DocEntry_LineNum",
                table: "CreditMemoLines",
                columns: new[] { "DocEntry", "LineNum" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CreditMemoHeaders");
            migrationBuilder.DropTable(name: "CreditMemoLines");
        }
    }
}
