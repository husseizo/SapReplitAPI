using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <summary>
    /// Adds OITM.U_OE_Numbers as a nullable, verbatim mirror column on the SQLite
    /// Products cache table. Nullable, no default value — NULL must stay NULL,
    /// existing rows are unaffected (SQLite ADD COLUMN backfills NULL for existing
    /// rows, which is correct here since we never fabricate a default).
    /// </summary>
    public partial class AddUOENumbersToProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "U_OE_Numbers",
                table: "Products",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "U_OE_Numbers",
                table: "Products");
        }
    }
}
