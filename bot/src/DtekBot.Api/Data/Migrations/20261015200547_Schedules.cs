using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Schedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleImage",
                table: "Channels",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Grid");

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleNotify",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ScheduleThreads",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MessageId = table.Column<int>(type: "int", nullable: false),
                    IsPhoto = table.Column<bool>(type: "bit", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    EpisodeStart = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageKind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleThreads", x => x.ChatId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleThreads");

            migrationBuilder.DropColumn(
                name: "ScheduleImage",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ScheduleNotify",
                table: "Channels");
        }
    }
}
