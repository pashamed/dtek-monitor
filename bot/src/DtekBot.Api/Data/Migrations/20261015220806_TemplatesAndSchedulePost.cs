using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplatesAndSchedulePost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PartIds",
                table: "Channels",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleEventMode",
                table: "Channels",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Edit");

            migrationBuilder.AddColumn<bool>(
                name: "SchedulePost",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SchedulePostImage",
                table: "Channels",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Grid");

            migrationBuilder.AddColumn<string>(
                name: "SchedulePostMode",
                table: "Channels",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<string>(
                name: "Templates",
                table: "Channels",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SchedulePostThreads",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MessageId = table.Column<int>(type: "int", nullable: false),
                    IsPhoto = table.Column<bool>(type: "bit", nullable: false),
                    DateKey = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageKind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulePostThreads", x => x.ChatId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchedulePostThreads");

            migrationBuilder.DropColumn(
                name: "PartIds",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ScheduleEventMode",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SchedulePost",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SchedulePostImage",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SchedulePostMode",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Templates",
                table: "Channels");
        }
    }
}
