using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DtekBot.Api.Data;

// Used only by `dotnet ef`; the real connection string comes from host configuration.
public sealed class BotDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=DtekBot;Trusted_Connection=True;")
            .Options;
        return new BotDbContext(options);
    }
}
