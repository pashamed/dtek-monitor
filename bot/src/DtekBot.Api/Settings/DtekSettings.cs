namespace DtekBot.Api.Settings;

public sealed class DtekSettings
{
    public const string SectionName = "Dtek";

    public List<RegionSettings> Regions { get; set; } = [];
    public ClassificationSettings Classification { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
    public AddressCheckSettings AddressChecks { get; set; } = new();
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
    public List<string> Aliases { get; set; } = [];
    public List<PartSettings> Parts { get; set; } = [];
}

public sealed class PartSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
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
