using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261019105400_AddressDirectories")]
public sealed class AddressDirectories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AddressDirectories",
            columns: table => new
            {
                RegionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Data = table.Column<string>(type: "nvarchar(max)", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_AddressDirectories", x => x.RegionId));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AddressDirectories");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        new BotDbContextModelSnapshot().BuildCurrentModel(modelBuilder);
}
