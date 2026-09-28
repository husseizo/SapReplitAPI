using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    /// <summary>
    /// Creates dbo.NeonMirrorWork (SQLite) — durable "Neon needs refreshing for this
    /// entity" intent, written in the same SQLite transaction as the inventory cache
    /// mutation that produced it. No existing table/column touched.
    /// </summary>
    public partial class AddNeonMirrorWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NeonMirrorWork",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EntityKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NeonMirrorWork", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NeonMirrorWork_EntityType_EntityKey",
                table: "NeonMirrorWork",
                columns: new[] { "EntityType", "EntityKey" });

            migrationBuilder.CreateIndex(
                name: "IX_NeonMirrorWork_Status_Priority_CreatedAtUtc",
                table: "NeonMirrorWork",
                columns: new[] { "Status", "Priority", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NeonMirrorWork");
        }
    }
}
