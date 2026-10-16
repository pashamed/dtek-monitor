using System.Globalization;
using System.Text;

namespace DtekBot.Api.Svitlobot;

public enum SvitlobotFailure
{
    None,

    // No channel with this key.
    NotFound,

    // Svitlobot understood the call and refused it ("error", "parse error", "invalid data").
    Rejected,

    // Network problem, timeout, HTTP error or an answer nobody expected.
    Unavailable,
}

// Message is ready to show to the user (Ukrainian); it never contains the channel key.
public class SvitlobotResult(SvitlobotFailure failure, string? message)
{
    public SvitlobotFailure Failure { get; } = failure;

    public string? Message { get; } = message;

    public bool Ok => Failure == SvitlobotFailure.None;

    public static SvitlobotResult Success() => new(SvitlobotFailure.None, null);

    public static SvitlobotResult Fail(SvitlobotFailure failure, string message) => new(failure, message);
}

public sealed class SvitlobotResult<T>(T? value, SvitlobotFailure failure, string? message) : SvitlobotResult(failure, message)
{
    public T? Value { get; } = value;

    public static SvitlobotResult<T> Success(T value) => new(value, SvitlobotFailure.None, null);

    public new static SvitlobotResult<T> Fail(SvitlobotFailure failure, string message) => new(default, failure, message);
}

public sealed record SvitlobotTemplate(int Id, string Name);

public sealed record SvitlobotChannelInfo(IReadOnlyList<SvitlobotTemplate> Templates, bool HasSchedule);

// The channel key is a secret and travels in the query string, so nothing here may log a URL or a key:
// only the method name and the outcome are logged.
public interface ISvitlobotClient
{
    Task<SvitlobotResult<SvitlobotChannelInfo>> GetChannelAsync(string key, CancellationToken ct);

    Task<SvitlobotResult> SetTemplateAsync(string key, int templateId, CancellationToken ct);

    // Seven rows (Monday first) of 24 digits joined by ';'.
    Task<SvitlobotResult> SetTimetableAsync(string key, string timetableData, CancellationToken ct);

    // Removes the schedule and the schedule message in the Telegram channel.
    Task<SvitlobotResult> RemoveAsync(string key, CancellationToken ct);
}

public sealed class SvitlobotClient(HttpClient http, ILogger<SvitlobotClient> logger) : ISvitlobotClient
{
    public const string DefaultBaseUrl = "https://api.svitlobot.in.ua";
    private const string FieldSeparator = ";&;";
    private const string PartSeparator = ";&&&;";
    private const int MaxNameLength = 100;

    public async Task<SvitlobotResult<SvitlobotChannelInfo>> GetChannelAsync(string key, CancellationToken ct)
    {
        var (answer, failure) = await SendAsync("getChannelTimetable", $"/website/getChannelTimetable?channel_key={Uri.EscapeDataString(key)}", ct);
        if (answer is null)
        {
            return SvitlobotResult<SvitlobotChannelInfo>.Fail(failure!.Failure, failure.Message!);
        }

        return SvitlobotResult<SvitlobotChannelInfo>.Success(ParseChannel(answer));
    }

    public Task<SvitlobotResult> SetTemplateAsync(string key, int templateId, CancellationToken ct) =>
        WriteAsync(
            "setChannelTimetable",
            $"/website/setChannelTimetable?channel_key={Uri.EscapeDataString(key)}&timetable_id={templateId.ToString(CultureInfo.InvariantCulture)}",
            ct);

    public Task<SvitlobotResult> SetTimetableAsync(string key, string timetableData, CancellationToken ct) =>
        WriteAsync(
            "timetableEditEvent",
            $"/website/timetableEditEvent?channel_key={Uri.EscapeDataString(key)}&timetableData={Uri.EscapeDataString(timetableData)}",
            ct);

    public Task<SvitlobotResult> RemoveAsync(string key, CancellationToken ct) =>
        WriteAsync("removeChannelTimetable", $"/website/removeChannelTimetable?channel_key={Uri.EscapeDataString(key)}", ct);

    private async Task<SvitlobotResult> WriteAsync(string method, string pathAndQuery, CancellationToken ct)
    {
        var (answer, failure) = await SendAsync(method, pathAndQuery, ct);
        if (answer is null)
        {
            return failure!;
        }

        if (answer.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            return SvitlobotResult.Success();
        }

        logger.LogWarning("Svitlobot {Method}: unexpected answer", method);
        return SvitlobotResult.Fail(SvitlobotFailure.Unavailable, "Світлобот відповів несподівано. Спробуйте пізніше.");
    }

    // The trimmed answer, or a failure; known error answers are mapped here for every method.
    private async Task<(string? Answer, SvitlobotResult? Failure)> SendAsync(string method, string pathAndQuery, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(pathAndQuery, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Svitlobot {Method}: HTTP {Status}", method, (int)response.StatusCode);
                return (null, Unavailable());
            }

            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            var mapped = MapError(text);
            if (mapped is not null)
            {
                logger.LogInformation("Svitlobot {Method}: {Outcome}", method, mapped.Failure);
                return (null, mapped);
            }

            if (text.Length == 0)
            {
                logger.LogWarning("Svitlobot {Method}: empty answer", method);
                return (null, Unavailable());
            }

            logger.LogInformation("Svitlobot {Method}: answered", method);
            return (text, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Svitlobot {Method}: failed ({Error})", method, ex.GetType().Name);
            return (null, Unavailable());
        }
    }

    private static SvitlobotResult Unavailable() =>
        SvitlobotResult.Fail(SvitlobotFailure.Unavailable, "Світлобот недоступний. Спробуйте пізніше.");

    // The answer for an unknown key echoes the key, so it is only matched here, never passed on.
    internal static SvitlobotResult? MapError(string text)
    {
        if (text.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return SvitlobotResult.Fail(SvitlobotFailure.Rejected, "Світлобот повернув помилку. Спробуйте пізніше.");
        }

        if (text.Equals("parse error", StringComparison.OrdinalIgnoreCase))
        {
            return SvitlobotResult.Fail(SvitlobotFailure.Rejected, "Світлобот не зміг розібрати запит.");
        }

        if (text.Equals("invalid data", StringComparison.OrdinalIgnoreCase))
        {
            return SvitlobotResult.Fail(SvitlobotFailure.Rejected, "Світлобот відхилив надіслані дані.");
        }

        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Length < 300)
        {
            return SvitlobotResult.Fail(SvitlobotFailure.NotFound, "Світлобот не знайшов канал за цим ключем. Перевірте ключ.");
        }

        return null;
    }

    // Items are separated by ";&&&;" and the last one is the current matrix ("no data" when nothing is set);
    // the fields of a template by ";&;": the first is its id, the first non-numeric one after it is the name.
    internal static SvitlobotChannelInfo ParseChannel(string text)
    {
        var parts = text.Split(PartSeparator);
        var last = parts[^1].Trim();
        var templateParts = parts.Length > 1 ? parts[..^1] : [];
        var hasSchedule = last.Length > 0 && !last.Equals("no data", StringComparison.OrdinalIgnoreCase);

        // A lone item with fields is a template, not a matrix.
        if (parts.Length == 1 && last.Contains(FieldSeparator, StringComparison.Ordinal))
        {
            templateParts = parts;
            hasSchedule = false;
        }

        var templates = new List<SvitlobotTemplate>();
        foreach (var part in templateParts)
        {
            var template = ParseTemplate(part);
            if (template is not null && templates.All(t => t.Id != template.Id))
            {
                templates.Add(template);
            }
        }

        return new SvitlobotChannelInfo(templates, hasSchedule);
    }

    private static SvitlobotTemplate? ParseTemplate(string part)
    {
        var fields = part.Split(FieldSeparator).Select(f => f.Trim()).ToArray();
        if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var name = fields.Skip(1).FirstOrDefault(f => f.Length > 0 && !int.TryParse(f, NumberStyles.Any, CultureInfo.InvariantCulture, out _));
        if (name is null)
        {
            return new SvitlobotTemplate(id, $"Шаблон {id}");
        }

        var clean = new StringBuilder(name.Length);
        foreach (var c in name.Where(c => !char.IsControl(c)))
        {
            clean.Append(c);
        }

        var result = clean.ToString().Trim();
        if (result.Length > MaxNameLength)
        {
            result = result[..MaxNameLength];
        }

        return new SvitlobotTemplate(id, result.Length == 0 ? $"Шаблон {id}" : result);
    }
}
