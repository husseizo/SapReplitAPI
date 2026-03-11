using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    [Migration("20260310000001_DropOrderHeadersId")]
    public partial class DropOrderHeadersId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove the stray Id column from OrderHeaders.
            // DocEntry is and always was the primary key for this table.
            // Id was a leftover non-PK NOT NULL column that broke raw SQL upserts
            // because no value was supplied for it (NOT NULL constraint failed: OrderHeaders.Id).
            migrationBuilder.DropColumn(
                name: "Id",
                table: "OrderHeaders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "OrderHeaders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
