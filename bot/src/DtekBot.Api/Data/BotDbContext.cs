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

    // Check the saved address when this chat gets a «Світло зникло» post (from a power-monitor app).
    public bool OutageTrigger { get; set; }
    public string? GroupId { get; set; }
    public string? AddressLabel { get; set; }

    // Street and house exactly as the address picker produced them; these are what the DTEK site is queried with.
    public string? AddressStreet { get; set; }
    public string? AddressHouse { get; set; }
}

public enum AddressCheckStatus
{
    Pending,
    Done,
    Expired,
}

// A user's "the power is off, is it local?" request, waiting for the workflow to check the DTEK site.
public sealed class AddressCheck
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public string RegionId { get; set; } = "";
    public string Street { get; set; } = "";
    public string House { get; set; } = "";
    public string Label { get; set; } = "";
    public AddressCheckStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

// Latest DTEK answer for one house; doubles as the short-lived cache that spares the DTEK site.
public sealed class AddressResult
{
    public string RegionId { get; set; } = "";
    public string Street { get; set; } = "";
    public string House { get; set; } = "";
    public bool Found { get; set; }
    public string SubType { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; }

    public bool HasOutage => Found && (SubType != "" || StartDate != "" || EndDate != "" || Type != "");

    // Same rule as the personal monitor: only avariyni/ekstreni are unplanned.
    public bool IsEmergency => HasOutage
        && (SubType.Contains("авар", StringComparison.OrdinalIgnoreCase)
            || SubType.Contains("екст", StringComparison.OrdinalIgnoreCase));
}

public sealed class KeyValue
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
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
    public DbSet<AddressCheck> AddressChecks => Set<AddressCheck>();
    public DbSet<AddressResult> AddressResults => Set<AddressResult>();
    public DbSet<KeyValue> KeyValues => Set<KeyValue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AddressCheck>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.Street).HasMaxLength(256);
            e.Property(x => x.House).HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.Status, x.RegionId });
            e.HasIndex(x => new { x.ChatId, x.RequestedAt });
        });

        modelBuilder.Entity<AddressResult>(e =>
        {
            e.HasKey(x => new { x.RegionId, x.Street, x.House });
            e.Property(x => x.RegionId).HasMaxLength(64);
            e.Property(x => x.Street).HasMaxLength(256);
            e.Property(x => x.House).HasMaxLength(64);
            e.Property(x => x.SubType).HasMaxLength(512);
            e.Property(x => x.StartDate).HasMaxLength(64);
            e.Property(x => x.EndDate).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(256);
            e.Ignore(x => x.HasOutage);
            e.Ignore(x => x.IsEmergency);
        });

        modelBuilder.Entity<KeyValue>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(256);
        });

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
            e.Property(x => x.AddressStreet).HasMaxLength(256);
            e.Property(x => x.AddressHouse).HasMaxLength(64);
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
