using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ScheduleReminder",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleReminderMinutes",
                table: "Channels",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateTable(
                name: "ScheduleReminderDeliveries",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OutageStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleReminderDeliveries", x => new { x.ChatId, x.RegionId, x.GroupId, x.OutageStart });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleReminderDeliveries");

            migrationBuilder.DropColumn(
                name: "ScheduleReminder",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ScheduleReminderMinutes",
                table: "Channels");
        }
    }
}
