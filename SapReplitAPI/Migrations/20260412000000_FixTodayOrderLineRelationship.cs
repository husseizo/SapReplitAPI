using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class FixTodayOrderLineRelationship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the foreign key constraint if it exists
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_TodayOrderLines_CachedTodayOrderDocEntry"";
            ");

            // Drop the shadow property column if it exists
            migrationBuilder.Sql(@"
                PRAGMA foreign_keys = OFF;

                CREATE TABLE IF NOT EXISTS ""TodayOrderLines_temp"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_TodayOrderLines"" PRIMARY KEY AUTOINCREMENT,
                    ""DocEntry"" INTEGER NOT NULL,
                    ""ItemCode"" TEXT NOT NULL,
                    ""Dscription"" TEXT NOT NULL,
                    ""Quantity"" decimal(18, 2) NOT NULL,
                    ""Price"" decimal(18, 2) NOT NULL,
                    ""WhsCode"" TEXT NOT NULL,
                    ""U_ItemName"" TEXT NOT NULL DEFAULT '',
                    ""U_Manufacturer"" TEXT NOT NULL DEFAULT '',
                    ""DocDate"" TEXT NOT NULL,
                    CONSTRAINT ""FK_TodayOrderLines_TodayOrderHeaders_DocEntry"" FOREIGN KEY (""DocEntry"") REFERENCES ""TodayOrderHeaders"" (""DocEntry"") ON DELETE CASCADE
                );

                INSERT INTO ""TodayOrderLines_temp"" 
                    (""Id"", ""DocEntry"", ""ItemCode"", ""Dscription"", ""Quantity"", ""Price"", ""WhsCode"", ""U_ItemName"", ""U_Manufacturer"", ""DocDate"")
                SELECT 
                    ""Id"", ""DocEntry"", ""ItemCode"", ""Dscription"", ""Quantity"", ""Price"", ""WhsCode"", ""U_ItemName"", ""U_Manufacturer"", ""DocDate""
                FROM ""TodayOrderLines"";

                DROP TABLE ""TodayOrderLines"";
                ALTER TABLE ""TodayOrderLines_temp"" RENAME TO ""TodayOrderLines"";

                CREATE INDEX IF NOT EXISTS ""IX_TodayOrderLines_DocEntry"" ON ""TodayOrderLines"" (""DocEntry"");

                PRAGMA foreign_keys = ON;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-add the shadow property column
            migrationBuilder.AddColumn<int>(
                name: "CachedTodayOrderDocEntry",
                table: "TodayOrderLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TodayOrderLines_CachedTodayOrderDocEntry",
                table: "TodayOrderLines",
                column: "CachedTodayOrderDocEntry");

            migrationBuilder.AddForeignKey(
                name: "FK_TodayOrderLines_TodayOrderHeaders_CachedTodayOrderDocEntry",
                table: "TodayOrderLines",
                column: "CachedTodayOrderDocEntry",
                principalTable: "TodayOrderHeaders",
                principalColumn: "DocEntry");
        }
    }
}
