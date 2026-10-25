namespace DtekBot.Api.Settings;

public sealed class DtekSettings
{
    public const string SectionName = "Dtek";

    public List<RegionSettings> Regions { get; set; } = [];
    public ClassificationSettings Classification { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
    public AddressCheckSettings AddressChecks { get; set; } = new();
    public ScheduleSettings Schedules { get; set; } = new();
    public GridSettings Grid { get; set; } = new();
    public TimerSettings Timers { get; set; } = new();
}

// Background timings. Interval changes apply from the next tick; turning a timer on or off needs a restart.
public sealed class TimerSettings
{
    // Monitoring cycle: DTEK popups, Ukrenergo, schedules (Baskerville per Schedules:CacheMinutes) and notifications.
    public int MonitoringIntervalMinutes { get; set; } = 5;
    public int MonitoringStartDelaySeconds { get; set; } = 30;

    // check-emergency dispatch of the standalone monitor workflow; 0 turns it off.
    public int MonitorWorkflowIntervalMinutes { get; set; } = 15;
    public int MonitorWorkflowStartDelaySeconds { get; set; } = 60;

    // How long a monitoring cycle waits for the check-regions browser fallback.
    public int BrowserWaitSeconds { get; set; } = 180;

    // Kyiv day and time of the weekly forced Svitlobot rewrite, so the new week is drawn from fresh data.
    public DayOfWeek SvitlobotWeeklyRefreshDay { get; set; } = DayOfWeek.Monday;
    public TimeSpan SvitlobotWeeklyRefreshTime { get; set; } = new(3, 0, 0);

    public TimeSpan MonitoringInterval => TimeSpan.FromMinutes(Math.Clamp(MonitoringIntervalMinutes, 1, 60));
    public TimeSpan MonitoringStartDelay => TimeSpan.FromSeconds(Math.Clamp(MonitoringStartDelaySeconds, 0, 600));
    public TimeSpan MonitorWorkflowInterval => TimeSpan.FromMinutes(Math.Clamp(MonitorWorkflowIntervalMinutes, 5, 60));
    public TimeSpan MonitorWorkflowStartDelay => TimeSpan.FromSeconds(Math.Clamp(MonitorWorkflowStartDelaySeconds, 0, 600));
    public TimeSpan BrowserWait => TimeSpan.FromSeconds(Math.Clamp(BrowserWaitSeconds, 1, 240));
}

public sealed class RegionSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string GridStatusSlug { get; set; } = "";

    // Set only for regions covered by the Yasno address and group API (Kyiv).
    public int YasnoRegionId { get; set; }
    public int YasnoDsoId { get; set; }
    public bool HasGroups => YasnoRegionId > 0 && YasnoDsoId > 0;

    // File name (without .json) in the outage-data-ua repo: data/<name>.json and images/<name>/.
    public string OutageDataFile { get; set; } = "";
    public bool HasSchedules => !string.IsNullOrEmpty(OutageDataFile);
    public List<string> Aliases { get; set; } = [];
    public List<PartSettings> Parts { get; set; } = [];

    // Part ids that used to exist and may still be saved in chats, mapped to the parts that replaced them.
    public Dictionary<string, List<string>> RetiredParts { get; set; } = [];

    // Short names for a retired group when a message covers all of its parts ("Правий берег" instead of seven districts).
    public Dictionary<string, string> RetiredPartLabels { get; set; } = [];

    // Names for a message: the full set of a retired group's parts collapses to its label.
    public List<string> DisplayNames(IEnumerable<string> ids)
    {
        var wanted = ids.ToList();
        var names = new List<string>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (retired, parts) in RetiredParts)
        {
            if (parts.Count > 1 && RetiredPartLabels.TryGetValue(retired, out var label)
                && parts.All(p => wanted.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                names.Add(label);
                covered.UnionWith(parts);
            }
        }

        names.AddRange(wanted.Where(id => !covered.Contains(id))
            .Select(id => Parts.FirstOrDefault(p => p.Id == id)?.Name ?? id));
        return names;
    }

    // Replaces retired part ids with their replacements; true when the channel was changed.
    public bool UpgradeRetiredParts(DtekBot.Api.Data.Channel channel)
    {
        var saved = channel.PartIdList;
        if (RetiredParts.Count == 0 || !saved.Any(id => RetiredParts.Keys.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var upgraded = new List<string>();
        foreach (var id in saved)
        {
            var replacement = RetiredParts.FirstOrDefault(r => string.Equals(r.Key, id, StringComparison.OrdinalIgnoreCase));
            upgraded.AddRange(replacement.Value ?? [id]);
        }

        channel.PartId = null;
        channel.PartIdList = upgraded;
        return true;
    }
}

public sealed class PartSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];

    // Parts implied by this one: an alert for a bank also concerns the city districts on it.
    public List<string> Children { get; set; } = [];
}

public sealed class ClassificationSettings
{
    public List<string> Cancelled { get; set; } = [];
    public List<string> Stabilization { get; set; } = [];
    public List<string> Accident { get; set; } = [];
    public List<string> Emergency { get; set; } = [];
}

public sealed class PollingSettings
{
    public int CancelRecheckDelaySeconds { get; set; } = 30;
}

// Schedules come from the public outage-data-ua repository (MIT), which republishes DTEK data every few minutes.
public sealed class ScheduleSettings
{
    public string BaseUrl { get; set; } = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/";

    // How often Baskerville is checked for new schedules; checks run on monitoring cycles, so use a multiple of
    // Timers:MonitoringIntervalMinutes.
    public int CacheMinutes { get; set; } = 10;
    public bool EnableEventNotifications { get; set; }

    // A failed refresh keeps serving the last good copy for this long.
    public int MaxStaleMinutes { get; set; } = 120;
}

// The public feed of the Ukrenergo (НЕК) status per region, republished on GitHub.
public sealed class GridSettings
{
    public string FeedUrl { get; set; } = "https://raw.githubusercontent.com/lasercat12/ua-power-status/main/data/regions.json";
    public int CacheMinutes { get; set; } = 1;

    // A feed generated longer ago than this is ignored: unknown never opens or closes an alert.
    public int MaxAgeMinutes { get; set; } = 45;
}

// Limits that keep on-demand address checks from hammering GitHub Actions and the DTEK site.
public sealed class AddressCheckSettings
{
    public int ChatCooldownMinutes { get; set; } = 10;
    public int DailyLimitPerChat { get; set; } = 20;
    public int ResultCacheMinutes { get; set; } = 5;
    public int DispatchMinIntervalMinutes { get; set; } = 3;
    public int MaxStreetsPerRun { get; set; } = 20;
    public int RequestTimeoutMinutes { get; set; } = 15;
}
