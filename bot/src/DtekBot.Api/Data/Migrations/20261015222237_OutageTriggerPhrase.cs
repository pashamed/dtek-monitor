using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutageTriggerPhrase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutageTriggerPattern",
                table: "Channels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecentPosts",
                table: "Channels",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutageTriggerPattern",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RecentPosts",
                table: "Channels");
        }
    }
}
