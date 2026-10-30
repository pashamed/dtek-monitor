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
    IReadOnlyDictionary<string, string>? Templates = null,
    string? OutageTriggerPattern = null,
    IReadOnlyDictionary<string, SlotSettingDto>? SlotSettings = null,
    bool HasSvitlobotKey = false,
    string? AddressCity = null,
    bool ScheduleReminder = false,
    int ScheduleReminderMinutes = 15,
    bool QuietHoursEnabled = false,
    int QuietHoursStartMinute = 1320,
    int QuietHoursEndMinute = 480,
    bool? DeletePreviousAlert = null,
    // Omitted/null preserves saved addresses; a list replaces them atomically with the settings.
    IReadOnlyList<ChatAddressDto>? Addresses = null,
    // Read-only here: the group whose weekly schedule goes to Svitlobot; changed via PUT .../svitlobot/group.
    string? SvitlobotGroupId = null);

// Per alert-message type: whether the chat gets it at all, and whether a newer alert edits the open message.
// EditInPlace does not apply to the «ended» slot; EndStyle applies to it alone.
public sealed record SlotSettingDto(bool Enabled, bool EditInPlace, EndStyle EndStyle = EndStyle.Reply);

// How the «ended» notice is posted: a reply to the original alert, a separate message, or the original edited in place.
// The attribute keeps the stored JSON readable with the plain Web options as well.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndStyle
{
    Reply,
    New,
    Edit,
}

// Warning is advice only: saving the phrase is never blocked.
public sealed record TriggerCheckDto(bool Valid, int Seen, int Matched, string? Warning);

// Plain-text lines describing where things stand for a chat right now (popup, schedule, group emergency).
public sealed record ChatStatusDto(IReadOnlyList<string> Lines, ChatDiagnosticsDto? Diagnostics = null);

public sealed record ChatDiagnosticsDto(IReadOnlyList<string> Lines);

public sealed record ScheduleViewIntervalDto(DateTimeOffset Start, DateTimeOffset End);

public sealed record ScheduleViewDayDto(string Date, bool Available, IReadOnlyList<ScheduleViewIntervalDto> Intervals);

public sealed record ScheduleViewDto(
    string RegionId,
    string RegionName,
    string GroupId,
    DateTimeOffset ServerNow,
    DateTimeOffset CheckedAt,
    bool IsCached,
    IReadOnlyList<ScheduleViewDayDto> Days,
    DateTimeOffset? NextChangeAt,
    bool? NextChangeIsOff,
    IReadOnlyList<string> Warnings);

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

public sealed record AddressItemDto(int Id, string Name, string? GroupId = null);

public sealed record AddressNameDto(string Name);

public sealed record DtekAddressHouseDto(string House, string? GroupId, IReadOnlyList<string>? GroupIds = null);

public sealed record SetDtekAddressRequest(string RegionId, string City, string Street, string House, string? GroupId, IReadOnlyList<string>? GroupIds = null);

public sealed record SetAddressRequest(
    string RegionId,
    int StreetId,
    int HouseId,
    string Label,
    string? Street = null,
    string? House = null);

// Address typed by hand for regions without the address picker: exactly as written on the DTEK site.
public sealed record ManualAddressRequest(string RegionId, string City, string Street, string House);

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

// Name is what users see and type (Ukrainian); Key is the canonical Latin name used by storage and rendering.
public sealed record PlaceholderDto(string Name, string Description, string Example, string? Key = null);

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
    ScheduleImage? ImageKind = null,
    IReadOnlyList<ChatAddressDto>? Addresses = null,
    string? ScheduleLineTemplate = null,
    string? ScheduleHeadingTemplate = null);

// ImageUrl is set for slots that come with a picture (the schedule post).
public sealed record PreviewResponse(string Html, IReadOnlyList<string> Errors, string? ImageUrl = null);

// Posts the slot's message (the given template, or the chat's current one) into the chat, marked as a test.
public sealed record SendTestRequest(string? Template, ScheduleImage? ImageKind = null);

public sealed record ApiError(string Error, IReadOnlyList<string>? Details = null);

public sealed record ChatAddressGroupDto(string GroupId, string? Label = null);
public sealed record ChatAddressDto(
    long Id, string RegionId, string Name, string? City, string? Street, string? House,
    IReadOnlyList<ChatAddressGroupDto> Groups, string? PartIds = null, string? Label = null);
public sealed record SaveAddressesRequest(IReadOnlyList<ChatAddressDto> Addresses);
