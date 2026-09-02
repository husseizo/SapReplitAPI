using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SapReplitAPI.Migrations
{
    public partial class AddPickListZoneTraceability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PickLists: ZoneRef, DeliveryLocation (header-level aggregate from ORDR UDFs)
            migrationBuilder.AddColumn<string>(
                name: "ZoneRef",
                table: "PickLists",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryLocation",
                table: "PickLists",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            // PickListLines: ZoneRef, DeliveryLocation, U_ReplitId (line-level, from ORDR.OrderEntry)
            migrationBuilder.AddColumn<string>(
                name: "ZoneRef",
                table: "PickListLines",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryLocation",
                table: "PickListLines",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "U_ReplitId",
                table: "PickListLines",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            // PickListBinAllocations: ZoneRef, DeliveryLocation, U_ReplitId (bin-level, via PKL1.PickEntry→OrderEntry)
            migrationBuilder.AddColumn<string>(
                name: "ZoneRef",
                table: "PickListBinAllocations",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryLocation",
                table: "PickListBinAllocations",
                type: "TEXT",
                nullable: true,
                defaultValue: null);

            migrationBuilder.AddColumn<string>(
                name: "U_ReplitId",
                table: "PickListBinAllocations",
                type: "TEXT",
                nullable: true,
                defaultValue: null);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ZoneRef",          table: "PickLists");
            migrationBuilder.DropColumn(name: "DeliveryLocation", table: "PickLists");
            migrationBuilder.DropColumn(name: "ZoneRef",          table: "PickListLines");
            migrationBuilder.DropColumn(name: "DeliveryLocation", table: "PickListLines");
            migrationBuilder.DropColumn(name: "U_ReplitId",       table: "PickListLines");
            migrationBuilder.DropColumn(name: "ZoneRef",          table: "PickListBinAllocations");
            migrationBuilder.DropColumn(name: "DeliveryLocation", table: "PickListBinAllocations");
            migrationBuilder.DropColumn(name: "U_ReplitId",       table: "PickListBinAllocations");
        }
    }
}
