namespace DtekBot.Api.Settings;

public sealed class DtekSettings
{
    public const string SectionName = "Dtek";

    public List<RegionSettings> Regions { get; set; } = [];
    public ClassificationSettings Classification { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
}

public sealed class RegionSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string GridStatusSlug { get; set; } = "";
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
