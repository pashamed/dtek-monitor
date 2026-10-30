using System.ComponentModel.DataAnnotations.Schema;
using DtekBot.Api.Parsing;

namespace DtekBot.Api.Data;

// Statuses of the Ukrenergo (НЕК) feed. Only the last two mean an emergency; schedules and shortage are not alerts.
public static class GridStatuses
{
    public const string Normal = "normal";
    public const string Shortage = "shortage";
    public const string OutageSchedules = "outage_schedules";
    public const string EmergencyOutages = "emergency_outages";
    public const string GridDamage = "grid_damage";

    public static bool IsKnown(string? status) => status is
        Normal or Shortage or OutageSchedules or EmergencyOutages or GridDamage;

    public static PopupKind ToKind(string? status) => status switch
    {
        EmergencyOutages => PopupKind.Emergency,
        GridDamage => PopupKind.Accident,
        _ => PopupKind.None,
    };
}

// Latest Ukrenergo status of one region, as last accepted from the public feed.
public sealed class GridState
{
    public string RegionId { get; set; } = "";
    public string Status { get; set; } = GridStatuses.Normal;
    public string? Title { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset FeedGeneratedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [NotMapped]
    public PopupKind Kind => GridStatuses.ToKind(Status);

    [NotMapped]
    public bool IsAlert => Kind != PopupKind.None;
}
