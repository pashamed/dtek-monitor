using DtekBot.Api.Parsing;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;

namespace DtekBot.Api.Data;

public sealed class Channel
{
    public long ChatId { get; set; }
    public string RegionId { get; set; } = "";
    public string? PartId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public string? Title { get; set; }
    public bool IsPaused { get; set; }
    public AlertFilter Filter { get; set; } = AlertFilter.All;
    public bool NotifyOnEnd { get; set; } = true;
    public bool EditInPlace { get; set; } = true;
    public string? Template { get; set; }

    // Power group such as "45.1", resolved from the address the user picked.
    public string? GroupId { get; set; }
    public string? AddressLabel { get; set; }
}

// Latest known status of one power group, as reported by the Yasno planned-outages feed.
public sealed class GroupState
{
    public string RegionId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Today { get; set; } = "";
    public string Tomorrow { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsEmergency => Today == GroupStatuses.Emergency;
}

public static class GroupStatuses
{
    public const string Emergency = "EmergencyShutdowns";
}

// Which Telegram users may manage a group or channel from the Mini App.
public sealed class ChannelAdmin
{
    public long ChatId { get; set; }
    public long UserId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class RegionState
{
    public string RegionId { get; set; } = "";
    public PopupKind Kind { get; set; }
    public string NormalizedText { get; set; } = "";
    public string? PopupText { get; set; }
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
    public DbSet<ChannelAdmin> ChannelAdmins => Set<ChannelAdmin>();
    public DbSet<GroupState> GroupStates => Set<GroupState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupState>(e =>
        {
            e.HasKey(x => new { x.RegionId, x.GroupId });
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.GroupId).HasMaxLength(16);
            e.Property(x => x.Today).HasMaxLength(64);
            e.Property(x => x.Tomorrow).HasMaxLength(64);
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.HasKey(x => x.ChatId);
            e.Property(x => x.ChatId).ValueGeneratedNever();
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.PartId).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Filter).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Template).HasMaxLength(MessageTemplate.MaxLength);
            e.Property(x => x.GroupId).HasMaxLength(16);
            e.Property(x => x.AddressLabel).HasMaxLength(256);
            e.HasIndex(x => new { x.RegionId, x.IsActive });
            e.HasIndex(x => x.GroupId);
        });

        modelBuilder.Entity<ChannelAdmin>(e =>
        {
            e.HasKey(x => new { x.ChatId, x.UserId });
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<RegionState>(e =>
        {
            e.HasKey(x => x.RegionId);
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.PopupText).HasMaxLength(4000);
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
