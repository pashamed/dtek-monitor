namespace DtekBot.Shared;

// Every kind of message the bot posts to a channel has a slot: its own template, placeholders and default text.
public static class MessageSlots
{
    public const string Emergency = "emergency";
    public const string Regional = "regional";
    public const string Local = "local";
    public const string Ended = "ended";
    public const string Schedule = "schedule";
    public const string ScheduleLine = "schedule-line";
    public const string ScheduleHeading = "schedule-heading";
    public const string BeforeOff = "before-off";
    public const string Off = "off";
    public const string On = "on";

    private static readonly IReadOnlyList<PlaceholderDto> LocalExtra =
    [
        new("group", "Номер черги", "45.1"),
    ];

    private static readonly IReadOnlyList<PlaceholderDto> SchedulePlaceholders =
    [
        new("header", "Заголовок: «Графік…» уперше за день, «Оновлений графік…» при зміні", "Оновлений графік відключень на сьогодні"),
        new("weekday", "День тижня", "Четвер"),
        new("date", "Дата", "15.10.2026"),
        new("outages", "Усі інтервали на сьогодні. Вигляд рядка — у «Рядок графіка»", "🔴 14:00 - 18:00 (~4 год)"),
        new("morning", "Інтервали, що починаються з 06:00 до 12:00", "🔴 08:00 - 10:00 (~2 год)"),
        new("afternoon", "Інтервали, що починаються з 12:00 до 18:00", "🔴 14:00 - 18:00 (~4 год)"),
        new("evening", "Інтервали, що починаються з 18:00 до 24:00", "🔴 20:00 - 22:00 (~2 год)"),
        new("night", "Інтервали, що починаються з 00:00 до 06:00", "🔴 02:00 - 04:00 (~2 год)"),
        new("tomorrowDate", "Дата завтрашнього графіка", "16.10.2026"),
        new("total", "Загальна тривалість відключень на сьогодні", "~4 год"),
        new("tomorrow", "Відключення на завтра (порожнє, поки графіка на завтра немає)", "🔴 02:00 - 06:00 (~4 год)"),
        new("updated", "Коли джерело оновило графік", "15.10.2026 01:02"),
        new("region", "Назва регіону", "м. Київ"),
        new("group", "Номер черги", "45.1"),
    ];

    private static readonly IReadOnlyList<PlaceholderDto> OffPlaceholders =
    [
        new("time", "Час, коли світло зникло", "14:24"),
        new("onDuration", "Скільки світло було до цього (порожнє, якщо невідомо)", "17год 23хв"),
        new("until", "Коли світло має повернутися за графіком (порожнє, якщо невідомо)", "18:00"),
        new("planned", "Скільки триватиме відключення за графіком", "~4 год"),
        new("region", "Назва регіону", "м. Київ"),
        new("group", "Номер черги", "45.1"),
    ];

    private static readonly IReadOnlyList<PlaceholderDto> BeforeOffPlaceholders =
    [
        new("time", "Коли почнеться планове відключення", "14:00"),
        new("minutes", "За скільки хвилин почнеться відключення", "15"),
        new("date", "Дата початку відключення", "15.10.2026"),
        new("region", "Назва регіону", "м. Київ"),
        new("group", "Номер черги", "45.1"),
    ];

    private static readonly IReadOnlyList<PlaceholderDto> OnPlaceholders =
    [
        new("time", "Час, коли світло з'явилося", "18:07"),
        new("offDuration", "Скільки світла не було (порожнє, якщо невідомо)", "3год 42хв"),
        new("next", "Коли наступне відключення за графіком (порожнє, якщо немає)", "22:00"),
        new("region", "Назва регіону", "м. Київ"),
        new("group", "Номер черги", "45.1"),
    ];

    public static IReadOnlyList<TemplateSlotDto> All { get; } =
    [
        new(
            Emergency,
            "emergency",
            "Екстрені відключення",
            "Сповіщення, коли на сайті ДТЕК з'явилося повідомлення про екстрені відключення. Графіки в цей час не діють.",
            MessageTemplate.Default,
            MessageTemplate.Placeholders),
        new(
            Ended,
            "emergency",
            "Запис про відключення знято",
            "Відповідь на сповіщення, коли запис про відключення зник із сайту ДТЕК. Це не підтвердження, що світло вже є.",
            MessageTemplate.Default,
            MessageTemplate.Placeholders),
        new(
            Regional,
            "regional",
            "Аварія в регіоні",
            "Сповіщення про аварію в енергосистемі регіону або району: можливі позачергові відключення.",
            MessageTemplate.Default,
            MessageTemplate.Placeholders),
        new(
            Local,
            "local",
            "Локальне відключення",
            "Сповіщення про аварійне чи екстрене відключення саме вашої черги (за даними Yasno).",
            "{emoji} Локальне відключення, черга {group}\n{message}{#address}\n📍 {address}{/address}\n\nДеталі: {site}",
            [.. MessageTemplate.Placeholders, .. LocalExtra]),
        new(
            Schedule,
            "stabilization",
            "Графік відключень",
            "Графік на сьогодні й завтра. Вигляд задають «Заголовок графіка» та «Рядок графіка»; старий загальний шаблон більше не використовується.",
            "❗️ {header}\n📅 {weekday} {date}\n\n{outages}",
            SchedulePlaceholders),
        new(
            ScheduleHeading,
            "stabilization",
            "Заголовок графіка",
            "Один заголовок на день. Черги та інтервали бот додає нижче автоматично. У власному шаблоні додайте {changes} для підсумку змін плану. Типовий заголовок показує підсумок автоматично.",
            "❗️ {header}\n📅 {weekday} {date}",
            [
                new("header", "Графік відключень на сьогодні / завтра", "Графік відключень на сьогодні"),
                new("weekday", "День тижня відповідного дня", "Середа"),
                new("date", "Дата відповідного дня", "21.10.2026"),
                new("region", "Збережений регіон", "Київська область"),
                new("part", "Збережені райони (якщо є)", "Бучанський район"),
                new("address", "Збережені адреси (якщо є)", "м. Ірпінь, вул. Західна, 14"),
                new("changes", "Планові інтервали, додані або прибрані відносно попереднього графіка (порожньо, якщо змін немає)", "Зміни: додано 14:00–16:00; прибрано 18:00–20:00"),
            ]),
        new(
            ScheduleLine,
            "stabilization",
            "Рядок графіка",
            "Вигляд кожного інтервалу в {відключення} та {завтра}: значки, час, дата й тривалість.",
            "🔴 {start} - {end} ({duration})",
            [
                new("start", "Початок інтервалу", "14:00"),
                new("end", "Кінець інтервалу (24:00 опівночі)", "18:00"),
                new("duration", "Тривалість інтервалу", "~4 год"),
                new("date", "Дата інтервалу", "15.10.2026"),
                new("weekday", "День тижня", "Четвер"),
                new("daypart", "Частина доби за початком інтервалу", "День"),
            ]),
        new(
            BeforeOff,
            "stabilization",
            "Попередження про відключення",
            "Одноразове повідомлення за вибрану кількість хвилин до початку планового відключення.",
            "⏳ За графіком черги {group} відключення почнеться о {time}{#date} ({date}){/date} — через {minutes} хв.",
            BeforeOffPlaceholders),
        new(
            Off,
            "stabilization",
            "Світло вимкнено за графіком",
            "Повідомлення, коли за графіком почалося відключення, зі статистикою: скільки світло було до цього.",
            "🔴 {time} Світло вимкнено за графіком{#onDuration}\n🕓 Воно було {onDuration}{/onDuration}{#until}\n🗓 Очікуємо за графіком о {until}{/until}",
            OffPlaceholders),
        new(
            On,
            "stabilization",
            "Світло з'явилося за графіком",
            "Повідомлення, коли відключення за графіком закінчилося, зі статистикою: скільки світла не було.",
            "🟢 {time} Світло з'явилося за графіком{#offDuration}\n🕓 Його не було {offDuration}{/offDuration}{#next}\n🗓 Наступне відключення за графіком о {next}{/next}",
            OnPlaceholders),
    ];

    public static TemplateSlotDto? Find(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    // Slots that share the alert layout and the legacy single template.
    public static bool IsAlert(string? id) => id is Emergency or Regional or Local or Ended;

    // At least one of these must appear, otherwise the message would carry no content.
    public static IReadOnlyList<string> RequiredAny(string slot) => slot switch
    {
        Emergency or Regional or Local => ["message", "popup"],
        Schedule => ["outages", "morning", "afternoon", "evening", "night"],
        ScheduleLine => ["start", "end"],
        _ => [],
    };

    // Sample values straight from the placeholder examples, for previews without real data.
    public static IReadOnlyDictionary<string, string> Samples(string slot) =>
        (Find(slot)?.Placeholders ?? []).ToDictionary(p => p.Name, p => p.Example);
}
