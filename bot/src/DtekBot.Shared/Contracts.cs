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
    ScheduleImage ScheduleImage = ScheduleImage.Grid,
    IReadOnlyList<string>? PartIds = null,
    bool SchedulePost = false,
    PostMode SchedulePostMode = PostMode.New,
    ScheduleImage SchedulePostImage = ScheduleImage.Grid,
    PostMode ScheduleEventMode = PostMode.Edit,
    IReadOnlyDictionary<string, string>? Templates = null);

// Plain-text lines describing where things stand for a chat right now (popup, schedule, group emergency).
public sealed record ChatStatusDto(IReadOnlyList<string> Lines);

// The answer to "the power is off, why?" (and, when a DTEK address check is queued, what to expect).
public sealed record CheckResultDto(string Text);

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

// How a message about a changing situation is kept in the channel.
public enum PostMode
{
    // Edit the same message while it is still the current one.
    Edit,

    // Post a new message every time and leave the old ones.
    New,

    // Post a new message, then delete the previous one.
    ReplaceDelete,
}

// One editable message type. Group is one of: emergency, regional, local, stabilization.
public sealed record TemplateSlotDto(
    string Id,
    string Group,
    string Title,
    string Description,
    string DefaultTemplate,
    IReadOnlyList<PlaceholderDto> Placeholders);

public sealed record BootstrapDto(
    long UserId,
    IReadOnlyList<RegionDto> Regions,
    IReadOnlyList<PlaceholderDto> Placeholders,
    string DefaultTemplate,
    int MaxTemplateLength,
    IReadOnlyList<TemplateSlotDto>? TemplateSlots = null);

// Chat is "@username" or a numeric chat id.
public sealed record LinkChatRequest(string Chat);

// Slot picks the message type (see TemplateSlotDto); Kind is only used by the alert slots' sample data.
public sealed record PreviewRequest(
    string? Template,
    AlertKind Kind,
    string? RegionId,
    string? PartId,
    IReadOnlyList<string>? PartIds = null,
    string? Slot = null,
    string? GroupId = null,
    ScheduleImage? ImageKind = null);

// ImageUrl is set for slots that come with a picture (the schedule post).
public sealed record PreviewResponse(string Html, IReadOnlyList<string> Errors, string? ImageUrl = null);

// Posts the slot's message (the given template, or the chat's current one) into the chat, marked as a test.
public sealed record SendTestRequest(string? Template, ScheduleImage? ImageKind = null);

public sealed record ApiError(string Error, IReadOnlyList<string>? Details = null);
