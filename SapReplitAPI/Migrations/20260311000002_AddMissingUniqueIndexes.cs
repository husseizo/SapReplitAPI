using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    [Migration("20260311000002_AddMissingUniqueIndexes")]
    public partial class AddMissingUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Customers.CardCode — required for ON CONFLICT(CardCode) in CustomerCacheService
            migrationBuilder.Sql(@"
DELETE FROM Customers
WHERE Id NOT IN (
    SELECT MAX(Id) FROM Customers GROUP BY CardCode
)");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_CardCode"" ON ""Customers"" (""CardCode"")");

            // OrderLines.(DocEntry, LineNum) — required for ON CONFLICT(DocEntry, LineNum) in OrderCacheService
            migrationBuilder.Sql(@"
DELETE FROM OrderLines
WHERE Id NOT IN (
    SELECT MAX(Id) FROM OrderLines GROUP BY DocEntry, LineNum
)");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_OrderLines_DocEntry_LineNum"" ON ""OrderLines"" (""DocEntry"", ""LineNum"")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Customers_CardCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_OrderLines_DocEntry_LineNum""");
        }
    }
}
