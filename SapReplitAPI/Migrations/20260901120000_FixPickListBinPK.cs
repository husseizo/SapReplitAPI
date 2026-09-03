using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class FixPickListBinPK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── PickLists: add SlpCode / SlpName ────────────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "SlpCode",
                table: "PickLists",
                type: "INTEGER",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "SlpName",
                table: "PickLists",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // ── PickListBinAllocations: rebuild with (AbsEntry, PickEntry, Pkl2LinNum) PK
            // SQLite does not support ALTER PRIMARY KEY — must rename / recreate / copy / drop.

            // Step 1: drop existing indexes before rename
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_PickListBinAllocations_AbsEntry"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_PickListBinAllocations_BinAbsEntry"";");

            // Step 2: rename old table
            migrationBuilder.Sql(@"ALTER TABLE ""PickListBinAllocations"" RENAME TO ""PickListBinAllocations_old"";");

            // Step 3: create new table with correct PK and new columns
            migrationBuilder.CreateTable(
                name: "PickListBinAllocations",
                columns: table => new
                {
                    AbsEntry      = table.Column<int>(type: "INTEGER", nullable: false),
                    PickEntry     = table.Column<int>(type: "INTEGER", nullable: false),
                    Pkl2LinNum    = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderEntry    = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderLine     = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemCode      = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    WhsCode       = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    BinAbsEntry   = table.Column<int>(type: "INTEGER", nullable: false),
                    BinCode       = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    PickQtty      = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RelQtty       = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OpenCreQty    = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PickListName  = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    PickListStatus= table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    SlpCode       = table.Column<int>(type: "INTEGER", nullable: true, defaultValue: null),
                    SlpName       = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickListBinAllocations",
                        x => new { x.AbsEntry, x.PickEntry, x.Pkl2LinNum });
                });

            // Step 4: copy existing rows; new columns default to 0 / empty / null
            migrationBuilder.Sql(@"
INSERT INTO ""PickListBinAllocations""
    (AbsEntry, PickEntry, Pkl2LinNum, OrderEntry, OrderLine,
     ItemCode, WhsCode, BinAbsEntry, BinCode, PickQtty, RelQtty,
     OpenCreQty, PickListName, PickListStatus, SlpCode, SlpName)
SELECT AbsEntry, PickEntry, Pkl2LinNum, OrderEntry, OrderLine,
       COALESCE(ItemCode,''), COALESCE(WhsCode,''), BinAbsEntry, COALESCE(BinCode,''),
       PickQtty, RelQtty,
       0.0, '', '', NULL, ''
FROM ""PickListBinAllocations_old"";
");

            // Step 5: drop old table
            migrationBuilder.Sql(@"DROP TABLE ""PickListBinAllocations_old"";");

            // Step 6: recreate indexes on new table
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
            migrationBuilder.DropColumn(name: "SlpCode", table: "PickLists");
            migrationBuilder.DropColumn(name: "SlpName", table: "PickLists");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_PickListBinAllocations_AbsEntry"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_PickListBinAllocations_BinAbsEntry"";");
            migrationBuilder.Sql(@"ALTER TABLE ""PickListBinAllocations"" RENAME TO ""PickListBinAllocations_new"";");

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

            migrationBuilder.Sql(@"
INSERT INTO ""PickListBinAllocations""
    (AbsEntry, Pkl2LinNum, PickEntry, OrderEntry, OrderLine, ItemCode, WhsCode, BinAbsEntry, BinCode, PickQtty, RelQtty)
SELECT AbsEntry, Pkl2LinNum, PickEntry, OrderEntry, OrderLine, ItemCode, WhsCode, BinAbsEntry, BinCode, PickQtty, RelQtty
FROM ""PickListBinAllocations_new"";
");

            migrationBuilder.Sql(@"DROP TABLE ""PickListBinAllocations_new"";");

            migrationBuilder.CreateIndex(name: "IX_PickListBinAllocations_AbsEntry",   table: "PickListBinAllocations", column: "AbsEntry");
            migrationBuilder.CreateIndex(name: "IX_PickListBinAllocations_BinAbsEntry", table: "PickListBinAllocations", column: "BinAbsEntry");
        }
    }
}
