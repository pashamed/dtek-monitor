using DtekBot.Api.Parsing;
using Microsoft.EntityFrameworkCore;

namespace DtekBot.Api.Data;

public sealed class Channel
{
    public long ChatId { get; set; }
    public string RegionId { get; set; } = "";
    public string? PartId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RegionState
{
    public string RegionId { get; set; } = "";
    public PopupKind Kind { get; set; }
    public string NormalizedText { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Delivery
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public string RegionId { get; set; } = "";
    public int TelegramMessageId { get; set; }
    public string ContentHash { get; set; } = "";
    public bool IsClosed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<RegionState> RegionStates => Set<RegionState>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>(e =>
        {
            e.HasKey(x => x.ChatId);
            e.Property(x => x.ChatId).ValueGeneratedNever();
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.PartId).HasMaxLength(64);
            e.HasIndex(x => new { x.RegionId, x.IsActive });
        });

        modelBuilder.Entity<RegionState>(e =>
        {
            e.HasKey(x => x.RegionId);
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Hash).HasMaxLength(64);
        });

        modelBuilder.Entity<Delivery>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.HasIndex(x => new { x.ChatId, x.RegionId });
        });
    }
}
