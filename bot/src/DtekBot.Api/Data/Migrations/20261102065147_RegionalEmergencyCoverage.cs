using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RegionalEmergencyCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmergencyPartsJson",
                table: "Deliveries",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmergencyPartsJson",
                table: "Deliveries");
        }
    }
}
