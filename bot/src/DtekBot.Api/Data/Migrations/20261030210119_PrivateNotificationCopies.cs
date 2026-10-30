using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrivateNotificationCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivateNotificationCopies",
                columns: table => new
                {
                    SourceChatId = table.Column<long>(type: "bigint", nullable: false),
                    SourceMessageId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivateNotificationCopies", x => new { x.SourceChatId, x.SourceMessageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PrivateNotificationCopies_ChannelAdmins_SourceChatId_UserId",
                        columns: x => new { x.SourceChatId, x.UserId },
                        principalTable: "ChannelAdmins",
                        principalColumns: new[] { "ChatId", "UserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivateNotificationCopies_SourceChatId_UserId",
                table: "PrivateNotificationCopies",
                columns: new[] { "SourceChatId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivateNotificationCopies");
        }
    }
}
