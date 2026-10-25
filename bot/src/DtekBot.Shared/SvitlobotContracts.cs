namespace DtekBot.Shared;

// The Svitlobot channel key is a secret: it is only ever sent to the server (SetSvitlobotKeyRequest) and never returned.
public sealed record SetSvitlobotKeyRequest(string? Key);

public sealed record SvitlobotTemplateDto(int Id, string Name);

// Live state of the channel's Svitlobot schedule. Without a stored key: HasKey false, no templates, no schedule.
public sealed record SvitlobotStateDto(bool HasKey, IReadOnlyList<SvitlobotTemplateDto> Templates, bool HasSchedule);

public sealed record SetSvitlobotTemplateRequest(int TemplateId);

// Masked check value of the saved key (dots + last two characters) and a short keyed fingerprint; never the key itself.
public sealed record SvitlobotKeyCheckDto(string Masked, string Fingerprint);

// Optional body of the push call; a missing body or field means true: "possible" (maybe) outages count as outages.
public sealed record SvitlobotPushRequest(bool? IncludeMaybe = null);

// Chooses which of the chat's groups Svitlobot follows; null returns to the default (first address, first group).
public sealed record SetSvitlobotGroupRequest(string? GroupId);

// GroupId is the group Svitlobot follows after the change.
public sealed record SvitlobotGroupDto(string? GroupId, string Message);

// Answer of the write calls; HasSchedule is the state of the Svitlobot schedule afterwards.
public sealed record SvitlobotResultDto(string Message, bool HasSchedule);
