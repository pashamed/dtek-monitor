using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddressChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressHouse",
                table: "Channels",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressStreet",
                table: "Channels",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AddressChecks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Street = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    House = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AddressResults",
                columns: table => new
                {
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Street = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    House = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Found = table.Column<bool>(type: "bit", nullable: false),
                    SubType = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    StartDate = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EndDate = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressResults", x => new { x.RegionId, x.Street, x.House });
                });

            migrationBuilder.CreateTable(
                name: "KeyValues",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyValues", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AddressChecks_ChatId_RequestedAt",
                table: "AddressChecks",
                columns: new[] { "ChatId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AddressChecks_Status_RegionId",
                table: "AddressChecks",
                columns: new[] { "Status", "RegionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddressChecks");

            migrationBuilder.DropTable(
                name: "AddressResults");

            migrationBuilder.DropTable(
                name: "KeyValues");

            migrationBuilder.DropColumn(
                name: "AddressHouse",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "AddressStreet",
                table: "Channels");
        }
    }
}
