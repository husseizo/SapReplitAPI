using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    [Migration("20260311000003_AddCancellationStatusDefault")]
    public partial class AddCancellationStatusDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The DropOrderHeadersId migration rebuilt the OrderHeaders table using EF's model
            // definitions, which did not include HasDefaultValue("") for CancellationStatus.
            // This stripped the DEFAULT '' that was originally set by AddCancelledToCachedTodayOrder.
            // As a result the column became NOT NULL with no default, breaking raw SQL UPSERTs
            // that omitted the column.
            //
            // SQLite does not support ALTER TABLE ALTER COLUMN, so we rebuild the table.
            // PRAGMA foreign_keys = 0 suppresses FK enforcement during the rebuild.

            migrationBuilder.Sql("PRAGMA foreign_keys = 0;");

            migrationBuilder.Sql(@"
CREATE TABLE ""OrderHeaders_new"" (
    ""DocEntry""           INTEGER      NOT NULL CONSTRAINT ""PK_OrderHeaders"" PRIMARY KEY,
    ""DocNum""             INTEGER      NOT NULL,
    ""DocDate""            TEXT         NOT NULL,
    ""CardName""           TEXT         NOT NULL,
    ""OrderValue""         decimal(18,2) NOT NULL,
    ""Status""             TEXT         NOT NULL,
    ""SlpCode""            INTEGER,
    ""SlpName""            TEXT         NOT NULL DEFAULT '',
    ""CancellationStatus"" TEXT         NOT NULL DEFAULT ''
);");

            migrationBuilder.Sql(@"
INSERT INTO ""OrderHeaders_new""
    (""DocEntry"", ""DocNum"", ""DocDate"", ""CardName"",
     ""OrderValue"", ""Status"", ""SlpCode"", ""SlpName"", ""CancellationStatus"")
SELECT
    ""DocEntry"", ""DocNum"", ""DocDate"", ""CardName"",
    ""OrderValue"", ""Status"", ""SlpCode"", ""SlpName"", ""CancellationStatus""
FROM ""OrderHeaders"";");

            migrationBuilder.Sql(@"DROP TABLE ""OrderHeaders"";");
            migrationBuilder.Sql(@"ALTER TABLE ""OrderHeaders_new"" RENAME TO ""OrderHeaders"";");

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the DEFAULT '' — rebuild without it.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;");

            migrationBuilder.Sql(@"
CREATE TABLE ""OrderHeaders_old"" (
    ""DocEntry""           INTEGER      NOT NULL CONSTRAINT ""PK_OrderHeaders"" PRIMARY KEY,
    ""DocNum""             INTEGER      NOT NULL,
    ""DocDate""            TEXT         NOT NULL,
    ""CardName""           TEXT         NOT NULL,
    ""OrderValue""         decimal(18,2) NOT NULL,
    ""Status""             TEXT         NOT NULL,
    ""SlpCode""            INTEGER,
    ""SlpName""            TEXT         NOT NULL DEFAULT '',
    ""CancellationStatus"" TEXT         NOT NULL
);");

            migrationBuilder.Sql(@"
INSERT INTO ""OrderHeaders_old""
    (""DocEntry"", ""DocNum"", ""DocDate"", ""CardName"",
     ""OrderValue"", ""Status"", ""SlpCode"", ""SlpName"", ""CancellationStatus"")
SELECT
    ""DocEntry"", ""DocNum"", ""DocDate"", ""CardName"",
    ""OrderValue"", ""Status"", ""SlpCode"", ""SlpName"", ""CancellationStatus""
FROM ""OrderHeaders"";");

            migrationBuilder.Sql(@"DROP TABLE ""OrderHeaders"";");
            migrationBuilder.Sql(@"ALTER TABLE ""OrderHeaders_old"" RENAME TO ""OrderHeaders"";");

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;");
        }
    }
}
