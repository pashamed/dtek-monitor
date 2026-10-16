using DtekBot.Shared;

namespace DtekBot.Api.Bot;

// Which source an alert message is based on; it is named at the end of the existing {message} text.
public enum AlertSource
{
    None,
    Dtek,
    Ukrenergo,
}

public static class AlertSourceText
{
    public static string Tag(string message, AlertSource source, AlertKind kind)
    {
        if (source == AlertSource.None || kind is not (AlertKind.Emergency or AlertKind.Accident))
        {
            return message;
        }

        var label = source == AlertSource.Ukrenergo ? "(за даними Укренерго)" : "(за даними ДТЕК)";
        var trimmed = message.TrimEnd().TrimEnd('.');
        return $"{trimmed} {label}";
    }
}
