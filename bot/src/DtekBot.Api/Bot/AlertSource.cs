namespace DtekBot.Api.Bot;

// Attribution is based on selected evidence, not on wording inferred by the language model.
public enum AlertSource
{
    None,
    Dtek,
    Ukrenergo,
    Yasno,
}

public static class AlertSourceText
{
    public static string Tag(string message, AlertSource source)
    {
        if (source == AlertSource.None)
        {
            return message;
        }

        var label = Attribution(source);
        var trimmed = message.TrimEnd().TrimEnd('.');
        return $"{trimmed} {label}";
    }

    public static string EnsureAttribution(string html, AlertSource source)
    {
        if (source == AlertSource.None) return html;
        var label = Attribution(source);
        return html.Contains(label, StringComparison.Ordinal) ? html : $"{html}\n\n{label}";
    }

    private static string Attribution(AlertSource source) => source switch
    {
        AlertSource.Dtek => "(за даними ДТЕК)",
        AlertSource.Ukrenergo => "(за даними Укренерго)",
        AlertSource.Yasno => "(за даними Yasno)",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
