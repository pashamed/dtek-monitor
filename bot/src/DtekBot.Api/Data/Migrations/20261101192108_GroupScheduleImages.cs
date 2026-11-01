using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupScheduleImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads");

            migrationBuilder.Sql("UPDATE [SchedulePostThreads] SET [GroupId] = '' WHERE [MessageIds] IS NOT NULL;");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads",
                columns: new[] { "ChatId", "RegionId", "GroupId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SchedulePostThreads",
                table: "SchedulePostThreads",
                columns: new[] { "ChatId", "RegionId" });
        }
    }
}
