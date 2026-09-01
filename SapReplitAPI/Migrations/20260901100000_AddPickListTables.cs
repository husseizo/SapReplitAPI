using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddPickListTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PickLists",
                columns: table => new
                {
                    AbsEntry     = table.Column<int>(type: "INTEGER", nullable: false),
                    Name         = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    OwnerCode    = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerName    = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Status       = table.Column<string>(type: "TEXT", nullable: false),
                    Canceled     = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "N"),
                    Remarks      = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    PickDate     = table.Column<string>(type: "TEXT", nullable: false),
                    CreateDate   = table.Column<string>(type: "TEXT", nullable: false),
                    UpdateDate   = table.Column<string>(type: "TEXT", nullable: false),
                    U_ReplitId   = table.Column<string>(type: "TEXT", nullable: true, defaultValue: null),
                    LastSyncedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickLists", x => x.AbsEntry);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PickLists_Status",
                table: "PickLists",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PickLists_OwnerCode",
                table: "PickLists",
                column: "OwnerCode");

            migrationBuilder.CreateIndex(
                name: "IX_PickLists_UpdateDate",
                table: "PickLists",
                column: "UpdateDate");

            migrationBuilder.CreateTable(
                name: "PickListLines",
                columns: table => new
                {
                    AbsEntry       = table.Column<int>(type: "INTEGER", nullable: false),
                    PickEntry      = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderEntry     = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderLine      = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseObject     = table.Column<int>(type: "INTEGER", nullable: false),
                    RelQtty        = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PickQtty       = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PickStatus     = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    PrevReleas     = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ItemCode       = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Dscription     = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    WhsCode        = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    SourceSoDocNum = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickListLines", x => new { x.AbsEntry, x.PickEntry });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PickListLines_AbsEntry",
                table: "PickListLines",
                column: "AbsEntry");

            migrationBuilder.CreateIndex(
                name: "IX_PickListLines_OrderEntry",
                table: "PickListLines",
                column: "OrderEntry");

            migrationBuilder.CreateTable(
                name: "PickListBinAllocations",
                columns: table => new
                {
                    AbsEntry    = table.Column<int>(type: "INTEGER", nullable: false),
                    Pkl2LinNum  = table.Column<int>(type: "INTEGER", nullable: false),
                    PickEntry   = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderEntry  = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderLine   = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode    = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    WhsCode     = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    BinAbsEntry = table.Column<int>(type: "INTEGER", nullable: false),
                    BinCode     = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    PickQtty    = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RelQtty     = table.Column<decimal>(type: "decimal(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickListBinAllocations", x => new { x.AbsEntry, x.Pkl2LinNum });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PickListBinAllocations_AbsEntry",
                table: "PickListBinAllocations",
                column: "AbsEntry");

            migrationBuilder.CreateIndex(
                name: "IX_PickListBinAllocations_BinAbsEntry",
                table: "PickListBinAllocations",
                column: "BinAbsEntry");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PickListBinAllocations");
            migrationBuilder.DropTable(name: "PickListLines");
            migrationBuilder.DropTable(name: "PickLists");
        }
    }
}
