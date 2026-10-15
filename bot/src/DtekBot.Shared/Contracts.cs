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
    string? Template,
    string? GroupId = null,
    string? AddressLabel = null,
    bool OutageTrigger = false,
    bool ScheduleNotify = false,
    ScheduleImage ScheduleImage = ScheduleImage.Grid);

// Which picture accompanies schedule messages (files from the outage-data-ua repository).
public enum ScheduleImage
{
    Grid,
    Card,
    None,
}

// Exactly what the bot would post for the chat's group right now.
public sealed record SchedulePreviewDto(string GroupId, string Text, string? ImageUrl);

public sealed record AddressItemDto(int Id, string Name);

public sealed record SetAddressRequest(
    string RegionId,
    int StreetId,
    int HouseId,
    string Label,
    string? Street = null,
    string? House = null);

public sealed record GroupStatusDto(string GroupId, string Today, string Tomorrow, bool IsEmergency);

public sealed record ChatSummaryDto(long ChatId, string Title, bool IsPrivate, ChatSettingsDto Settings);

public sealed record PartDto(string Id, string Name);

public sealed record RegionDto(
    string Id,
    string Name,
    string SiteUrl,
    IReadOnlyList<PartDto> Parts,
    bool HasGroups = false,
    bool HasSchedules = false);

public sealed record GroupItemDto(string Id, string Name);

public sealed record SetGroupRequest(string RegionId, string GroupId);

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
