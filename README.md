# 🪫 DTEK Monitor

Automatic monitoring of DTEK accidental and emergency power outages, with Telegram notifications.

Architecture and onboarding: [application diagram and walkthrough](docs/architecture.md).

## 💖 Support

If you find this project useful, give it a ⭐️ and consider [making a donation](https://send.monobank.ua/jar/3QTpZrNn2T).

## 📋 Overview

The standalone monitor checks the DTEK website for a configured address and sends Telegram notifications about accidental or emergency outages. It also tracks regional emergency notices and skips scheduled address outages. GitHub Actions runs the browser script; the optional shared backend dispatches regular popup checks, with GitHub cron as a best-effort backup. The shared application additionally supports multiple chats, schedule notifications, and a Telegram Mini App.

## ✨ Features

- 🤖 Automatic periodic checks of reported outage status.
- 📱 Telegram notifications about accidental and emergency outages.
- 🚨 Regional popup checks dispatched every 15 minutes by the backend, with notices about transitions to stabilization outages or the absence of stabilization outages.
- 🔴 Regional grid status in emergency and clearance messages, using [ua-power-status](https://github.com/lasercat12/ua-power-status). Region mapping: `k` — Kyiv city, `kr` — Kyiv oblast, `dn` — Dnipropetrovsk oblast, `o` — Odesa oblast, `d` — Donetsk oblast. The status line is omitted if data is unavailable.
- ♻️ Updates to the existing message when the outage time and cause are unchanged.
- ↩️ A reply to the previous message when the outage time or cause changes.
- 🧵 Replies to configured channel power-off posts with the standalone address check, without editing the source post.
- 📊 Outage cause, start time, and estimated restoration time.
- 🌙 Silent notifications at night (22:00–08:00).
- 🔄 Browser checks executed through GitHub Actions.
- 🗂 Standalone bot state stored on a separate `artifacts` branch, keeping runtime state out of `main`.

## 🚀 Quick start

### 1. Fork the repository

Click **Fork** in the upper-right corner of the repository page.

### 2. Create a Telegram bot

1. Open [@BotFather](https://t.me/BotFather) in Telegram.
2. Send `/newbot`.
3. Choose a name and username for the bot.
4. Save the token (format: `123456789:ABCdefGHIjklMNOpqrsTUVwxyz`).

### 3. Get the chat ID

1. Send any message to your bot.
2. Open `https://api.telegram.org/bot<YOUR_BOT_TOKEN>/getUpdates` in a browser.
3. Find a value such as `"chat":{"id":123456789}` — this is your chat ID.

### 4. Configure secrets

In your fork, open **Settings → Secrets and variables → Actions** and add:

| Secret               | Description                                                                                      | Example                                                                                          |
| -------------------- | ------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------ |
| `TELEGRAM_BOT_TOKEN` | BotFather token                                                                                  | `123456789:ABCdefGHI...`                                                                         |
| `TELEGRAM_CHAT_ID`   | Telegram chat ID                                                                                  | `123456789` or `-123456789` (channel)                                                             |
| `REGION`             | DTEK region                                                                                      | `k` — Kyiv city, `kr` — Kyiv oblast, `dn` — Dnipropetrovsk oblast, `o` — Odesa oblast, `d` — Donetsk oblast |
| `CITY`               | Settlement type and name in Cyrillic, exactly as on DTEK: `м.` (city), `с.` (village), `смт`, etc. | `м. Васюківка`, `с. Васюківка`; leave empty for Kyiv city (`REGION=k`)                            |
| `STREET`             | Street type and name in Cyrillic, exactly as on DTEK, including `вул.`, `пров.`, or `просп.`       | `вул. Перемоги`, `пров. Перемоги`                                                                |
| `HOUSE`              | Building number                                                                                  | `1`                                                                                              |
| `CHECK_GRID_STATUS`  | Optional grid status in emergency messages                                                        | `false` disables it; empty or any other value enables it (default)                               |

### 5. Run the workflow

1. Open the **Actions** tab.
2. Select **Run power outages monitor**.
3. Click **Run workflow**, then confirm with **Run workflow** in the dropdown.

### 6. Optional: trigger a check from a phone or another service

No code changes or pushes are needed: configuration lives in secrets, and the token is only used to send the dispatch request. Enable workflows in your fork's **Actions** tab; after forking, GitHub may ask you to click **I understand my workflows, go ahead and enable them**.

To trigger a check with an external HTTP request, for example after losing power, create a [fine-grained token](https://github.com/settings/personal-access-tokens/new) restricted to your fork with **Contents: Read and write**, then send:

```bash
curl -X POST \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Accept: application/vnd.github+json" \
  https://api.github.com/repos/YOUR_USERNAME/dtek-monitor/dispatches \
  -d '{"event_type":"check-emergency"}'
```

- `check-emergency` — checks only the regional emergency popup, without an address request; publishes or updates the message and, if needed, clears it after a second check in the same run.
- `check-dtek` — performs a full standalone check, including the address request.
- `check-regions` — internal shared-bot browser fallback: checks regional notices
  for `client_payload.regionIds` and returns `collectionId`; does not post
  directly to Telegram or check addresses.

Send one request per event: the script performs its own clearance recheck. Workflow concurrency serializes execution; GitHub can replace an older pending run when further requests arrive. Keep the token private and revoke it when no longer needed.

## 🛠 Local setup

### Requirements

- Node.js (version pinned in `.nvmrc`)
- npm

### Installation

```bash
# Clone the repository
git clone https://github.com/YOUR_USERNAME/dtek-monitor.git
cd dtek-monitor

# Install locked dependencies
npm ci

# Install the Playwright browser
npx playwright install chromium
```

### Configuration

Create `.env` in the repository root using `.env.example`:

```env
TELEGRAM_BOT_TOKEN=123456789:ABCdefGHIjklMNOpqrsTUVwxyz
TELEGRAM_CHAT_ID=-12346789
REGION=kr
CITY=м. Васюківка
STREET=вул. Перемоги
HOUSE=1
# Optional: false disables the grid-status request
CHECK_GRID_STATUS=true
```

### Running

```bash
# Run once
npm run start

# Restart automatically when source files change
npm run watch
```

## 🔍 How it works

1. **Collection:** Playwright opens the DTEK website. The hourly scheduled backup checks the regional popup; manual runs and both `check-dtek` and `check-emergency` dispatches also request the configured address.
2. **Assessment:** After confirming there is no regional emergency, the script reads the configured address. DTEK emergency/accident results use a local-emergency message with outage and estimated restoration times, a warning when stabilization schedules do not apply, and the optional Ukrenergo status. When DTEK reports no active outage, the bot confirms that result with a second address check before posting a clearance reply. Scheduled outages are not reported. The hourly backup remains popup-only.
3. **Delivery:** An accidental or emergency outage produces a Telegram notification, sent silently at night (22:00–08:00).
4. **Message updates:**
   - A regional emergency/accident notice is sent as **one message**. Changes to the notice or grid status **edit that message**. In the standalone monitor, a notice announcing active emergency outages in some districts still triggers an alert when stabilization schedules apply elsewhere; an accident (“через аварію”) also takes priority. Plain stabilization notices and advice to avoid emergency outages do not trigger alerts.
   - Clearance is sent as a **reply** only after two checks of a genuine DTEK page in the same run, 30 seconds apart. It **closes the thread**; the next emergency starts a new message.
   - A local address emergency is cleared only after DTEK returns no active outage on two address checks, 30 seconds apart; the bot replies that the local emergency is no longer listed and advises checking again in 15 minutes if power is still out.
   - After clearance, the grid-status line in the clearance message continues to be edited until a new emergency starts.
   - Address outages edit the current message while time and cause are unchanged; otherwise they reply to the previous message. The first message of a new day is separate.

5. **State:** `last-message.json` lives on the separate `artifacts` branch. The workflow mounts that branch at `artifacts/` and commits changes after the script completes. A fresh fork creates the branch on the first run with state to save. Local runs use the ignored `artifacts/` directory.
6. **Workflow enablement:** GitHub can disable scheduled workflows in public repositories after 60 days without activity. Each run requests that its workflow remain enabled through the GitHub API; an already disabled workflow cannot run this step by itself.

## 📱 Example message

Bot messages and upstream notices remain in Ukrainian; examples below preserve the product text.

```
⚡️ Зафіксовано відключення:
🪫 10:30 — 18:00

⚠️ Аварійні ремонтні роботи.

📢 17.11.2025 11:22
🤖 17.11.2025 11:27
```

- 🪫 — outage start and estimated restoration time.
- ⚠️ — outage cause.
- 📢 — last update on the DTEK website.
- 🤖 — last bot message update.

### Regional emergency / accidental outages

When DTEK displays a regional emergency/accident notice rather than a stabilization notice, the bot publishes its text and appends the region's grid status unless `CHECK_GRID_STATUS` is disabled:

```
🚨 Екстрені / аварійні відключення:

Шановні клієнти!
Через атаку на енергообʼєкти на Київщині зафіксовані аварійні відключення у Фастівському, Обухівському і Бучанському районах. В інших районах можливі затримки із включенням по графіку.
Енергетики вже працюють, щоб якомога швидше покращити ситуацію і повернути світло.

🔴 Київська область: Електроенергії значно не вистачає (з 09.10, 06:04)

🤖 09.10.2026 13:10
```

While the notice is active, the same message is edited when its text or the grid status changes.

When the notice disappears, confirmed by a second check in the same run, the bot replies to the message and closes the thread:

```
✅ Екстрені / аварійні відключення скасовано.

Діють стабілізаційні відключення.

🔴 Київська область: Електроенергії значно не вистачає (з 09.10, 06:04)

🤖 09.10.2026 15:40
```

If there is no notice at all, the second line is “Стабілізаційних відключень наразі немає.” The next emergency starts a separate new message.

- 🔴 🟡 🟢 — regional grid status from [ua-power-status](https://github.com/lasercat12/ua-power-status).

## 🤖 Shared bot (optional)

The .NET application in [`bot/`](bot/) lets one bot serve multiple chats and channels. The backend checks sources directly and uses the separate `shared-bot.yml` browser workflow when needed. Collectors use the regions in `bot/src/DtekBot.Api/dtek-settings.json` and send popup observations to `/ingest`; the backend stores state in its database and updates subscribed chats. The standalone monitor retains separate configuration, Telegram delivery, and state.

**Capabilities**

Apart from `/start`, configuration is handled in the Mini App editor rather than Telegram commands. Open it with the “Редактор” menu button beside the input field, installed automatically by the bot, or the button under the `/start` greeting.

- **Region and district subscriptions:** Select one or more districts in the editor. Notices naming only other districts are not delivered; region-wide notices reach every eligible subscriber in that region.
- **Outage-group alerts (currently Kyiv only):** Select a street and building to resolve a group such as `45.1` and receive group emergency alerts. The API collects group statuses from Yasno during its coordinated monitoring cycle, rather than relying on hourly browser collection. Group data can differ from the DTEK website. Legacy `POST /ingest/groups` remains available; collectors should include `observedAt` so delayed observations cannot overwrite newer accepted data.
  A successfully delivered regional emergency suppresses redundant group-emergency
  posts only when it covers every saved address using that group. Partial district
  coverage, other regions, unknown/accident notices, disabled regional messages, and
  failed sends do not suppress local alerts. Group statuses are still collected.
  Each chat keeps one emergency message: a covered group alert that is already open
  continues as the regional alert, and a regional clearance while a covered group
  remains in emergency turns the same message into the group alert instead of
  posting an all-clear and a new alert. Group alerts read
  “⚠️ м. Київ (загально по групі 29.1): …” with “(за даними Yasno)” on its own line,
  because a Yasno group can span the whole city. User-facing text calls these
  groups («група»); the legacy `{черга}` placeholder is still accepted.
  “Джерело сповіщень” in “Повідомлення” selects the source per chat. **Авто**
  (default, recommended) is the combined flow above. **Лише ДТЕК** sends only
  DTEK/Ukrenergo regional alerts, with no Yasno group alerts or handoff.
  **Лише Yasno** sends only group emergency alerts and drops DTEK/Ukrenergo
  emergency, accident, and unknown notices. A chat without a saved group keeps
  receiving them. Switching modes closes an open message from the dropped source
  without posting anything. The additive `AlertSourceMode` migration stores the
  choice; existing chats get Auto. A client that omits `alertSource` keeps the
  saved value.
  The additive `RegionalEmergencyCoverage` migration records delivered coverage;
  legacy open messages gain coverage on their next regional evaluation without a resend.
- **Current status and diagnostics:** The editor distinguishes DTEK notices, Ukrenergo status, and published plans from actual power availability. Diagnostics show the saved region (Kyiv city and Kyiv oblast are distinct), districts, addresses, groups, check timestamps, today's/tomorrow's schedule availability, and automatic publishing settings. Stale or unavailable data is labelled explicitly. A delivery record's update timestamp is state history, not a Telegram message verification or an error log.
- **On-demand address assessments:** In the private-chat editor, open “Основне” → “Адреси та групи” and select “Перевірити відключення за адресою”. The bot evaluates each saved address using active DTEK/Ukrenergo notices and, when a group is known, its schedule and Yasno emergency status. If those data do not explain the outage and an address lookup is available, the bot requests the address from DTEK; queued lookup results arrive separately in the private chat. Kyiv addresses can be selected from a directory; elsewhere, enter the settlement, street, and building exactly as on DTEK. Playwright refreshes directories cached by the API, allowing searches even when direct server requests to DTEK are blocked. The collector sends the same `getHomeNum` request as the DTEK site (`dtek-kem` for Kyiv, `dtek-krem` and other regional sites with the settlement first), including the page's `updateFact` value, and the reply shows DTEK's `updateTimestamp` as “Дані ДТЕК оновлено”. Addresses are retained only while subscribed.
- **Automatic checks after “Світло зникло”:** If a power-monitoring app posts a message such as “🔴 14:24 Світло зникло…” to a channel, add this bot as an administrator, configure the address in the Mini App, and enable “Реагувати на «Світло зникло»”. The bot reacts to matching new channel posts, not historical messages, and replies with an assessment using known group schedules, DTEK/Ukrenergo notices, and Yasno group status. It requests an address lookup from DTEK only when those data do not explain the outage and an address lookup is available.
- **Optional schedule notifications per channel:** Select an address in Kyiv or choose a group manually for Kyiv, Dnipropetrovsk, or Odesa oblast, then enable “Сповіщення за графіком”. A detected group can be overridden manually while retaining the address for DTEK checks. Choose a two-day table image, a compact image, or no image. Schedule posts and outage-event messages have independent delivery modes: **new message**, **delete the old message and publish a replacement**, or **edit the same message**. Replacement requires permission to delete messages; otherwise the old message remains.
  - **Schedule posts:** A separate post shows the group's schedule, usually with an image: “❗️ Оновлений графік відключень на сьогодні / 📅 Четвер 15.10.2026 / 🔴 14:00 - 18:00 (~4 год)”. The first daily post starts with “Графік…”; changes use “Оновлений графік…”. “Надіслати графік у канал зараз” publishes immediately, even when automatic publishing is disabled.
  - **Advance warnings:** A separate message 5, 10, 15, 30, or 60 minutes before a planned group outage. Customize it in “Повідомлення”.
  - **Planned power-off / power-on events:** Examples: “🔴 14:24 Світло вимкнено за графіком / 🕓 Воно було 17год 23хв / 🗓 Очікуємо за графіком о 18:00” and “🟢 18:07 Світло з'явилося за графіком / 🕓 Його не було 3год 42хв”. A return-of-power message is sent only for an outage previously reported by the bot; these are schedule-based events, not voltage measurements. This existing capability remains disabled by the checked-in `Dtek:Schedules:EnableEventNotifications` setting; timetable behavior is outside this review's implementation scope.

  Schedules and ready-made images come from the public MIT-licensed
  [outage-data-ua](https://github.com/Baskerville42/outage-data-ua) repository:
  schedule JSON is under `data/` and images under `images/<region>/`, for example
  [images/kyiv-region](https://github.com/Baskerville42/outage-data-ua/tree/main/images/kyiv-region).
  The backend shares a 10-minute source cache across all chats instead of
  generating images or requesting schedules directly from DTEK.
- **Per-channel settings:** All alerts or emergencies/accidents only, clearance notifications, edit-versus-new delivery, and pause.
- **Deleting previous alerts:** With “Видаляти попереднє аварійне сповіщення” on, a new alert event deletes the previous alert and its clearance notice from the chat (DTEK and Yasno alike). While an event continues, “edit the same message” takes priority; in new-message mode the replaced message is deleted. Telegram allows deletion only within 48 hours of sending and only with the delete-messages right; diagnostics flag missing rights.
- **Private copies of linked chats:** The bot copies real alert, schedule, reminder, and automatic address-check messages from every linked channel/group into each linked administrator's private conversation. Each source has a labelled reply thread; multiple channels and electricity groups remain separate, including combined multi-group schedule posts. Copies follow bot-driven edits, clearance replies, and successful deletions, with durable source-specific message IDs. Your private subscription remains independent and may send its own notifications too. Copies are silent when either the source's delivery or your private quiet hours requires it. Pausing your private subscription does not pause linked-chat copies; pause the source or unlink it to stop those copies. Test sends and previews are not mirrored, historical messages are not imported, and manual Telegram edits/deletions are not tracked. The bot rechecks administrator access before copying; unlinking removes that source's copy tracking. Start the bot privately and keep it unblocked to receive copies. Telegram copy failures are logged without disabling the source channel.
- **Quiet hours:** Configure a daily Kyiv-time interval per chat or channel under “Повідомлення”. Automatic alerts and schedule messages arrive immediately but silently during that interval. Disabled by default; manual actions and test messages are not silenced.
- **Clearance delivery:** Under “Повідомлення”, the “Зняття запису” template supports replying to the original alert (default), sending a new message, or editing the original. If the original has been deleted or cannot be edited, the bot falls back to a new reply message.
- **Custom templates:** Start with a ready-made style, then choose “Налаштувати” to edit formatted text and insert named data chips. “Розширений” retains the raw template editor. Each editable message type has a Telegram-style preview; sending a test is a separate, confirmed action (tests are marked 🧪).
- **Authorization:** Only channel administrators can change channel settings.

New Telegram destinations must be **channels**, not Telegram groups. Electricity
groups/queues are subscription targets, not Telegram chat types. The existing
standalone Node monitor and its private-channel parser are a separate optional
mode; the shared application uses its own API-owned interpretation and delivery.
Do not configure both modes to post to the same destination unless duplicate,
independent streams are intentional.

The lite editor saves settings, region, and address drafts in one API transaction.
Changing region or cancelling a draft does not delete saved addresses. The
settings request's optional `addresses` field preserves existing addresses when
omitted or null; a list replaces them (an empty list clears them). Older clients
retain their separate address endpoint. The alternative editor preserves
settings it does not expose instead of replacing them with defaults.

Address districts use the same searchable multi-select buttons as the main
district selector. Each address can select several districts or all districts;
the default uses the main districts in the same region, or the whole address
region otherwise. These selections apply only after saving.

Private-copy failures are recorded for retry on later delivery/check ticks;
unchanged source messages need not be reposted to retry their copies. These
retries do not provide exactly-once Telegram delivery: cancellation or a crash
between a source send and its database save can still leave an untracked send.
Unlinking stops future copies and removes tracking, not historical Telegram
messages already received.

**Two emergency-outage sources**

The bot can alert from whichever source first reports an emergency:

1. **DTEK website:** Popup observations, including district names when provided, received through `POST /ingest` or direct backend collection.
2. **Ukrenergo:** Regional status from the public [feed](https://github.com/lasercat12/ua-power-status), `data/regions.json`. The backend polls it directly; GitHub Actions can also call `POST /ingest/grid/tick`. The `Grid` section of `dtek-settings.json` configures the feed URL and freshness limit (`MaxAgeMinutes`, 45 minutes).

Rules:

- `emergency_outages` means an emergency; `grid_damage` means an accident. `normal`, `shortage`, and `outage_schedules` (stabilization schedules) are not emergency alerts. A feed older than 45 minutes is ignored: unknown status neither opens nor closes an alert, and fetch failures do not change state.
- If Ukrenergo reports first, the alert is **region-wide**, without district filtering, so all eligible regional subscribers receive it, including those with district selections. The text ends with “(за даними Укренерго)”.
- When DTEK later provides details or districts, the existing message is **edited**, or replaced by a new message in new-message mode. A chat outside DTEK's named districts keeps the applicable region-wide Ukrenergo alert rather than receiving a false clearance.
- **Explicit DTEK status takes priority for its territory**, including cancellation and stabilization schedules. A DTEK stabilization/cancellation notice can close that territory's regional alert even while Ukrenergo still reports an emergency. Later grid ticks do not reopen it while that explicit DTEK status remains accepted. Yasno group-emergency protection still prevents misleading regional clearances.
- This is source priority, not a race between timestamps: even a newly announced Ukrenergo emergency does not override an already accepted explicit DTEK stabilization/cancellation for the same territory. DTEK must change its status before that territory returns to regional emergency mode.
- Yasno group statuses remain independent: a normal/scheduled status closes its group stream but does not override an active regional Ukrenergo alert. The app does not collect a separate Yasno website popup.
- Ukrenergo is the fallback for territories without an explicit DTEK status. An absent, empty, unparsed, failed, or blocked DTEK response is not an explicit cancellation and cannot override an active grid alert. If DTEK only names some districts, unmentioned districts retain the grid fallback. Kyiv city and Kyiv oblast are evaluated separately.
- Alert and clearance messages identify their selected evidence: DTEK website, Ukrenergo feed, or Yasno group data. Attribution is retained even when a custom template omits `{message}`; previews include the sample source too. A notice quoting an Ukrenergo order on the DTEK website is still attributed to DTEK, where it was collected. Message time is not the source's status-change time.
- Regional and district eligibility still applies. Address assessments use the same explicit-DTEK priority; an unresolved active Ukrenergo emergency can explain an outage without an additional DTEK address request.

**Known release limitation:** source fetch failures preserve accepted emergency
records rather than clearing them. Some reconciliation and address-assessment
paths can still use those persisted records without a current-evidence expiry.
The ingest freshness/ordering checks do not prove that a retained alert describes
the current situation. A stricter stale-evidence policy is deferred; no new
freshness thresholds are introduced in this release.

**Message templates** use Telegram HTML (`<b>`, `<i>`, `<u>`, `<s>`, `<code>`, `<pre>`, `<a href="https://…">`, `<tg-spoiler>`, `<blockquote>`) and placeholders:

The lite Mini App combines ready-made styles with a visual editor:

- **Ready-made styles:** choose Short, With address (where the slot supports it),
  or Standard. Replacing a custom template requires confirmation and only changes
  the draft.
- **Customize:** edit text, select text for bold/italic or a link, and insert
  friendly data chips such as Region, Address, and Group. Chips keep the underlying
  placeholder intact. Conditional blocks have paired “If present” / “End” markers.
  Undo/redo buttons are available; pasted content is plain text.
  On touch devices, tap a data chip to place the caret after it, then use the
  keyboard delete key to remove the whole chip without closing the editor.
- **Advanced:** edit the existing Telegram HTML and brace syntax directly. Unknown
  or unsupported syntax opens here unchanged rather than being silently converted.
  Merely switching modes does not rewrite a saved template.

The editors offer Ukrainian placeholder names only. Bootstrap metadata carries
both the Ukrainian `name` and English canonical `key`; no duplicate message text
is needed. Known legacy English tokens display in Ukrainian, while Save, Preview,
and Send test translate only placeholder tokens (including conditional markers)
to English keys before calling the API. Literal text, escaped braces and Telegram
markup are preserved. The API still accepts Ukrainian aliases for older clients;
storage and rendering use English keys.

The schedule interval editor remains plain text with data chips, without HTML
formatting. The API still validates and renders every preview using the existing
slot-specific contract; no new storage format or migration is needed. Previews
do not send messages. Save applies the draft, Cancel restores saved settings, and
Send test names the destination and requires confirmation (including saving all
pending settings when necessary).

Alert templates may use **only structured fields** or custom text: `{текст}` and
`{текст_сайту}` are no longer required. The “Лише дані” style provides a starting
point without the full notice. Empty templates, unknown placeholders, invalid
HTML, and oversized templates are still rejected. Schedule interval templates
retain their required interval fields.

Alerts, schedule headings, and advance reminders expose these data chips:

| Placeholder | Value |
| --- | --- |
| `{регіон}` / `{region}` | Configured region name |
| `{код_регіону}` / `{regionId}` | Configured region ID, such as `kyiv` |
| `{місто}` / `{city}` | Distinct saved cities of matching addresses; empty when unknown |
| `{група}` / `{group}` | Matching electricity groups; one exact group for a local alert |
| `{статус}` / `{status}` | Readable Ukrainian event status, not measured power |
| `{код_статусу}` / `{statusCode}` | Backend event code |

Alert status codes follow `AlertKind`: `Emergency`, `Accident`, `Ended`, or
`Unknown`. `Ended` means the outage record was removed, not confirmed power
restoration. Schedule headings use `ScheduleAvailable` or `ScheduleUnavailable`
according to day/group coverage; reminders use `PlannedOutage`. Matching address
values never combine different regions, and district/group-specific alerts exclude
unaffected addresses. Ukrainian identifiers and legacy English aliases both work.

| Placeholder                           | Meaning                                      |
| ------------------------------------- | -------------------------------------------- |
| `{значок}`                            | Event-type icon                              |
| `{заголовок}`                         | Short title                                  |
| `{текст}`                             | Prepared notification text                    |
| `{регіон}`, `{район}`, `{місце}`      | Region, district/area, and their combination   |
| `{текст_сайту}`                       | Original DTEK notice                          |
| `{час}`                               | Kyiv time                                    |
| `{сайт}`                              | DTEK website link                            |

Placeholder identifiers are Ukrainian application data and must not be translated. Legacy English aliases (`{emoji}`, `{message}`, `{time}`, `{site}`, `{region}`, `{part}`, `{where}`, `{popup}`, `{title}`, `{group}`, etc.) still work, can be mixed with Ukrainian identifiers, and do not require rewriting saved templates.

The default emergency template is `{значок} {текст}`, without a details link or DTEK attribution footer. Custom templates can still include `{сайт}`. Use `{{` and `}}` for literal braces.

A **conditional block**, `{#назва}…{/назва}`, is shown only when its placeholder is nonempty, for example `{#світло_було}🕓 Воно було {світло_було}{/світло_було}`. Blocks cannot be nested.

Each message type has its own placeholders, shown in the editor. Additional placeholders include:

| Message                | Additional placeholders                                                                                                    |
| ---------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Local outage           | `{група}`, `{адреса}` (saved address)                                                                                       |
| Schedule               | `{шапка}`, `{день_тижня}`, `{дата}`, `{відключення}`, `{разом}`, `{завтра}`, `{оновлено}`, `{регіон}`, `{група}`            |
| Power off              | `{час}`, `{світло_було}`, `{очікуємо_до}`, `{тривалість}`, `{регіон}`, `{група}`                                            |
| Power on               | `{час}`, `{світла_не_було}`, `{наступне}`, `{регіон}`, `{група}`                                                            |

The **Mini App** opens from the bot's private-chat menu at `/lite/`. It uses plain HTML and JavaScript without a build step and is lightweight for phones. To connect a **channel**, add the bot as an administrator with posting permission; the channel appears in the editor when the webhook receives `my_chat_member`. For public channels, entering the channel's `@username` in the editor is an alternative. Select a region and address, optionally override the group, and enable schedule images if desired. “Видалити зі списку” removes a channel from your list; if you are its last manager, alerts are disabled and saved address data is removed. The bot stays in the Telegram channel until manually removed. An alternative Blazor WebAssembly editor exists in `bot/src/DtekBot.WebApp` but is not connected to the bot.

Deployments enabling private notification copies must apply the additive
`PrivateNotificationCopies` migration before running the updated API. It creates
copy tracking keyed by source chat, source message, and administrator; no existing
subscriptions or Telegram messages are changed.

**Server configuration** (`appsettings.Production.json` on the host, never committed):

```json
{
  "ConnectionStrings": { "Default": "…" },
  "Database": { "AutoMigrate": false },
  "Bot": { "Token": "…", "WebhookSecret": "…" },
  "Feedback": { "RecipientChatId": 0 },
  "Ingest": { "Secret": "…" },
  "App": { "PublicUrl": "https://your-host.example" },
  "StandaloneMonitor": { "ChatId": -1001234567890 },
  "GitHub": { "Repository": "owner/dtek-monitor", "DispatchToken": "…" }
}
```

Use `AutoMigrate: true` only for initial setup and updates requiring database migrations. **The `ResetApplicationData` migration deletes all application data**: connected chats and administrators, addresses, schedules, Svitlobot keys, and check state. Tables and migration history remain. This is irreversible; back up the database before applying it. Hosting configuration secrets are outside the database and are not deleted. `App:PublicUrl` is the site's HTTPS URL, used for the “Редактор” menu button and the button under the greeting.

Set `Feedback:RecipientChatId` to the positive numeric Telegram user ID that should receive Mini App feedback; `0` disables delivery until configured. The recipient must open the bot and send `/start` before feedback can be delivered. Feedback includes the submitted text and the app/UI versions, and is sent only after the user explicitly submits the form. Requests require valid Mini App authentication, are limited to three per user per minute, and accept at most 2,000 characters. The recipient ID is configuration only and is not stored in the database.

The standalone trigger uses the repository Actions secret or variable
`STANDALONE_MONITOR_CHAT_ID` (prefer a secret if the channel ID should not be
exposed) and the phrase
“Світло зникло”; no Mini App or database setup is needed for that channel.
The existing webhook relays matching posts as `check-channel-outage`
dispatches to `monitor.yml`. The workflow ignores posts whose chat ID does not
match the configured secret/variable, then checks the configured address and
replies in the source post's thread without editing it. It skips the lookup
when DTEK reports a regional emergency or stabilization notice; scheduled
address outages are not labeled local emergencies. The webhook must be online
to receive and relay posts, but it does not perform the address assessment.
Other channels with the shared trigger enabled keep the existing assessment
flow. To check an existing post manually, open **Actions → Run power outages
monitor → Run workflow**, enter its Telegram message ID in the optional
`message_id` field, and start the workflow. The standalone monitor checks the
configured address regardless of the regional popup and edits that post with
the local outage details, including start and estimated restoration times.
If no address outage is listed, it uses the actual DTEK popup instead. If neither
is available or the check fails, it leaves the post unchanged rather than
replacing it with a generic no-outage message. It does not send a separate reply.
The bot must have permission to edit messages in the channel.

**District recognition in DTEK notices.** The shared bot interprets nonempty public popups **LLM first in the API**, for both direct collection and browser ingestion. Its forced `submit_popup_interpretation` function takes only machine-readable arguments: state kinds (`emergency`, `accident`, `stabilization`, `cancelled`), explicit scope, and district IDs from `dtek-settings.json`. It is an internal model tool, not a public HTTP/MCP endpoint. The standalone monitor is unchanged.

This applies to **every configured region**, including Kyiv oblast, not just Kyiv city. Each clause is interpreted separately: an emergency on Kyiv's right bank with schedules on its left bank produces seven Emergency districts and three Stabilization districts, not ten Emergency districts. A whole-Kyiv emergency includes all ten IDs; other whole-region emergencies include that region's complete configured district set. Kyiv city and oblast never share district IDs. Unmentioned districts are not automatically assigned stabilization.

Tool arguments have three distinct outcomes:

```json
{"status":"parsed","states":[{"kind":"emergency","scope":"districts","districts":["bucha","obukhiv","fastiv"]},{"kind":"stabilization","scope":"districts","districts":["brovary"]}]}
{"status":"unable_to_parse","states":[]}
{"status":"no_information","states":[]}
```

`parsed` requires nonempty valid states. `unable_to_parse` means ambiguous/uninterpretable text; `no_information` means no relevant status information. Neither empty result means all-clear. Whole-region states use `"scope":"all"` and list every configured district; an empty district list never implicitly means the whole region in this tool contract. Legacy aggregate ingest responses keep their empty-parts-means-region-wide convention.

The parsing prompt returns structured states only, not Telegram wording.
A bare negated transition such as “Екстрені відключення не скасовано.” is
ambiguous unless another clause explicitly establishes the active state.
Default regional alerts use a concise status without a details link or DTEK
attribution footer, followed by optional address/region context. Ukrenergo and
Yasno attribution is retained so those sources remain distinguishable.
Unknown notices explicitly say that the outage status is undetermined.
A disappeared notice says cancellation is unconfirmed; a return to schedules
reports schedules applying without claiming a regional emergency has ended.
Neither confirms measured power. Alert copy states what the bot monitors next;
Svitlobot sensor events remain separate from regional notices and planned schedules.
Saved custom templates and their placeholders remain unchanged.

Missing credentials, provider timeout, exhausted credit/quota, unsupported tool calling, invalid results, or an unable-to-parse answer trigger deterministic clause-aware parsing. A model's empty-information answer also falls back if the deterministic parser recognizes a state. Inputs over 4,000 characters are parsed completely by fallback rather than sending a misleading truncated notice. Unknown IDs, conflicting district states, and ungrounded geographic selections are rejected, not silently filtered. Successful model results are cached in API memory for 24 hours (up to 256 entries), keyed by region, text, settings, model, provider, and schema version. Provider failures have a 30-minute cooldown; restarting the API clears this cache. The workflow no longer maintains a popup cache on `bot-artifacts`.

A recognizable blackout/emergency without reliable district scope is region-wide. Uninterpretable or irrelevant nonempty notices send a region-wide **Unknown** alert unless an applicable active Ukrenergo fallback provides a known status. Only a verified absent popup or explicit status transition can clear DTEK alerts; failed, blocked, missing, or older observations do not. District-specific stabilization ends that district's regional alert with schedules wording, never a claim of measured power restoration, even if the broader Ukrenergo feed remains active. An absent popup alone does not override an active Ukrenergo alert.

Configure provider credentials **on the API host**, not just in GitHub Actions: `Llm__ApiKey`, with optional `Llm__BaseUrl` and `Llm__Model` environment variables, or equivalent private hosting configuration keys `Llm:ApiKey`, `Llm:BaseUrl`, and `Llm:Model`. Legacy `LLM_API_KEY`, `LLM_BASE_URL`, and `LLM_MODEL` names are also accepted by the API when the corresponding `Llm` key is absent. Never commit the key. GitHub Actions no longer calls the model during regional collection.

The default endpoint is `https://generativelanguage.googleapis.com/v1beta/openai`, with model `gemini-3.1-flash-lite`. Obtain a key from [Google AI Studio](https://aistudio.google.com/apikey), or use an OpenAI-compatible provider that supports forced function calls. Check model availability, pricing, and data-use terms; only public notice text and configured geography/classification definitions are sent. API billing is separate from a ChatGPT subscription.

Deploy the additive `PopupInterpretation` EF migration alongside this API version. It adds nullable interpretation/provenance fields to regional state without resetting existing data; legacy rows remain readable. Prepare and review migrations locally, back up the database, and apply them only through the authorized deployment process.

`GitHub:DispatchToken` is a [fine-grained token](https://github.com/settings/personal-access-tokens/new) restricted to this repository with **Contents: Read and write**, as in the external-trigger setup. It dispatches `check-addresses` to `shared-bot.yml`. Without it, queued requests depend on a later backup run; the hourly cron is not a guaranteed deadline. The same token dispatches `check-emergency` to the standalone `monitor.yml` every `Dtek:Timers:MonitorWorkflowIntervalMinutes` (default 15, `0` disables it). Keep the token only in private hosting configuration, such as `appsettings.Production.json`; never log it.

Shared-bot business decisions and source collection cadence belong to the API.
The shared workflow's backup run requests the secret-protected
`POST /ingest/monitoring/tick`; browser workers supply observations and queued
address results, not classification or delivery decisions. Yasno group collection
uses a separate API collector, never the standalone private-channel parser.

To limit GitHub Actions and DTEK load, `AddressChecks` in `dtek-settings.json` defines per-chat cooldowns, daily budgets, result caching, a minimum dispatch interval, shared checks per street, and a maximum number of streets per run.

Backend timings live in the `Timers` section of
`bot/src/DtekBot.Api/dtek-settings.json`:

| Setting | Default | Meaning |
|---|---|---|
| `Timers:MonitoringIntervalMinutes` | 5 | Coordinated monitoring cycle (1–60) |
| `Timers:MonitoringStartDelaySeconds` | 30 | Delay before the first cycle after startup |
| `Timers:MonitorWorkflowIntervalMinutes` | 15 | `check-emergency` dispatch to `monitor.yml` (5–60; `0` disables it) |
| `Timers:MonitorWorkflowStartDelaySeconds` | 60 | Delay before the first dispatch after startup |
| `Timers:BrowserWaitSeconds` | 180 | Wait for the `check-regions` browser fallback (1–240) |
| `Timers:ClearConfirmMinutes` | 10 | A missing DTEK popup ends an alert only when a second popup-less check at least this much later agrees (0–120; `0` ends at once) |
| `Timers:SvitlobotWeeklyRefreshDay` | `Monday` | Kyiv day of the weekly forced Svitlobot rewrite |
| `Timers:SvitlobotWeeklyRefreshTime` | `03:00` | Kyiv time of that rewrite; it runs on the first cycle at or after it |
| `Schedules:CacheMinutes` | 10 | How often Baskerville is checked for new schedules |

The file reloads on save: a changed interval applies from the next tick and is
shown in `/health`. Turning a timer on or off (`0`) and `Ticks:Enabled`, which is
a hosting setting, need an application restart.

The backend runs a coordinated cycle every 5 minutes (disable with
`Ticks:Enabled=false`). DTEK popups and the Ukrenergo feed are
collected in parallel with schedule reads. Baskerville is checked every 10
minutes (`Schedules:CacheMinutes`) on the cycle that reaches that interval;
keep it a multiple of `MonitoringIntervalMinutes`. Intervening cycles and Mini App reads
reuse that copy rather than forcing another network request. Due refreshes use
ETag conditional requests. A failed request backs off for the same window and
keeps the last usable copy, without treating unavailable data as an all-clear.
The first cycle starts after 30 seconds; cycles do not overlap. Only after source
collection completes does the backend reconcile alerts, evaluate schedule posts
and reminders, and sync Svitlobot. Notification evaluation stays at 5 minutes;
the 10-minute Baskerville interval does not slow it down.

| Function | Primary trigger | Separate GitHub workflow / backup |
|---|---|---|
| Personal regional-popup monitor (`src/monitor.js`) | Backend `MonitorWorkflowTimer`: every 15 minutes, first dispatch after 60 seconds | `monitor.yml`: `check-emergency`, manual / `check-dtek`, hourly cron |
| Baskerville schedules and schedule notifications | Backend cycle: 5-minute evaluation, Baskerville checked every 10 minutes | `schedule-refresh.yml`: 10-minute best-effort cron |
| Shared-bot regional browser fallback | Backend dispatches `check-regions` only when direct DTEK collection fails | `shared-bot.yml`: correlated region results; hourly backup |
| Shared-bot address checks | On demand, after schedule and regional-emergency assessment | `shared-bot.yml`: `check-addresses`; hourly backup drains queued requests |

The personal monitor timer needs `GitHub:Repository` and `GitHub:DispatchToken`.
`Timers:MonitorWorkflowIntervalMinutes` defaults to 15; set it to `0` to disable personal
monitor dispatches without disabling shared-bot monitoring. An accepted dispatch
does not guarantee an immediate GitHub runner start. Failed dispatches are logged
and retried at the next interval. GitHub cron remains a best-effort backup, not a
guaranteed retry deadline. Deploy the backend and put the workflows on the
repository's default branch to activate this split. Run one backend instance:
timers, caches and duplicate-prevention gates are process-local.

If direct DTEK access is blocked, including HTTP 200 containing an Imperva page,
the bot dispatches `check-regions` using `GitHub:DispatchToken`. The browser
workflow (`shared-bot.yml`) checks only unavailable regions, in parallel;
`collectionId` correlates replies with the current cycle. The wait is bounded by
`Timers:BrowserWaitSeconds` (default 180 seconds, maximum 240). Failed dispatches,
timeouts, and unavailable sources are logged; previous state is preserved,
not interpreted as an all-clear. Late replies pass through normal ingest,
and older observations cannot overwrite newer ones. GitHub Actions does not
guarantee runner start time.

Backup workflows call `POST /ingest/schedules/tick` (`schedule-refresh.yml`,
every 10 minutes) and `POST /ingest/grid/tick` (`shared-bot.yml`) with
`X-Ingest-Secret` and `Content-Length: 0`. These compatibility endpoints do not
start a full collection cycle; they wait for an active coordinated cycle to
finish rather than publishing intermediate state.
**IIS must keep the application running** (Always On, no idle timeout, or an
external keep-alive). In-process timers cannot run while the process is stopped.

To verify timers after a redeploy, open `GET /health`. Its `background` section
shows `startedAt`, the tick state with `lastCycleAt` and `cycles`, and the
monitor workflow state with `lastDispatchAt` and `lastDispatchAccepted`. Times in
`/health` and the log file are Kyiv time with the offset (`+02:00` or `+03:00`);
log files roll over at Kyiv midnight. The same
events appear in `logs/app-<date>.log`: `Application started` (with the build
version), `Built-in ticks started`, `Monitor workflow timer started` (or why a
timer is off), `First monitoring cycle finished`, one `Monitor workflow
dispatched` line per dispatch, and an hourly `Monitoring cycles alive` line.
`Application stopping` marks a redeploy, recycle, or idle shutdown. A gap after
it means the timers were not running.

When a chat is enabled or changed during an active emergency, the bot delivers the applicable current alert immediately instead of waiting for another website change.

**Deployment**

1. Build and publish `bot/src/DtekBot.Api`; it includes the Mini App.
2. Register the webhook, allowing `message`, `callback_query`, `channel_post`, and `my_chat_member`:
   `https://api.telegram.org/bot<TOKEN>/setWebhook?url=https://your-host.example/webhook&secret_token=<WebhookSecret>&allowed_updates=["message","callback_query","channel_post","my_chat_member"]`
3. Add repository secrets `INGEST_URL` (`https://your-host.example/ingest`) and `INGEST_SECRET`. Both are required; the shared-bot workflow reports a configuration failure if either is missing.

The bot writes logs to `logs/app-<date>.log` beside the application.

**Svitlobot (optional).** Each connected Telegram channel using [svitlobot.in.ua](https://svitlobot.in.ua) can have its own `channel_key` in the Mini App. One user can manage several channels; keys, groups, and automatic sync belong to the selected channel only. A button uploads the current Monday-to-Sunday week from the source data: days with a published daily schedule use it (earlier days of the week keep the schedule that was published for them), and other days use the typical weekly outage plan. Possible outages in the typical plan count as outages by default; `includeMaybe: false` includes only definite ones. A matching group preset is selected when available and can be changed. This updates Svitlobot schedule data, not an immediate Telegram message; Svitlobot controls its own posting and pinning. To post immediately through this bot, use “Надіслати графік у канал зараз”. The delete action removes the Svitlobot schedule and its channel post. Because a key authorizes schedule changes, it is stored only encrypted with AES-GCM, never returned or logged. Requests to Svitlobot are limited to one per minute per chat, following Svitlobot's integration rules; channel reads are cached for 5 minutes. All actions require management access. “Забути ключ” removes the key from this bot only, leaving the Svitlobot schedule intact. The key is also erased when the last manager removes the chat.

The key controls are under **Main → Svitlobot**. The Schedule tab includes a
“Налаштувати Світлобот і ключ каналу” shortcut, even before an address/group is chosen.
An unconnected channel shows the key field immediately; a connected channel offers
“Змінити ключ” to enter a replacement without first forgetting the working key.
Cancel clears the replacement draft without sending a request. Keys remain hidden
after a successful save; changing a key does not publish a Telegram message.

Schedules for saved keys synchronize automatically after source changes. Each week at `Timers:SvitlobotWeeklyRefreshDay`/`SvitlobotWeeklyRefreshTime` (Monday 03:00 Kyiv time by default), the bot resends the weekly plan so Svitlobot draws the new week from fresh data. When the plan becomes empty, the bot clears a schedule it wrote earlier with `removeChannelTimetable`, which also removes Svitlobot's schedule post from the channel; schedules the bot never wrote are left alone. Svitlobot only accepts a plan with at least one full outage hour, so a plan with only half-hour outages is skipped. A plan Svitlobot rejects (`invalid data`, unknown key) is not resent until the plan or key changes; network errors are retried after 30 s, 1, 2, then 5 minutes.

Configure an encryption key in the server's `appsettings.Production.json`; without it, the UI reports “Світлобот не налаштований на сервері”:

```json
{ "Svitlobot": { "KeyEncryptionKey": "<32 random bytes encoded as base64>" } }
```

Generate it with PowerShell:

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

Keep the encryption key safe. Changing it makes previously stored channel keys unreadable, requiring users to enter them again. Override the API URL with `Svitlobot:BaseUrl` if needed; the default is `https://api.svitlobot.in.ua`.

## ⚙️ Settings

### Changing check frequency

Set `Timers:MonitorWorkflowIntervalMinutes` in `dtek-settings.json` for the primary personal-monitor dispatch interval. To change the backup cron, edit `.github/workflows/monitor.yml`:

```yaml
schedule:
  - cron: "*/30 * * * *" # Every 30 minutes
```

Examples:

- `*/10 * * * *` — every 10 minutes
- `0 * * * *` — hourly
- `0 */2 * * *` — every 2 hours

GitHub does not guarantee exact start times: scheduled runs can be delayed or dropped under load, leaving gaps of several hours in observed run history.

The default personal-monitor backup is `7 * * * *` (hourly). The shared backend dispatches `check-emergency` every 15 minutes (`Timers:MonitorWorkflowIntervalMinutes`); an external scheduler can use the same request described in the phone-trigger section. Scheduled runs and `check-emergency` check only the regional popup; manual workflow runs and `check-dtek` also check the configured address.

## ⚠️ Limitations

The standalone monitor cannot collect data if `/shutdowns` or its form is unavailable. DTEK may be overloaded during widespread emergencies; a failed collection publishes nothing and a subsequent run retries. The backend normally dispatches every 15 minutes, but GitHub runner timing is not guaranteed. Emergency clearance is never published unless the page is confirmed to be a genuine DTEK page.

## 🔧 Retries

The standalone script retries failed operations:

- Up to five retries for collection and message delivery.
- A five-second delay between retries.
- Emergency clearance requires a second check after 30 seconds in the same run.
- If all attempts fail, the GitHub Actions run fails.

## 🆕 Updates

See this repository's [Releases](https://github.com/pashamed/dtek-monitor/releases)
and [CHANGELOG.md](CHANGELOG.md) for version changes. Upstream changes remain
available in [upstream releases](https://github.com/mr-devboy/dtek-monitor/releases).

## CI, versioning, and releases

GitHub Actions runs the pipeline; a Copilot subscription is not needed to execute
it. Copilot review can be enabled separately, but does not replace passing tests
or approval of a release.

### One-time GitHub setup

1. In **Settings → Actions → General → Workflow permissions**, enable
   **Allow GitHub Actions to create and approve pull requests**. The release
   workflow requests its own scoped write permissions; ordinary CI is read-only.
   No additional PAT or deployment credential is needed.
2. In **Settings → General → Pull Requests**, enable squash merging and select
   **Pull request title** as the default squash commit message. Prefer disabling
   merge commits and rebase merging so the validated title reaches `main`.
3. Protect `main` with a ruleset requiring pull requests and passing checks:
   **Node and Mini App tests**, **.NET build and tests**, and
   **Conventional PR title**. Select the actual check contexts shown after the
   first workflow run. Merge groups run CI too; their title check succeeds without
   revalidating GitHub's generated merge-queue commit title.
4. Optionally require a human review or automatic Copilot review. Never add
   production credentials to CI, and do not enable live Telegram/LLM tests.

### Change and release flow

- Open a PR with an English Conventional Commit title: `fix(api): preserve unknown
  states`, `feat: add schedule previews`, or `feat!: change the ingest contract`.
  Squash-merge with that title; ordinary titles such as `improvements` cannot be
  categorized by release-please. Direct pushes bypass the title check and should
  be blocked by the ruleset.
- PR CI installs the locked Node dependencies and Chromium, runs every
  `src/*.test.js` test, checks version consistency, builds the full .NET solution,
  runs offline xUnit tests, and smoke-tests the IIS package. Test results are
  retained as an Actions artifact for 14 days. The production collectors and
  notification workflows are not run as tests.
- On `main`, **Release** runs the same CI before release-please updates a release
  PR. `fix` and `perf` produce patch releases, `feat` produces minor releases,
  and `!` / `BREAKING CHANGE:` produces a major release. Documentation and other
  maintenance entries can appear alongside releasable changes; they do not
  independently force a release.
- Release-please updates `package.json`, `package-lock.json`,
  `.release-please-manifest.json`, `bot/Directory.Build.props`, and the changelog.
  The API, shared library, and alternative Blazor editor inherit the same .NET
  version. Do not edit version numbers or create release tags manually.
- The default `GITHUB_TOKEN` cannot trigger PR workflows through a bot-created
  PR. The release workflow explicitly dispatches CI and title validation on the
  release PR branch, so these checks run without a PAT.
- Review the release PR's changelog and breaking changes, then squash-merge it.
  After CI passes on `main`, release-please creates `vX.Y.Z` and a GitHub Release.
  The same workflow checks out that tag and attaches
  `dtek-bot-vX.Y.Z-iis.zip` and `SHA256SUMS.txt`. It does not rely on a separate
  `release` event, which `GITHUB_TOKEN` would not trigger.

Release notes/tag creation happens before packaging. Wait for the **package** job
to pass and both assets to appear before deploying. If packaging/upload fails,
use **Actions → Release → Run workflow**, select `main`, and enter the existing
tag to rebuild its assets without making another version. This requires a tag
that already contains the packaging scripts. A retry replaces the ZIP and its
checksum; always download the matching pair.

### Manual IIS deployment

Deployment remains manual: no runner connects to the server, no workflow changes
the webhook, and no workflow applies database migrations.

**Recommended:** download the ZIP and checksum from a successful release. Verify
the ZIP with `Get-FileHash .\dtek-bot-vX.Y.Z-iis.zip -Algorithm SHA256` and compare
it with `SHA256SUMS.txt`. The archive contains the framework-dependent API, Mini
App, `web.config`, default `appsettings.json`, and `dtek-settings.json`. IIS needs
the .NET 10 Hosting Bundle. Environment-specific `appsettings.*.json` files are
excluded: keep production credentials/configuration on the server.

If continuing the existing git-pull-and-publish process, deploy the release tag,
not the moving `main` branch. In a clean deployment checkout, run:

```powershell
git pull --ff-only
git fetch --tags origin
git switch --detach vX.Y.Z
node scripts\check-release-version.mjs vX.Y.Z
.\scripts\package-release.ps1 -Tag vX.Y.Z
```

Replace `vX.Y.Z` with the reviewed release tag. The script writes the same package
shape and checksum to ignored `artifacts/releases/`; it does not deploy. Prefer
the CI-produced ZIP for the exact tested artifact. Return this checkout to
`main` before the next `git pull`; use a separate checkout if it contains local
development changes. To build locally, the .NET SDK selected by `bot/global.json`
and Node.js are required.

Before copying files, back up the database, production settings, and the previous
application package. Review pending EF migrations and apply them only through
the authorized deployment process; the API currently applies pending migrations
at startup. Stop the IIS application pool while replacing binaries, preserve
server-only configuration and logs, then start the pool and check `GET /health`
and the startup log's version. Never run old binaries against an incompatible
schema: rollback may require a reviewed database restore as well as the previous
package.

## 📄 License

MIT License — free to use for personal and commercial purposes under its terms.


### Message templates

The “Екстрені відключення” editor combines emergency onset, district accidents,
and clearance. A state selector opens the corresponding text and settings.
Existing per-state templates are retained; the backend determines districts.

A schedule is one post with today and tomorrow sections. Each day's heading and
date appear once, followed by groups and addresses without duplicates within that
day. An unpublished tomorrow schedule is explicitly labelled unavailable.
The editor has two parts: “Заголовок і дата” and “Інтервал {відключення}”.
The heading supports `{шапка}`, `{день_тижня}`, and `{дата}`, using the date
and weekday of its own section. `{зміни}` shows planned intervals added or
removed since the previous schedule for that date and can be placed in a custom
heading. The default heading has no change summary; add `{зміни}` to a custom
heading to show it. Comparisons use planned
intervals for the same date and group, not measured time without power.
Icons and text are customizable.
“Редагувати рядок графіка” under “Графік” opens the interval editor directly.
Channel management and removal are under “Основне”.
Schedule headings and accident templates support backend values `{регіон}`,
`{район}`, and `{адреса}`. Missing districts and addresses are empty; use
`{#район} · {район}{/район}` and `{#адреса}\n📍 {адреса}{/адреса}`.
District accidents include only matching addresses.
The legacy `schedule` template remains stored for compatibility but no longer
controls the post. Separate dates, totals, and update timestamps are not appended
automatically. This applies to both single-group and multi-group schedules.

`{відключення}` repeats one outage interval, formatted by
“Інтервал {відключення}” (the `schedule-line` template), for example:
`⚡ {початок} → {кінець} · {тривалість_інтервалу} · {частина_доби}`.
`{дата}` and `{день_тижня}` are also available. Interval lines support text
and icons, not HTML; headings support Telegram HTML. Spaces and separators
are preserved for every interval on both days. Preview and test actions show
the complete post. Substitution uses the existing brace-template parser,
not regular expressions.
Enable **“Показувати періоди зі світлом”** under **“Вигляд графіка”** to show
alternating outage and light periods for each day and group, followed by the total
planned outage duration. There is no hourly threshold. Light means no planned
outage, not confirmed power. Unpublished or incomplete days remain unavailable.
The option is off by default; existing posts and interval templates are unchanged
until enabled. The enabled mode uses minute-based durations, merges overlapping
outages, and computes gaps within Kyiv calendar days (including daylight-saving
changes). Midnight ends remain `24:00`.
Customize light rows separately with **“Інтервал зі світлом”**
(`schedule-light-line`); outage rows still use `schedule-line`.
Long timelines fall back to text and split into Telegram-sized messages; edits
and delivery deduplication apply to those parts as well.
The API persists `scheduleShowLightPeriods`; omitted/null settings preserve the
saved choice for older clients. Preview requests accept the same boolean
(default `false`) and `scheduleLightLineTemplate` alongside the existing heading
and outage-row templates. Previews never send messages.

Accident templates also support `{дата}`, `{день_тижня}`, and `{частина_доби}`,
all based on Kyiv time.

The Svitlobot settings card explains that unpublished days use a typical weekly
plan, not a confirmed daily schedule. Showing the full week does not mean DTEK
has published daily schedules through Sunday. This explanation is in the Mini
App; Svitlobot controls its own graph labels.

### Schedule viewing and clear notifications

- **Schedule viewer (“Переглянути графік”):** A read-only Mini App mode.
  Choose a region and group without connecting a channel, saving a subscription,
  or posting messages. It shows today and tomorrow; an unpublished day is not
  treated as an empty schedule. The next-transition countdown is plan-based.
  Known emergency restrictions display a warning that the plan may not apply.
- **Emergency/accident-only preset (“Лише аварійні та екстрені”) and compact
  formatting:** Applied only after explicit selection and confirmation, then
  persisted with the normal save button. Viewing and presets do not send messages
  or unpause a chat.
- When DTEK explicitly announces stabilization schedules, the default clearance
  explains the transition from emergency to hourly planned outages. A record
  simply disappearing means only that the website no longer displays it.
- No address outage record, no planned outage, and actual restoration of power
  are different things. Website and schedule data do not measure voltage.
  Custom templates and the chosen thread-closing mode (edit, reply, new message)
  are preserved.

### Live Telegram integration tests

`LiveTelegramTests` are skipped by default. To run them, set `DTEK_LIVE_TELEGRAM=1`,
`Bot__Token` to a separate test bot token, and `TELEGRAM_TEST_CHAT_ID=-1004471996729`,
then run
`dotnet test bot\tests\DtekBot.Tests --filter "FullyQualifiedName~LiveTelegramTests&FullyQualifiedName!~EveryDistrict"`.
The local `.env.telegram-test` file is ignored by Git; load its values into the
test process without printing them. Never use a production bot token.

These tests use an isolated SQLite database, labelled simulated events, actual
Telegram sends/edits/deletes, and finally delete the messages they created.
`PowerLossWebhook_ChecksScheduleBeforeQueueingAddressLookup` injects labelled
channel posts into the isolated `/webhook` endpoint. It verifies planned-outage
replies without address requests, outside-schedule queueing and result delivery,
and district-emergency explanations without another lookup. Telegram delivery
is real; the clock, schedule, workflow trigger, and address results are controlled.
It does not verify Telegram forwarding an external sensor post to the hosted
webhook or execute the live DTEK browser collector.
The transport rejects requests targeting another chat and does not change the
bot's webhook or menu. The Mini App API test signs local test authentication
data and checks the test bot's real admin membership; it is not a real Telegram
Mini App login. Multi-group image previews are checked through `messages`;
the legacy `html` and `imageUrl` fields describe only the first post.
Test-send limits remain five requests per chat per minute and are isolated
per application host, including the live fixtures.
Schedule/grid source and photo delivery checks use public live
feeds. The public-source test checks every configured grid region and every
region with a configured schedule source. A source publishing `fact.data=[]`
is explicitly reported as unavailable, not as a successfully delivered
schedule; unexpected failures still fail the test. GitHub workflow dispatch,
DTEK address scraping, Svitlobot writes,
production hosting timers and mobile Telegram UI are not covered by these live tests.

`ReceivedEvents_RespectGeographyClosureSettingsAndCronScheduleChanges` additionally
saves each clearance mode through the settings API, receives emergency and
stabilization popups through `/ingest`, local statuses through `/ingest/groups`,
and grid statuses through `/ingest/grid/tick`. It checks city/oblast isolation,
group matching, duplicate suppression, and all three saved clearance modes
against real Telegram operations.
The simulated bot-admin link authenticates editor operations, but private-copy
fanout excludes the bot's own ID; the allowlisted-chat transport restriction
remains unchanged. Each geography case resets saved address memberships as well
as the primary region so the previous case cannot remain subscribed.
The grid replay confirms a missing DTEK popup before starting a fallback alert;
an explicit DTEK stabilization notice closes it and prevents later grid ticks
from reopening it.
The current local group-status source is
configured for Kyiv only; unsupported regions are checked for explicit rejection,
not counted as successful local-alert delivery. Controlled changed snapshots are delivered
through `/ingest/schedules/tick` in Edit, New, and ReplaceDelete modes, with
repeat ticks verified as no-ops. The source providers and database are isolated;
this does not inject simulated events into the hosted production application.
`DeliveryFanoutTests` checks the same ingest/cron recipient fanout offline with
multiple eligible subscribers and inactive, paused, and unsubscribed controls.

`AlertSourceModes_KeepOneEmergencyThreadOrFilterSourceInTestChannel` replays
labelled DTEK and Yasno events through the real dispatcher. It checks that Auto
keeps one emergency message across the handoff and closes it with one reply.
It also checks that DTEK-only and Yasno-only each skip the other source.

`BrowserSettings_SaveReloadValidateAndSendToRealTestChannel` runs installed
Playwright Chromium against the actual lite editor, an ephemeral loopback HTTP
proxy, and the isolated test API/database. It checks save/reload behavior for
pause, district selection, channel trigger, address name/district, schedule
publishing/reminders, quiet hours, cleanup and clearance mode. It verifies the
three-address UI limit, invalid-template blocking, a deliberately injected
failed-save response followed by a real successful retry, and actual test-alert
and schedule delivery to the allowlisted Telegram channel. Mobile and desktop
screenshots are written under ignored `artifacts`. Telegram WebApp initialization
is simulated with signed fixture authentication; this is not a mobile Telegram
login, hosted-production UI test, live address-directory lookup, or live Svitlobot
key/write test. Run it with the Telegram opt-in variables above and
`dotnet test bot\tests\DtekBot.Tests --filter FullyQualifiedName~BrowserSettings_SaveReloadValidateAndSendToRealTestChannel`.

The following Node LLM scripts are **legacy geography-only diagnostics**, not validation of the API's active LLM-first tool pipeline. Use the mocked `PopupInterpretationTests` and `PopupRoutingTests` for offline coverage of that pipeline.

For the legacy district ingest-to-Telegram matrix, first populate the ignored
`.env.llm-test` with `LLM_API_KEY`, `LLM_BASE_URL`, and `LLM_MODEL`. With
`DTEK_LIVE_LLM=1`, run
`node --env-file=.env.llm-test src\live-routing-inputs.js` from the repository root.
This reads all configured DTEK regional pages in parallel, then asks the configured
real model to identify districts in labelled, controlled public-text replays.
Only public notices and configured geographic names are sent to the model.
Successful answers are validated and grounded against the source text. Results
are saved to ignored `artifacts\live-routing-inputs.json`; provider errors are
reported, not replaced with fabricated successful answers. To retry only failed
district cases, add `--retry-failures` (successful cases are retained).

Set `DTEK_ROUTING_INPUTS` to the absolute path of that result file, retain the
Telegram opt-in variables above, and run
`dotnet test bot\tests\DtekBot.Tests --filter FullyQualifiedName~EveryDistrict_DeterministicAndRealLlmInputs_IngestToLiveTelegram`.
The matrix exercises every configured district through the real `/ingest`
endpoint, isolated persistence, saved templates, real Telegram sends/edits,
duplicate suppression, stabilization clearance, and cleanup. It also ingests
the captured current website popups. Allow roughly 12 minutes for Telegram
rate limiting. These are controlled replays, not evidence of current emergencies
in those districts. The exhaustive model calls deliberately test the model
directly; they do not exercise the API-hosted forced-tool client. Legacy `structured` geography hints remain accepted in ingest payloads but do not override the API interpreter.
The offline city/oblast routing matrices additionally cover matching,
nonmatching, region-wide subscriptions and both source arrival/clearance orders.

### Multiple addresses and groups

The lite editor stores up to **three addresses total across regions**. Each address
has its own region, several groups, and an optional label such as “Під’їзд 1”.
One channel can follow Kyiv city and Kyiv oblast simultaneously. “Знайти адресу” opens
settlement, street, and building lookup (street and building only for Kyiv city).
Changing the channel's main region changes the legacy/default selection, not the
regions of existing addresses. Address edits and general settings are saved together;
Cancel leaves the server's saved addresses unchanged.
Reloading restores the last channel and tab; a single chat opens without a
selection step. Older clients can save general settings without deleting the
new address list. Replace the full list through
`PUT /api/chats/{chatId}/addresses` (`SaveAddressesRequest`); the DTO's `addresses`
field is optional. Blazor remains compatible for general settings, while
address-list management is available in the lite Mini App.

Choose **Table for two days**, **Compact image**, or **No image** under Schedule →
Publishing → Image. Image modes publish a separate schedule and picture for each
region/group, including channels with multiple addresses. With no image, all groups
within a region are published in **one text post** with sections and labels.
A group shared by several addresses in that region appears once. Different regions
have independently tracked posts and reminders; mixed-region messages name the region.
Identical group numbers in different regions are separate subscriptions. Edit, new-message, and
replace-with-deletion modes are preserved. Long posts are split to fit Telegram
limits. Previews and confirmed test sends show the same separate group posts in
image modes. Each group post has independent delivery tracking; failed groups retry
without republishing successful groups. The `GroupScheduleImages` migration expands
the post key to chat/region/group and preserves existing tracked messages. Back up
the database before deployment; reverting the migration requires consolidating
multiple group records per region first.
There is no building-wide ANY/ALL aggregate: each group is shown separately.
An advance warning for a shared outage start is sent once for all matching
groups. Quiet hours mean silent delivery, not delayed delivery.

Svitlobot keeps **one weekly timetable per channel**, so it follows **one selected
address, region, and group**. In the Mini App's Svitlobot card, a channel with several groups shows
a «Зробити основною» button for each group. Until one is chosen, or if the
chosen group is removed, the first group of the first address is used. Changing
the main group writes that group's week from its address's region on the next sync; manual uploads respect
`includeMaybe`. If the main group's weekly data is unavailable, synchronization
waits. Monday refreshes are preserved. Svitlobot channel posts
provide **only the trigger text**, “Світло зникло” or a custom phrase;
their estimated restoration times, durations, and other content are not a
schedule source.

The trigger assesses every address and replies briefly for each. Unresolved
cases are checked at DTEK as one batch within a single chat budget; completed
batch results arrive in one message. The DTEK collector passes the full
`sub_type_reason` array, filling detected groups for addresses without a group
selection. Manually selected groups are not overwritten. Alerts and assessments use
each address's region and districts; group accidents have a separate region/group
stream naming matching addresses. Checks share one chat budget but cache and apply
results by region, so identical address text in two regions cannot mix results.

Migration `20261020213320_MultiAddressGroups` adds `ChatAddresses` and
`ChatAddressGroups`, migrates the legacy address/group without clearing data,
keeps legacy columns readable, and preserves message IDs. The migration is
included in the repository; development work does not apply it to production.
Migration `20261101150040_MultiRegionAddresses` adds the Svitlobot target region/address
and expands schedule thread keys to include the region (and electricity group for
event threads). Existing message IDs and addresses are retained. Apply this migration
with the release; no production migration is performed during development.
Back up the database and use the normal controlled migration process for a
separately authorized deployment.

## Agent skills and engineering audit

Repository-scoped skills are organized by workspace: shared workflows in
`.agents/skills/`, JavaScript in `src/.agents/skills/`, and .NET in
`bot/.agents/skills/`. The JavaScript browser skill also covers the lite Mini App
inside the API project. See [the skill guide](docs/agent-skills.md) for discovery,
usage, sources, and updates, and [the engineering audit](docs/project-audit-2026-10-22.md)
for findings and validation limits. Follow [AGENTS.md](AGENTS.md) for repository
commands and rules.
