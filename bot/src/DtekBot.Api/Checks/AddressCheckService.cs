using System.Text.Json.Serialization;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace DtekBot.Api.Checks;

public enum RequestOutcome
{
    NoAddress,
    AlreadyPending,
    Cooldown,
    DailyLimit,
    Cached,
    Queued,
}

public sealed record CheckRequestResult(RequestOutcome Outcome, string Message);

public sealed record PendingStreet(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("street")] string Street,
    [property: JsonPropertyName("houses")] IReadOnlyList<string> Houses);

public sealed record HouseResultEntry(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("subType")] string? SubType,
    [property: JsonPropertyName("startDate")] string? StartDate,
    [property: JsonPropertyName("endDate")] string? EndDate,
    [property: JsonPropertyName("type")] string? Type);

public sealed record StreetResult(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("street")] string Street,
    [property: JsonPropertyName("houses")] Dictionary<string, HouseResultEntry>? Houses);

public sealed record AddressResultsRequest(
    [property: JsonPropertyName("results")] List<StreetResult>? Results);

public sealed class AddressCheckService(
    BotDbContext db,
    ITelegramBotClient bot,
    IWorkflowTrigger trigger,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<AddressCheckService> logger)
{
    public const string WorkflowEvent = "check-addresses";
    private const string LastDispatchKey = "last-dispatch";

    private AddressCheckSettings Limits => options.CurrentValue.AddressChecks;

    // Called when a user says the power is off and asks whether it is local.
    public async Task<CheckRequestResult> RequestAsync(Channel channel, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (string.IsNullOrEmpty(channel.AddressStreet) || string.IsNullOrEmpty(channel.AddressHouse))
        {
            return new(RequestOutcome.NoAddress, "Спершу оберіть адресу в редакторі (кнопка «🧩 Редактор повідомлень»).");
        }

        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        if (region is null || !region.HasGroups)
        {
            return new(RequestOutcome.NoAddress, "Для цього регіону перевірка за адресою поки недоступна.");
        }

        await ExpireStaleAsync(now, ct);

        if (await db.AddressChecks.AnyAsync(c => c.ChatId == channel.ChatId && c.Status == AddressCheckStatus.Pending, ct))
        {
            return new(RequestOutcome.AlreadyPending, "Попередню перевірку ще виконують. Зачекайте на відповідь.");
        }

        var cutoff = now - TimeSpan.FromMinutes(Limits.ResultCacheMinutes);
        var cached = await db.AddressResults.FirstOrDefaultAsync(r =>
            r.RegionId == channel.RegionId && r.Street == channel.AddressStreet && r.House == channel.AddressHouse
            && r.CheckedAt >= cutoff, ct);
        if (cached is not null)
        {
            return new(RequestOutcome.Cached, await BuildReplyAsync(channel.AddressLabel, cached, channel.RegionId, ct));
        }

        var last = await db.AddressChecks
            .Where(c => c.ChatId == channel.ChatId)
            .OrderByDescending(c => c.RequestedAt)
            .Select(c => (DateTimeOffset?)c.RequestedAt)
            .FirstOrDefaultAsync(ct);
        var cooldown = TimeSpan.FromMinutes(Limits.ChatCooldownMinutes);
        if (last is { } lastAt && now - lastAt < cooldown)
        {
            var left = (int)Math.Ceiling((cooldown - (now - lastAt)).TotalMinutes);
            return new(RequestOutcome.Cooldown, $"Перевірку можна повторити через {left} хв.");
        }

        var day = now - TimeSpan.FromHours(24);
        if (await db.AddressChecks.CountAsync(c => c.ChatId == channel.ChatId && c.RequestedAt >= day, ct) >= Limits.DailyLimitPerChat)
        {
            return new(RequestOutcome.DailyLimit, "Досягнуто добового ліміту перевірок. Спробуйте завтра.");
        }

        db.AddressChecks.Add(new AddressCheck
        {
            ChatId = channel.ChatId,
            RegionId = channel.RegionId,
            Street = channel.AddressStreet,
            House = channel.AddressHouse,
            Label = channel.AddressLabel ?? $"{channel.AddressStreet}, {channel.AddressHouse}",
            Status = AddressCheckStatus.Pending,
            RequestedAt = now,
        });
        await db.SaveChangesAsync(ct);

        var dispatched = await MaybeDispatchAsync(now, ct);
        logger.LogInformation("Address check queued for chat {ChatId} (dispatched={Dispatched})", channel.ChatId, dispatched);
        return new(RequestOutcome.Queued, "⏳ Перевіряю адресу на сайті ДТЕК. Це може тривати кілька хвилин, я напишу результат.");
    }

    // Many users share one workflow run: a dispatch is skipped while a recent one is still in flight.
    private async Task<bool> MaybeDispatchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var state = await db.KeyValues.FirstOrDefaultAsync(k => k.Key == LastDispatchKey, ct);
        if (state is not null
            && DateTimeOffset.TryParse(state.Value, out var lastAt)
            && now - lastAt < TimeSpan.FromMinutes(Limits.DispatchMinIntervalMinutes))
        {
            return false;
        }

        if (!await trigger.TriggerAsync(WorkflowEvent, ct))
        {
            return false;
        }

        if (state is null)
        {
            state = new KeyValue { Key = LastDispatchKey };
            db.KeyValues.Add(state);
        }

        state.Value = now.ToString("O");
        await db.SaveChangesAsync(ct);
        return true;
    }

    // What the workflow should check now: distinct streets, capped per run.
    public async Task<IReadOnlyList<PendingStreet>> GetPendingAsync(CancellationToken ct)
    {
        await ExpireStaleAsync(time.GetUtcNow(), ct);

        var pending = await db.AddressChecks
            .Where(c => c.Status == AddressCheckStatus.Pending)
            .OrderBy(c => c.RequestedAt)
            .ToListAsync(ct);

        return pending
            .GroupBy(c => (c.RegionId, c.Street))
            .Take(Limits.MaxStreetsPerRun)
            .Select(g => new PendingStreet(g.Key.RegionId, g.Key.Street, g.Select(c => c.House).Distinct().ToList()))
            .ToList();
    }

    public async Task<int> ProcessResultsAsync(AddressResultsRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var answered = 0;

        foreach (var street in request.Results ?? [])
        {
            foreach (var (house, entry) in street.Houses ?? [])
            {
                var result = await db.AddressResults.FirstOrDefaultAsync(r =>
                    r.RegionId == street.RegionId && r.Street == street.Street && r.House == house, ct);
                if (result is null)
                {
                    result = new AddressResult { RegionId = street.RegionId, Street = street.Street, House = house };
                    db.AddressResults.Add(result);
                }

                result.Found = entry.Found;
                result.SubType = Clip(entry.SubType, 512);
                result.StartDate = Clip(entry.StartDate, 64);
                result.EndDate = Clip(entry.EndDate, 64);
                result.Type = Clip(entry.Type, 256);
                result.CheckedAt = now;
            }

            await db.SaveChangesAsync(ct);

            var waiting = await db.AddressChecks
                .Where(c => c.Status == AddressCheckStatus.Pending && c.RegionId == street.RegionId && c.Street == street.Street)
                .ToListAsync(ct);
            foreach (var check in waiting)
            {
                if (street.Houses is null || !street.Houses.ContainsKey(check.House))
                {
                    continue;
                }

                var result = await db.AddressResults.FirstAsync(r =>
                    r.RegionId == check.RegionId && r.Street == check.Street && r.House == check.House, ct);
                await TrySendAsync(check.ChatId, await BuildReplyAsync(check.Label, result, check.RegionId, ct), ct);
                check.Status = AddressCheckStatus.Done;
                check.CompletedAt = now;
                answered++;
            }

            await db.SaveChangesAsync(ct);
        }

        return answered;
    }

    private async Task ExpireStaleAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromMinutes(Limits.RequestTimeoutMinutes);
        var stale = await db.AddressChecks
            .Where(c => c.Status == AddressCheckStatus.Pending && c.RequestedAt < cutoff)
            .ToListAsync(ct);
        foreach (var check in stale)
        {
            check.Status = AddressCheckStatus.Expired;
            check.CompletedAt = now;
            await TrySendAsync(check.ChatId, "Не вдалося перевірити адресу вчасно. Спробуйте пізніше.", ct);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<string> BuildReplyAsync(string? label, AddressResult result, string regionId, CancellationToken ct)
    {
        var where = string.IsNullOrWhiteSpace(label) ? $"{result.Street}, {result.House}" : label;
        var lines = new List<string>();

        if (!result.Found)
        {
            lines.Add($"❓ Адресу «{where}» не знайдено на сайті ДТЕК. Перевірте адресу в редакторі.");
        }
        else if (result.IsEmergency)
        {
            lines.Add($"🚨 За адресою {where} зафіксовано позапланове відключення.");
            lines.Add($"Причина: {result.SubType}");
            lines.Add(Period(result));
        }
        else if (result.HasOutage)
        {
            lines.Add($"⏰ За адресою {where} діє планове відключення за графіком.");
            lines.Add(Period(result));
        }
        else
        {
            lines.Add($"✅ За адресою {where} відключень на сайті ДТЕК немає.");
            lines.Add("Якщо світла немає, перевірте електрощиток і зверніться до ДТЕК.");
        }

        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == regionId, ct);
        var general = state?.Kind switch
        {
            PopupKind.Emergency => "На сайті ДТЕК діє загальне повідомлення про екстрені відключення.",
            PopupKind.Accident => "На сайті ДТЕК діє загальне повідомлення про аварію в мережі.",
            _ => null,
        };
        if (general is not null)
        {
            lines.Add("");
            lines.Add("ℹ️ " + general);
        }

        lines.Add("");
        lines.Add($"Перевірено: {KyivTime.Format(result.CheckedAt)} (Київ)");
        return string.Join("\n", lines.Where(l => l is not null));
    }

    private static string Period(AddressResult r) =>
        r.StartDate == "" && r.EndDate == "" ? "" : $"{r.StartDate} — {r.EndDate}".Trim(' ', '—');

    private static string Clip(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        return text.Length > max ? text[..max] : text;
    }

    private async Task TrySendAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            await bot.SendMessage(chatId, text, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Address check reply to chat {ChatId} failed: {Message}", chatId, ex.Message);
        }
    }
}
