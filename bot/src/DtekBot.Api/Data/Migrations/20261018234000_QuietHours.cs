using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DtekBot.Api.Data.Migrations;

[DbContext(typeof(BotDbContext))]
[Migration("20261018234000_QuietHours")]
public sealed class QuietHours : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "QuietHoursEnabled",
            table: "Channels",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "QuietHoursStartMinute",
            table: "Channels",
            type: "int",
            nullable: false,
            defaultValue: 1320);

        migrationBuilder.AddColumn<int>(
            name: "QuietHoursEndMinute",
            table: "Channels",
            type: "int",
            nullable: false,
            defaultValue: 480);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "QuietHoursEnabled", table: "Channels");
        migrationBuilder.DropColumn(name: "QuietHoursStartMinute", table: "Channels");
        migrationBuilder.DropColumn(name: "QuietHoursEndMinute", table: "Channels");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        new BotDbContextModelSnapshot().BuildCurrentModel(modelBuilder);
    }
}
