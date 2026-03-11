using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexToProductsItemCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove duplicate rows first (keep the most recently updated row per ItemCode)
            migrationBuilder.Sql(@"
DELETE FROM Products
WHERE Id NOT IN (
    SELECT MAX(Id)
    FROM Products
    GROUP BY ItemCode
)");

            // IF NOT EXISTS prevents failure if the index was already created manually
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Products_ItemCode"" ON ""Products"" (""ItemCode"")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Products_ItemCode""");
        }
    }
}
