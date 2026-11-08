using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertCleanupTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClearPendingSince",
                table: "RegionStates",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EndMessageId",
                table: "Deliveries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAt",
                table: "Deliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUpdated",
                table: "AddressResults",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClearPendingSince",
                table: "RegionStates");

            migrationBuilder.DropColumn(
                name: "EndMessageId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "SourceUpdated",
                table: "AddressResults");
        }
    }
}
