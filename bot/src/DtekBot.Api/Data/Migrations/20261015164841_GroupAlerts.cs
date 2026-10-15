using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressLabel",
                table: "Channels",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "Channels",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupStates",
                columns: table => new
                {
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Today = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tomorrow = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupStates", x => new { x.RegionId, x.GroupId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_GroupId",
                table: "Channels",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupStates");

            migrationBuilder.DropIndex(
                name: "IX_Channels_GroupId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "AddressLabel",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Channels");
        }
    }
}
