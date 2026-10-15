using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PopupText",
                table: "RegionStates",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EditInPlace",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Filter",
                table: "Channels",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<bool>(
                name: "IsPaused",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnEnd",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Template",
                table: "Channels",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Channels",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelAdmins",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelAdmins", x => new { x.ChatId, x.UserId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelAdmins_UserId",
                table: "ChannelAdmins",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelAdmins");

            migrationBuilder.DropColumn(
                name: "PopupText",
                table: "RegionStates");

            migrationBuilder.DropColumn(
                name: "EditInPlace",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Filter",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "IsPaused",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "NotifyOnEnd",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Template",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Channels");
        }
    }
}
