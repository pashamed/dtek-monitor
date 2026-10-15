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
        string? popup)
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
                "✅",
                "Відключення скасовано",
                "екстрені відключення скасовано або повідомлення на сайті зникло."),
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
            ["where"] = where,
            ["site"] = siteUrl,
            ["time"] = KyivTime.Format(now),
            ["popup"] = popupText,
        };
    }
}
