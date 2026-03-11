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

            migrationBuilder.CreateIndex(
                name: "IX_Products_ItemCode",
                table: "Products",
                column: "ItemCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_ItemCode",
                table: "Products");
        }
    }
}
