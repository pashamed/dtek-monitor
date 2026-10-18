using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddressCity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AddressResults",
                table: "AddressResults");

            migrationBuilder.AddColumn<string>(
                name: "AddressCity",
                table: "Channels",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "AddressResults",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "AddressChecks",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AddressResults",
                table: "AddressResults",
                columns: new[] { "RegionId", "City", "Street", "House" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AddressResults",
                table: "AddressResults");

            migrationBuilder.DropColumn(
                name: "AddressCity",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "City",
                table: "AddressResults");

            migrationBuilder.DropColumn(
                name: "City",
                table: "AddressChecks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AddressResults",
                table: "AddressResults",
                columns: new[] { "RegionId", "Street", "House" });
        }
    }
}
