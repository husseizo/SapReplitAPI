using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    DocEntry     = table.Column<int>(type: "INTEGER", nullable: false),
                    DocNum       = table.Column<int>(type: "INTEGER", nullable: false),
                    DocDate      = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocDueDate   = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TaxDate      = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocStatus    = table.Column<string>(type: "TEXT", nullable: false),
                    Canceled     = table.Column<string>(type: "TEXT", nullable: false),
                    CardCode     = table.Column<string>(type: "TEXT", nullable: false),
                    CardName     = table.Column<string>(type: "TEXT", nullable: false),
                    DocTotal     = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DocCur       = table.Column<string>(type: "TEXT", nullable: false),
                    SlpCode      = table.Column<int>(type: "INTEGER", nullable: false),
                    SlpName      = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    UserSign     = table.Column<int>(type: "INTEGER", nullable: false),
                    Comments     = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    CreateDate   = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateTS     = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdateDate   = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateTS     = table.Column<int>(type: "INTEGER", nullable: false),
                    BPLId        = table.Column<int>(type: "INTEGER", nullable: false),
                    U_ReplitId   = table.Column<string>(type: "TEXT", nullable: true),
                    DocStatusDisplay = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.DocEntry);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryLines",
                columns: table => new
                {
                    DocEntry    = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNum     = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode    = table.Column<string>(type: "TEXT", nullable: false),
                    Dscription  = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Quantity    = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OpenQty     = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    WhsCode     = table.Column<string>(type: "TEXT", nullable: false),
                    Price       = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal   = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency    = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    BaseType    = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseEntry   = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseLine    = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetType  = table.Column<int>(type: "INTEGER", nullable: false),
                    TrgetEntry  = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryLines", x => new { x.DocEntry, x.LineNum });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_CardCode",
                table: "Deliveries",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_DocDate",
                table: "Deliveries",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_DocNum",
                table: "Deliveries",
                column: "DocNum");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_DocStatus_Canceled",
                table: "Deliveries",
                columns: new[] { "DocStatus", "Canceled" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLines_DocEntry",
                table: "DeliveryLines",
                column: "DocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLines_ItemCode",
                table: "DeliveryLines",
                column: "ItemCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DeliveryLines");
            migrationBuilder.DropTable(name: "Deliveries");
        }
    }
}
