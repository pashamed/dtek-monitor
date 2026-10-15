using System.Text.Json;
using System.Text.Json.Serialization;

namespace DtekBot.Shared;

// The server pins its own JSON options for /api so the Telegram.Bot snake_case policy never leaks in.
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum AlertFilter
{
    All,
    EmergencyOnly,
}

public enum AlertKind
{
    Emergency,
    Accident,
    Unknown,
    Ended,
}

public sealed record ChatSettingsDto(
    string? RegionId,
    string? PartId,
    bool IsActive,
    bool IsPaused,
    AlertFilter Filter,
    bool NotifyOnEnd,
    bool EditInPlace,
    string? Template);

public sealed record ChatSummaryDto(long ChatId, string Title, bool IsPrivate, ChatSettingsDto Settings);

public sealed record PartDto(string Id, string Name);

public sealed record RegionDto(string Id, string Name, string SiteUrl, IReadOnlyList<PartDto> Parts);

public sealed record PlaceholderDto(string Name, string Description, string Example);

public sealed record BootstrapDto(
    long UserId,
    IReadOnlyList<RegionDto> Regions,
    IReadOnlyList<PlaceholderDto> Placeholders,
    string DefaultTemplate,
    int MaxTemplateLength);

// Chat is "@username" or a numeric chat id.
public sealed record LinkChatRequest(string Chat);

public sealed record PreviewRequest(string? Template, AlertKind Kind, string? RegionId, string? PartId);

public sealed record PreviewResponse(string Html, IReadOnlyList<string> Errors);

public sealed record ApiError(string Error, IReadOnlyList<string>? Details = null);
