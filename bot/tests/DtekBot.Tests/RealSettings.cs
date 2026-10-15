using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DtekBot.Tests;

public sealed class StaticOptions(DtekSettings value) : IOptionsMonitor<DtekSettings>
{
    public DtekSettings CurrentValue => value;

    public DtekSettings Get(string? name) => value;

    public IDisposable? OnChange(Action<DtekSettings, string?> listener) => null;
}

// The real dtek-settings.json that ships with the bot.
public static class RealSettings
{
    public static DtekSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dtek-settings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        return config.GetSection(DtekSettings.SectionName).Get<DtekSettings>()!;
    }

    public static PopupClassifier Classifier(DtekSettings? settings = null) =>
        new(new StaticOptions(settings ?? Load()));
}
