namespace DtekBot.Shared;

public static class TemplateStatus
{
    public const string PlannedOutage = "PlannedOutage";
    public const string ScheduleAvailable = "ScheduleAvailable";
    public const string ScheduleUnavailable = "ScheduleUnavailable";

    public static IReadOnlyList<PlaceholderDto> Placeholders { get; } =
    [
        new("status", "Статус події українською; не вимірює наявність світла", "Екстрені відключення"),
        new("statusCode", "Код події: Emergency, Accident, Ended, Unknown; для графіків ScheduleAvailable, ScheduleUnavailable або PlannedOutage", "Emergency"),
    ];
}
