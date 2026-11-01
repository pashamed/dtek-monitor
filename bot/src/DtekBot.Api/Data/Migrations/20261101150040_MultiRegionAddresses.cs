using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiRegionAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ScheduleThreads",
                table: "ScheduleThreads");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads");

            migrationBuilder.AddColumn<long>(
                name: "SvitlobotAddressId",
                table: "Channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SvitlobotRegionId",
                table: "Channels",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ScheduleThreads",
                table: "ScheduleThreads",
                columns: new[] { "ChatId", "RegionId", "GroupId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads",
                columns: new[] { "ChatId", "RegionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ScheduleThreads",
                table: "ScheduleThreads");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads");

            migrationBuilder.DropColumn(
                name: "SvitlobotAddressId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SvitlobotRegionId",
                table: "Channels");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ScheduleThreads",
                table: "ScheduleThreads",
                column: "ChatId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads",
                column: "ChatId");
        }
    }
}
