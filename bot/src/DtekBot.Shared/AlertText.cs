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
                "🚨",
                "Екстрені відключення",
                "введені екстрені відключення електроенергії. Графіки відключень не діють. Розумно споживайте електроенергію."),
            AlertKind.Accident => (
                "⚠️",
                "Аварія в енергосистемі",
                "аварія в енергосистемі, можливі позачергові відключення. Стабілізаційні графіки можуть не діяти."),
            AlertKind.Ended => (
                "ℹ️",
                "Запис про відключення знято",
                stabilizationActive
                    ? "екстрені відключення завершено; за повідомленням ДТЕК, діють стабілізаційні графіки. Це не означає, що електроенергія вже є."
                    : "запис про екстрені відключення більше не відображається. Це не підтверджує, що електропостачання відновлено."),
            _ => (
                "ℹ️",
                "Повідомлення ДТЕК",
                "на сайті ДТЕК з'явилося важливе повідомлення."),
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
            ["message"] = $"{where}: {sentence}",
            ["region"] = regionName,
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
