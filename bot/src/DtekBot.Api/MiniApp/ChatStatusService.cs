using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.MiniApp;

// What the editor's "current state" card shows: what the DTEK site says for the region and what the schedule
// says for the chat's group. Reads only data the bot already has, so it is free to call.
public sealed class ChatStatusService(
    BotDbContext db,
    IScheduleProvider schedules,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time)
{
    public async Task<IReadOnlyList<string>> BuildAsync(
        Channel? channel,
        CancellationToken ct,
        string? requestedRegionId = null,
        IReadOnlyList<string>? requestedPartIds = null)
    {
        var regionId = string.IsNullOrWhiteSpace(requestedRegionId) ? channel?.RegionId : requestedRegionId;
        var region = string.IsNullOrEmpty(regionId)
            ? null
            : options.CurrentValue.Regions.FirstOrDefault(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return ["Оберіть регіон, щоб побачити поточний стан."];
        }

        var settingsMatch = channel is not null
            && string.Equals(channel.RegionId, region.Id, StringComparison.OrdinalIgnoreCase);
        var groupIds = settingsMatch ? channel!.GroupIds() : [];
        var partIds = requestedPartIds ?? (settingsMatch ? channel!.PopupDistrictIds() : []);
        var lines = new List<string>();

        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
        var classified = state?.Classification().Resolve(partIds);
        var districtEmergencyApplies = classified?.Kind != PopupKind.Emergency
            || classified.PartIds is not { Count: > 0 }
            || partIds.Count == 0
            || classified.PartIds.Intersect(partIds, StringComparer.OrdinalIgnoreCase).Any();
        lines.Add(classified?.Kind == PopupKind.Emergency && !districtEmergencyApplies
            ? "ℹ️ Для обраних районів екстрених відключень на сайті ДТЕК не зазначено."
            : classified?.Kind switch
        {
            PopupKind.Emergency => "🚨 На сайті ДТЕК діють екстрені відключення.",
            PopupKind.Accident => "⚠️ На сайті ДТЕК повідомлено про аварію в енергосистемі.",
            PopupKind.Unknown => "ℹ️ На сайті ДТЕК є невизначене повідомлення.",
            PopupKind.Stabilization => "🗓 Діють стабілізаційні відключення (за графіками).",
            null => "Даних із сайту ДТЕК ще немає.",
            _ => "ℹ️ Екстрених відключень на сайті ДТЕК не зазначено.",
        });
        if (state is not null)
        {
            lines.Add($"Перевірено: {KyivTime.Format(state.ObservedAt)} (час Києва · {region.Name})");
        }

        foreach (var groupId in groupIds)
        {
            var group = await db.GroupStates.FirstOrDefaultAsync(g => g.RegionId == region.Id && g.GroupId == groupId, ct);
            if (group?.IsEmergency == true)
            {
                lines.Add($"🚨 Для черги {groupId} діють екстрені відключення (за даними Yasno).");
            }

            if (region.HasSchedules)
            {
                var snapshot = await schedules.GetAsync(region.Id, ct);
                var now = time.GetUtcNow();
                var status = snapshot?.StatusAt(groupId, now);
                if (status is { IsOff: true })
                {
                    var since = status.Since is { } s ? $" з {ScheduleText.Clock(s)}" : "";
                    var until = status.Until is { } u ? $" Планове завершення о {ScheduleText.ClockWithDay(u, now)}." : "";
                    lines.Add($"🗓 За графіком (черга {groupId}) заплановано відключення{since}.{until}");
                }
                else if (status is not null)
                {
                    var next = status.NextStart is { } n
                        ? $" Наступне відключення о {ScheduleText.ClockWithDay(n, now)}."
                        : " Наступних відключень за графіком поки немає.";
                    lines.Add($"🗓 За графіком (черга {groupId}) зараз відключення не заплановано.{next}");
                }
            }
        }

        return lines;
    }

    public async Task<ChatDiagnosticsDto> DiagnosticsAsync(Channel? channel, CancellationToken ct)
    {
        if (channel is null)
            return new(["Підписку ще не налаштовано. Перегляд графіка не потребує підписки."]);

        var settings = options.CurrentValue;
        var region = settings.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        var now = time.GetUtcNow();
        var lines = new List<string>
        {
            $"Збережений регіон: {region?.Name ?? "не обрано"}.",
            $"Підписка: {(!channel.IsActive ? "вимкнена" : channel.IsPaused ? "на паузі" : "активна")}.",
            "Фактична наявність електроенергії невідома: ці джерела повідомляють про обмеження та опублікований план, а не вимірюють напругу.",
        };
        if (region is null) return new(lines);

        var districts = region.DisplayNames(channel.PartIdList);
        lines.Add("Збережені райони: " + (districts.Count > 0 ? string.Join(", ", districts) : "увесь регіон") + ".");
        var groupIds = channel.GroupIds();
        lines.Add("Збережені черги: " + (groupIds.Count > 0 ? string.Join(", ", groupIds) : "не обрано") + ".");
        foreach (var address in channel.Addresses)
            lines.Add($"Адреса «{address.Name}»: {address.Label ?? string.Join(", ", new[] { address.City, address.Street, address.House }.Where(v => !string.IsNullOrWhiteSpace(v)))}.");
        if (channel.Addresses.Count == 0 && !string.IsNullOrWhiteSpace(channel.AddressLabel))
            lines.Add("Адреса: " + channel.AddressLabel + ".");

        var addresses = channel.Addresses.Select(a => (a.City, a.Street, a.House, Label: a.Name)).ToList();
        if (addresses.Count == 0 && channel.AddressStreet is not null)
            addresses.Add((channel.AddressCity, channel.AddressStreet, channel.AddressHouse, channel.AddressLabel ?? "збережена"));
        foreach (var address in addresses.Where(a => a.Street is not null && a.House is not null))
        {
            var city = address.City ?? "";
            var result = await db.AddressResults.AsNoTracking().FirstOrDefaultAsync(r =>
                r.RegionId == region.Id && r.City == city && r.Street == address.Street && r.House == address.House, ct);
            lines.Add(result is null
                ? $"ДТЕК за адресою «{address.Label}»: успішної перевірки ще немає."
                : $"ДТЕК за адресою «{address.Label}»: "
                    + (!result.Found ? "адресу не знайдено." : result.HasOutage ? "є запис про відключення." : "запису про відключення немає.")
                    + $" Перевірено: {KyivTime.Format(result.CheckedAt)}."
                    + (now - result.CheckedAt > TimeSpan.FromMinutes(settings.AddressChecks.ResultCacheMinutes)
                        ? " Попередня відповідь; поточний стан не підтверджено." : ""));
        }

        var grid = await db.GridStates.AsNoTracking().FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
        lines.Add(grid is null
            ? "Укренерго: даних ще немає."
            : $"Укренерго: {GridLabel(grid.Status)}. Час даних: {KyivTime.Format(grid.FeedGeneratedAt)}."
                + (now - grid.FeedGeneratedAt > TimeSpan.FromMinutes(settings.Grid.MaxAgeMinutes)
                    ? " Дані застарілі; не є підтвердженням поточного стану." : ""));

        lines.Add($"Автопублікація графіка: {(channel.SchedulePost ? "увімкнена" : "вимкнена")}.");
        if (channel.SchedulePost)
            lines.Add(channel.SchedulePostMode switch
            {
                PostMode.Edit => "Оновлення графіка редагує попередній допис: нового повідомлення може не бути.",
                PostMode.ReplaceDelete => "Оновлення графіка створює новий допис і намагається видалити попередній.",
                _ => "Зміна графіка публікується новим повідомленням.",
            });
        if (!region.HasSchedules)
            lines.Add("Джерело графіків для цього регіону не налаштовано.");
        else if (groupIds.Count == 0)
            lines.Add("Для автопублікації оберіть чергу або адресу з чергою.");
        else
        {
            var snapshot = await schedules.GetAsync(region.Id, ct);
            if (snapshot is null)
                lines.Add("Графік недоступний: джерело не повернуло придатних даних. Відсутність даних не означає відсутність відключень.");
            else
            {
                lines.Add($"Графік: остання успішна перевірка {KyivTime.Format(snapshot.FetchedAt)}."
                    + (now - snapshot.FetchedAt > TimeSpan.FromMinutes(settings.Schedules.CacheMinutes)
                        ? " Використовується попередня копія; свіжішу перевірку не підтверджено." : ""));
                var date = KyivTime.FromUtc(now).Date;
                foreach (var offset in new[] { 0, 1 })
                {
                    var start = ScheduleText.KyivDayStart(date.AddDays(offset));
                    var end = ScheduleText.KyivDayStart(date.AddDays(offset + 1));
                    var missing = groupIds.Where(id => !snapshot.CoversDay(id, start, end)).ToArray();
                    var available = snapshot.CoverageStart <= start && snapshot.CoverageEnd >= end;
                    lines.Add($"Графік на {(offset == 0 ? "сьогодні" : "завтра")}: "
                        + (!available ? "ще не оприлюднено або дані вже не охоплюють цю дату."
                            : missing.Length > 0 ? "немає даних для черг " + string.Join(", ", missing) + "."
                            : "доступний для збережених черг."));
                }
            }
        }

        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.ChatId == channel.ChatId && d.TelegramMessageId > 0).Select(d => d.UpdatedAt).ToListAsync(ct);
        var posts = await db.SchedulePostThreads.AsNoTracking()
            .Where(d => d.ChatId == channel.ChatId && d.MessageId > 0).Select(d => d.UpdatedAt).ToListAsync(ct);
        var events = await db.ScheduleThreads.AsNoTracking()
            .Where(d => d.ChatId == channel.ChatId && d.MessageId > 0).Select(d => d.UpdatedAt).ToListAsync(ct);
        lines.Add(posts.Count == 0 ? "Збережених успішних публікацій графіка ще немає."
            : $"Остання збережена успішна публікація чи зміна графіка: {KyivTime.Format(posts.Max())}.");
        var last = deliveries.Concat(posts).Concat(events).OrderDescending().FirstOrDefault();
        lines.Add(last == default ? "Збережених записів доставки ще немає."
            : $"Останнє оновлення збереженого запису доставки: {KyivTime.Format(last)}.");
        lines.Add("Це історія стану доставки, не перевірка наявності повідомлення в Telegram. Журнал помилок доставки тут поки недоступний.");
        if (!channel.IsActive || channel.IsPaused)
            lines.Add("Автосповіщення зупинено налаштуваннями підписки.");
        return new(lines);
    }

    private static string GridLabel(string status) => status switch
    {
        GridStatuses.Normal => "обмежень не зазначено",
        GridStatuses.Shortage => "дефіцит електроенергії",
        GridStatuses.OutageSchedules => "погодинні графіки",
        GridStatuses.EmergencyOutages => "екстрені відключення",
        GridStatuses.GridDamage => "пошкодження мережі",
        _ => "невизначений стан",
    };
}
