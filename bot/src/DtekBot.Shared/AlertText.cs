namespace DtekBot.Shared;

public static class AlertText
{
    private const int MaxPopupLength = 2000;

    public static IReadOnlyDictionary<string, string> Values(
        AlertKind kind,
        string regionName,
        IReadOnlyList<string> partNames,
        string siteUrl,
        DateTimeOffset now,
        string? popup,
        bool stabilizationActive = false)
    {
        var part = string.Join(", ", partNames);
        var where = partNames.Count > 0 ? $"{regionName} ({part})" : regionName;

        var (emoji, title, sentence) = kind switch
        {
            AlertKind.Emergency => (
                "⚠️",
                "Екстрені відключення",
                "введені екстрені відключення. Графіки не діють; повідомимо про зміни."),
            AlertKind.Accident => (
                "⚠️",
                "Аварія в енергосистемі",
                "аварія в енергосистемі. Можливі позачергові відключення; повідомимо про зміни."),
            AlertKind.Ended => (
                "ℹ️",
                "Запис про відключення знято",
                stabilizationActive
                    ? "діють стабілізаційні графіки. Це не підтверджує наявність світла; стежимо за змінами."
                    : "запис про екстрені відключення більше не відображається. Скасування не підтверджене; перевіряємо оновлення. Це не підтверджує наявність світла."),
            _ => (
                "ℹ️",
                "Статус відключень невідомий",
                "статус відключень не визначено. Перевіряємо оновлення."),
        };

        var popupText = (popup ?? "").Trim();
        if (popupText.Length > MaxPopupLength)
        {
            popupText = popupText[..MaxPopupLength] + "…";
        }

        return new Dictionary<string, string>
        {
            ["emoji"] = emoji,
            ["title"] = title,
            ["status"] = title,
            ["statusCode"] = kind.ToString(),
            ["message"] = $"{where}: {sentence}",
            ["region"] = regionName,
            ["regionId"] = "",
            ["city"] = "",
            ["group"] = "",
            ["part"] = part,
            ["address"] = "",
            ["where"] = where,
            ["site"] = siteUrl,
            ["time"] = KyivTime.Format(now),
            ["popup"] = popupText,
            ["date"] = KyivTime.FromUtc(now).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture),
            ["weekday"] = new[] { "Неділя", "Понеділок", "Вівторок", "Середа", "Четвер", "Пʼятниця", "Субота" }[(int)KyivTime.FromUtc(now).DayOfWeek],
            ["daypart"] = new[] { "Ніч", "Ранок", "День", "Вечір" }[KyivTime.FromUtc(now).Hour / 6],
        };
    }
}
