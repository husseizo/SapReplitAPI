using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedCustomerTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardCode = table.Column<string>(type: "TEXT", nullable: false),
                    CardName = table.Column<string>(type: "TEXT", nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerType = table.Column<string>(type: "TEXT", nullable: false),
                    SalesPersonName = table.Column<string>(type: "TEXT", nullable: false),
                    TotalSpent = table.Column<decimal>(type: "TEXT", nullable: false),
                    AddressesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
