using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiAddressGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MessageIds",
                table: "SchedulePostThreads",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AddressId",
                table: "AddressChecks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BatchId",
                table: "AddressChecks",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BudgetCharged",
                table: "AddressChecks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChatAddresses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    City = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Street = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    House = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PartIds = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatAddresses_Channels_ChatId",
                        column: x => x.ChatId,
                        principalTable: "Channels",
                        principalColumn: "ChatId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatAddressGroups",
                columns: table => new
                {
                    AddressId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAddressGroups", x => new { x.AddressId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_ChatAddressGroups_ChatAddresses_AddressId",
                        column: x => x.AddressId,
                        principalTable: "ChatAddresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatAddresses_ChatId",
                table: "ChatAddresses",
                column: "ChatId");
            migrationBuilder.Sql("UPDATE [AddressChecks] SET [BudgetCharged] = 1;");
            var name = ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer" ? "N'Адреса'" : "'Адреса'";
            migrationBuilder.Sql($"""
                INSERT INTO [ChatAddresses] ([ChatId], [RegionId], [Name], [City], [Street], [House], [Label], [PartIds])
                SELECT [ChatId], [RegionId], {name}, [AddressCity], [AddressStreet], [AddressHouse], [AddressLabel], COALESCE([PartIds], [PartId])
                FROM [Channels]
                WHERE [AddressStreet] IS NOT NULL OR [AddressHouse] IS NOT NULL OR [AddressLabel] IS NOT NULL OR [GroupId] IS NOT NULL;
                INSERT INTO [ChatAddressGroups] ([AddressId], [GroupId], [Label])
                SELECT a.[Id], c.[GroupId], NULL FROM [ChatAddresses] a
                INNER JOIN [Channels] c ON c.[ChatId] = a.[ChatId]
                WHERE c.[GroupId] IS NOT NULL;
                UPDATE [AddressChecks] SET [AddressId] = (
                    SELECT a.[Id] FROM [ChatAddresses] a
                    WHERE a.[ChatId] = [AddressChecks].[ChatId] AND a.[RegionId] = [AddressChecks].[RegionId]
                        AND COALESCE(a.[City], '') = [AddressChecks].[City]
                        AND a.[Street] = [AddressChecks].[Street] AND a.[House] = [AddressChecks].[House]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatAddressGroups");

            migrationBuilder.DropTable(
                name: "ChatAddresses");

            migrationBuilder.DropColumn(
                name: "MessageIds",
                table: "SchedulePostThreads");

            migrationBuilder.DropColumn(
                name: "AddressId",
                table: "AddressChecks");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "AddressChecks");

            migrationBuilder.DropColumn(
                name: "BudgetCharged",
                table: "AddressChecks");
        }
    }
}
