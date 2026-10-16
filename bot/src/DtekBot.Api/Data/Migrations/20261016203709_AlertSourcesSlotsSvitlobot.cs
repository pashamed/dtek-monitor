using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertSourcesSlotsSvitlobot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PartIds",
                table: "RegionStates",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlotSettings",
                table: "Channels",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SvitlobotKey",
                table: "Channels",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GridStates",
                columns: table => new
                {
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Since = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FeedGeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GridStates", x => x.RegionId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GridStates");

            migrationBuilder.DropColumn(
                name: "PartIds",
                table: "RegionStates");

            migrationBuilder.DropColumn(
                name: "SlotSettings",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SvitlobotKey",
                table: "Channels");
        }
    }
}
