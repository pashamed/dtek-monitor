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

1. **Collection:** Playwright opens the DTEK website. Scheduled runs and `check-emergency` check the regional popup; manual workflow runs and `check-dtek` additionally request the configured address.
2. **Assessment:** The script reads the outage cause, start time, and end time. It sends no address notification if there is no outage or the outage is scheduled.
3. **Delivery:** An accidental or emergency outage produces a Telegram notification, sent silently at night (22:00–08:00).
4. **Message updates:**
   - A regional emergency/accident notice is sent as **one message**. Changes to the notice or grid status **edit that message**; stabilization notices are not treated as new emergencies, unless the same notice also reports an accident (“через аварію”), which is then sent to the named districts.
   - Clearance is sent as a **reply** only after two checks of a genuine DTEK page in the same run, 30 seconds apart. It **closes the thread**; the next emergency starts a new message.
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
- **Outage-group alerts (currently Kyiv only):** Select a street and building to resolve a group such as `45.1` and receive group emergency alerts. Group data comes from Yasno rather than the DTEK website and can differ from it.
- **Current status and diagnostics:** The editor distinguishes DTEK notices, Ukrenergo status, and published plans from actual power availability. Diagnostics show the saved region (Kyiv city and Kyiv oblast are distinct), districts, addresses, groups, check timestamps, today's/tomorrow's schedule availability, and automatic publishing settings. Stale or unavailable data is labelled explicitly. A delivery record's update timestamp is state history, not a Telegram message verification or an error log.
- **On-demand address checks:** In the private-chat editor, select “Перевірити зараз” under “Адреса і черга”. Kyiv addresses can be selected from a directory; elsewhere, enter the settlement, street, and building exactly as on DTEK. Playwright refreshes directories cached by the API, allowing searches even when direct server requests to DTEK are blocked. When needed, GitHub Actions checks the address and the bot reports scheduled or unscheduled outages alongside regional status. Addresses are retained only while subscribed.
- **Automatic checks after “Світло зникло”:** If a power-monitoring app posts a message such as “🔴 14:24 Світло зникло…” to a channel, add this bot as an administrator, configure the address in the Mini App, and enable “Реагувати на «Світло зникло»”. The bot receives new channel posts, not historical messages. Assessment proceeds from cheapest to most expensive: the group's schedule first (a current planned stabilization outage is reported **without an address request to DTEK**, with a warning if a regional emergency may invalidate the plan), then the regional emergency/accident notice, and finally an address check.
- **Optional schedule notifications per channel:** Select an address in Kyiv or choose a group manually for Kyiv, Dnipropetrovsk, or Odesa oblast, then enable “Сповіщення за графіком”. A detected group can be overridden manually while retaining the address for DTEK checks. Choose a two-day table image, a compact image, or no image. Schedule posts and outage-event messages have independent delivery modes: **new message**, **delete the old message and publish a replacement**, or **edit the same message**. Replacement requires permission to delete messages; otherwise the old message remains.
  - **Schedule posts:** A separate post shows the group's schedule, usually with an image: “❗️ Оновлений графік відключень на сьогодні / 📅 Четвер 15.10.2026 / 🔴 14:00 - 18:00 (~4 год)”. The first daily post starts with “Графік…”; changes use “Оновлений графік…”. “Надіслати графік у канал зараз” publishes immediately, even when automatic publishing is disabled.
  - **Advance warnings:** A separate message 5, 10, 15, 30, or 60 minutes before a planned group outage. Customize it in “Повідомлення”.
  - **Planned power-off / power-on events:** Examples: “🔴 14:24 Світло вимкнено за графіком / 🕓 Воно було 17год 23хв / 🗓 Очікуємо за графіком о 18:00” and “🟢 18:07 Світло з'явилося за графіком / 🕓 Його не було 3год 42хв”. A return-of-power message is sent only for an outage previously reported by the bot; these are schedule-based events, not voltage measurements.

  Schedules and ready-made images come from the public MIT-licensed
  [outage-data-ua](https://github.com/Baskerville42/outage-data-ua) repository:
  schedule JSON is under `data/` and images under `images/<region>/`, for example
  [images/kyiv-region](https://github.com/Baskerville42/outage-data-ua/tree/main/images/kyiv-region).
  The backend shares a 10-minute source cache across all chats instead of
  generating images or requesting schedules directly from DTEK.
- **Per-channel settings:** All alerts or emergencies/accidents only, clearance notifications, edit-versus-new delivery, and pause.
- **Quiet hours:** Configure a daily Kyiv-time interval per chat or channel under “Повідомлення”. Automatic alerts and schedule messages arrive immediately but silently during that interval. Disabled by default; manual actions and test messages are not silenced.
- **Clearance delivery:** Under “Повідомлення”, the “Зняття запису” template supports replying to the original alert (default), sending a new message, or editing the original. If the original has been deleted or cannot be edited, the bot falls back to a new reply message.
- **Custom templates:** Each message type — emergency, clearance, regional accident, local outage, schedule, power-off, and power-on — supports placeholders, a Telegram-style preview, and “Надіслати тест у канал” (tests are marked 🧪).
- **Authorization:** Only channel administrators can change channel settings.

**Two emergency-outage sources**

The bot can alert from whichever source first reports an emergency:

1. **DTEK website:** Popup observations, including district names when provided, received through `POST /ingest` or direct backend collection.
2. **Ukrenergo:** Regional status from the public [feed](https://github.com/lasercat12/ua-power-status), `data/regions.json`. The backend polls it directly; GitHub Actions can also call `POST /ingest/grid/tick`. The `Grid` section of `dtek-settings.json` configures the feed URL and freshness limit (`MaxAgeMinutes`, 45 minutes).

Rules:

- `emergency_outages` means an emergency; `grid_damage` means an accident. `normal`, `shortage`, and `outage_schedules` (stabilization schedules) are not emergency alerts. A feed older than 45 minutes is ignored: unknown status neither opens nor closes an alert, and fetch failures do not change state.
- If Ukrenergo reports first, the alert is **region-wide**, without district filtering, so all eligible regional subscribers receive it, including those with district selections. The text ends with “(за даними Укренерго)”.
- When DTEK later provides details or districts, the existing message is **edited**, or replaced by a new message in new-message mode. A chat outside DTEK's named districts does not receive a false clearance while Ukrenergo still reports a regional emergency; its thread closes silently.
- Clearance is sent only when **both** sources are no longer in an emergency state. If DTEK clears while Ukrenergo remains active, the message switches to Ukrenergo text; if Ukrenergo clears while DTEK remains active, DTEK text remains.
- Regional and district eligibility still applies. An address assessment treats an active Ukrenergo emergency as an explanation, just like a general DTEK notice, without an additional DTEK address request.

**Message templates** use Telegram HTML (`<b>`, `<i>`, `<u>`, `<s>`, `<code>`, `<pre>`, `<a href="https://…">`, `<tg-spoiler>`, `<blockquote>`) and placeholders:

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

The default emergency template is `{значок} {текст}` followed by `Деталі: {сайт}` on a new line. Use `{{` and `}}` for literal braces.

A **conditional block**, `{#назва}…{/назва}`, is shown only when its placeholder is nonempty, for example `{#світло_було}🕓 Воно було {світло_було}{/світло_було}`. Blocks cannot be nested.

Each message type has its own placeholders, shown in the editor. Additional placeholders include:

| Message                | Additional placeholders                                                                                                    |
| ---------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Local outage           | `{черга}`, `{адреса}` (saved address)                                                                                       |
| Schedule               | `{шапка}`, `{день_тижня}`, `{дата}`, `{відключення}`, `{разом}`, `{завтра}`, `{оновлено}`, `{регіон}`, `{черга}`            |
| Power off              | `{час}`, `{світло_було}`, `{очікуємо_до}`, `{тривалість}`, `{регіон}`, `{черга}`                                            |
| Power on               | `{час}`, `{світла_не_було}`, `{наступне}`, `{регіон}`, `{черга}`                                                            |

The **Mini App** opens from the bot's private-chat menu at `/lite/`. It uses plain HTML and JavaScript without a build step and is lightweight for phones. To connect a **channel**, add the bot as an administrator with posting permission; the channel appears in the editor when the webhook receives `my_chat_member`. For public channels, entering the channel's `@username` in the editor is an alternative. Select a region and address, optionally override the group, and enable schedule images if desired. “Видалити зі списку” removes a channel from your list; if you are its last manager, alerts are disabled and saved address data is removed. The bot stays in the Telegram channel until manually removed. An alternative Blazor WebAssembly editor exists in `bot/src/DtekBot.WebApp` but is not connected to the bot.

**Server configuration** (`appsettings.Production.json` on the host, never committed):

```json
{
  "ConnectionStrings": { "Default": "…" },
  "Database": { "AutoMigrate": false },
  "Bot": { "Token": "…", "WebhookSecret": "…" },
  "Ingest": { "Secret": "…" },
  "App": { "PublicUrl": "https://your-host.example" },
  "GitHub": { "Repository": "owner/dtek-monitor", "DispatchToken": "…" }
}
```

Use `AutoMigrate: true` only for initial setup and updates requiring database migrations. **The `ResetApplicationData` migration deletes all application data**: connected chats and administrators, addresses, schedules, Svitlobot keys, and check state. Tables and migration history remain. This is irreversible; back up the database before applying it. Hosting configuration secrets are outside the database and are not deleted. `App:PublicUrl` is the site's HTTPS URL, used for the “Редактор” menu button and the button under the greeting.

**District recognition in DTEK notices.** Popup wording changes, so the bot first matches dictionary entries from `dtek-settings.json`. Word stems handle Ukrainian inflections: “бучанськ” matches “Бучанському районі”. If the repository has `LLM_API_KEY`, the workflow asks a language model about **unfamiliar notices only**: an unrecognized message kind, or district-related wording without a recognized district. Understood notices bypass the model. The result identifies all districts, specific IDs such as `obukhiv`, `fastiv`, and `bucha`, or unknown scope. The model receives only the public notice and allowed geography; unknown IDs and ungrounded district selections are rejected. Non-correlated browser runs persist the popup cache on `bot-artifacts`; correlated fallback runs skip branch checkout/commit. Model failures or invalid results fall back to deterministic classification; without district restrictions, eligible subscribers throughout the region receive the alert.

The default provider is Google Gemini Flash-Lite. Obtain a key from [Google AI Studio](https://aistudio.google.com/apikey) and store it as `LLM_API_KEY`. Check current provider pricing and data-use terms; the free tier may use requests for product improvement, so only public notice text is sent. Other OpenAI-compatible providers use `LLM_BASE_URL` and `LLM_MODEL`; an OpenAI example is `https://api.openai.com/v1` with `gpt-5-nano`. API billing is separate from a ChatGPT subscription. Verify model availability in your provider account; the configured default is `gemini-3.1-flash-lite`.

This step runs in **GitHub Actions**, not the web application. Set `LLM_API_KEY` and, if needed, `LLM_BASE_URL` / `LLM_MODEL` under the workflow repository's **Settings → Secrets and variables → Actions**. The API does not use an `LLM_API_KEY` value from `appsettings.Production.json`.

`GitHub:DispatchToken` is a [fine-grained token](https://github.com/settings/personal-access-tokens/new) restricted to this repository with **Contents: Read and write**, as in the external-trigger setup. It dispatches `check-addresses` to `shared-bot.yml`. Without it, queued requests depend on a later backup run; the hourly cron is not a guaranteed deadline. The same token dispatches `check-emergency` to the standalone `monitor.yml` every `Dtek:Timers:MonitorWorkflowIntervalMinutes` (default 15, `0` disables it). Keep the token only in private hosting configuration, such as `appsettings.Production.json`; never log it.

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

**Svitlobot (optional).** Each connected Telegram channel using [svitlobot.in.ua](https://svitlobot.in.ua) can have its own `channel_key` in the Mini App. One user can manage several channels; keys, groups, and automatic sync belong to the selected channel only. A button uploads its typical weekly outage plan from the source data. Possible outages count as outages by default; `includeMaybe: false` includes only definite ones. A matching group preset is selected when available and can be changed. This updates Svitlobot schedule data, not an immediate Telegram message; Svitlobot controls its own posting and pinning. To post immediately through this bot, use “Надіслати графік у канал зараз”. The delete action removes the Svitlobot schedule and its channel post. Because a key authorizes schedule changes, it is stored only encrypted with AES-GCM, never returned or logged. Requests to Svitlobot are limited to one per minute per chat, following Svitlobot's integration rules; channel reads are cached for 5 minutes. All actions require management access. “Забути ключ” removes the key from this bot only, leaving the Svitlobot schedule intact. The key is also erased when the last manager removes the chat.

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

See [Releases](https://github.com/mr-devboy/dtek-monitor/releases) for upstream version changes and upgrade instructions.

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
heading. The default heading includes a short change summary; existing custom
headings are unchanged until you add `{зміни}`. Comparisons use planned
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
Accident templates also support `{дата}`, `{день_тижня}`, and `{частина_доби}`,
all based on Kyiv time.

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
The transport rejects requests targeting another chat and does not change the
bot's webhook or menu. The Mini App API test signs local test authentication
data and checks the test bot's real admin membership; it is not a real Telegram
Mini App login. Schedule/grid source and photo delivery checks use public live
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
against real Telegram operations. The current local group-status source is
configured for Kyiv only; unsupported regions are checked for explicit rejection,
not counted as successful local-alert delivery. Controlled changed snapshots are delivered
through `/ingest/schedules/tick` in Edit, New, and ReplaceDelete modes, with
repeat ticks verified as no-ops. The source providers and database are isolated;
this does not inject simulated events into the hosted production application.
`DeliveryFanoutTests` checks the same ingest/cron recipient fanout offline with
multiple eligible subscribers and inactive, paused, and unsubscribed controls.

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

For the district ingest-to-Telegram matrix, first populate the ignored
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
directly; production bypasses the model for recognized deterministic notices.
The offline city/oblast routing matrices additionally cover matching,
nonmatching, region-wide subscriptions and both source arrival/clearance orders.

### Multiple addresses and groups

The lite editor stores up to **three addresses in one region**. Each address can
have several groups and a label such as “Під’їзд 1”. “Знайти адресу” opens
settlement, street, and building lookup (street and building only for Kyiv city).
Changing region requires confirmation before removing the old addresses and groups.
Reloading restores the last channel and tab; a single chat opens without a
selection step. Older clients can save general settings without deleting the
new address list. Replace the full list through
`PUT /api/chats/{chatId}/addresses` (`SaveAddressesRequest`); the DTO's `addresses`
field is optional. Blazor remains compatible for general settings, while
address-list management is available in the lite Mini App.

All groups are published in **one text post** with sections and labels.
A group shared by several addresses appears once. Edit, new-message, and
replace-with-deletion modes are preserved. Long posts are split to fit Telegram
limits. A single address with one group retains its existing format and image.
There is no building-wide ANY/ALL aggregate: each group is shown separately.
An advance warning for a shared outage start is sent once for all matching
groups. Quiet hours mean silent delivery, not delayed delivery.

Svitlobot keeps **one weekly timetable per channel**, so it follows **one main
group**. In the Mini App's Svitlobot card, a channel with several groups shows
a «Зробити основною» button for each group. Until one is chosen, or if the
chosen group is removed, the first group of the first address is used. Changing
the main group writes that group's week on the next sync; manual uploads respect
`includeMaybe`. If the main group's weekly data is unavailable, synchronization
waits. Monday refreshes are preserved. Svitlobot channel posts
provide **only the trigger text**, “Світло зникло” or a custom phrase;
their estimated restoration times, durations, and other content are not a
schedule source.

The trigger assesses every address and replies briefly for each. Unresolved
cases are checked at DTEK as one batch within a single chat budget; completed
batch results arrive in one message. The DTEK collector passes the full
`sub_type_reason` array, filling detected groups for addresses without a group
selection. Manually selected groups are not overwritten. Regional alerts remain
shared, while group accidents have a separate stream naming matching addresses.

Migration `20261020213320_MultiAddressGroups` adds `ChatAddresses` and
`ChatAddressGroups`, migrates the legacy address/group without clearing data,
keeps legacy columns readable, and preserves message IDs. The migration is
included in the repository; development work does not apply it to production.
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
