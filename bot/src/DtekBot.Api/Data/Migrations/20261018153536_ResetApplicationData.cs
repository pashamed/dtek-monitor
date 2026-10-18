using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResetApplicationData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM [ScheduleReminderDeliveries];
                DELETE FROM [AddressResults];
                DELETE FROM [AddressChecks];
                DELETE FROM [ChannelAdmins];
                DELETE FROM [Deliveries];
                DELETE FROM [GridStates];
                DELETE FROM [GroupStates];
                DELETE FROM [KeyValues];
                DELETE FROM [RegionStates];
                DELETE FROM [SchedulePostThreads];
                DELETE FROM [ScheduleThreads];
                DELETE FROM [Channels];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
