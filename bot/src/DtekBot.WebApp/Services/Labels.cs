using DtekBot.Shared;

namespace DtekBot.WebApp.Services;

public static class Labels
{
    public static string KindLabel(AlertKind kind) => kind switch
    {
        AlertKind.Emergency => "Екстрені",
        AlertKind.Accident => "Аварія",
        AlertKind.Unknown => "Інше",
        AlertKind.Ended => "Завершення",
        _ => kind.ToString(),
    };

    public static string Subscription(ChatSettingsDto settings, IReadOnlyList<RegionDto> regions)
    {
        if (!settings.IsActive)
        {
            return "Не підписано";
        }

        var region = regions.FirstOrDefault(r => r.Id == settings.RegionId);
        if (region is null)
        {
            return "Регіон не вибрано";
        }

        var part = region.Parts.FirstOrDefault(p => p.Id == settings.PartId);
        return part is null ? region.Name : $"{region.Name} · {part.Name}";
    }
}
