# Repository instructions

These instructions apply throughout this repository. Use a nested `AGENTS.md` only
when a directory needs additional guidance. Keep this file focused on durable,
repository-specific rules; update commands and paths when the implementation changes.

## Language

- Write all documentation in English, including README content, agent instructions,
  changelog entries, configuration explanations, and documentation added in pull requests.
- Write all code comments in English, including XML documentation, JSDoc, inline
  comments, and comments in scripts, workflows, stylesheets, and markup.
- Ukrainian product text, upstream notices, geographic names, fixtures, and template
  placeholder identifiers are application data. Preserve their spelling and behavior;
  documentation may quote exact identifiers or input values when necessary. Explain
  those literals in English. Do not translate the product as a documentation change.

## Project map

- `src/monitor.js`: standalone Node.js/Playwright monitor for one Telegram chat.
  Runtime state belongs in ignored `artifacts/`, persisted on the `artifacts` branch
  by the monitor workflow.
- `src/ingest-regions.js` and `src/check-addresses.js`: browser collectors that send
  regional observations, address directories, and queued address results to the API.
- `src/popup-keywords.js` and `src/popup-llm.js`: legacy geography-only diagnostics.
  Active shared-bot interpretation lives in API `Parsing/PopupInterpreter.cs`.
- `bot/src/DtekBot.Api/`: ASP.NET Core API, Telegram webhook, EF Core SQL Server
  persistence, monitoring cycle, notification delivery, and Svitlobot integration.
- `bot/src/DtekBot.Api/wwwroot/lite/`: active Telegram Mini App, plain HTML/CSS/JS
  served by the API; no frontend build step.
- `bot/src/DtekBot.Shared/`: shared DTOs, message templates, slots, and Kyiv time.
- `bot/src/DtekBot.WebApp/`: alternative Blazor WebAssembly editor. Preserve shared
  contract compatibility when changing the API or lite editor.
- `bot/tests/DtekBot.Tests/`: xUnit tests, in-memory SQLite API fixtures, fake
  providers and Telegram transport, plus explicitly enabled live Telegram tests.
- `bot/src/DtekBot.Api/dtek-settings.json`: region IDs, district aliases,
  classification rules, source mappings, limits, and timing defaults.
- `.github/workflows/`: operational monitoring, schedule refresh, and release-please.
  These workflows are not a build/test CI suite.

Use `README.md` for setup and operational details. Read the relevant implementation
and nearby tests before changing behavior; the code and configuration define the
current contracts.

## Repository skills

- Shared review workflows live in `.agents/skills/`: `hybrid-contract-review`
  for changes crossing JS/.NET or editor contracts, and `security-threat-model`
  for explicitly requested threat modeling.
- Node.js guidance and browser investigation live in `src/.agents/skills/`
  (`node`, `playwright`). The Playwright skill also applies to the JavaScript Mini
  App in `bot/src/DtekBot.Api/wwwroot/lite/` and `src/lite-editor.test.js`.
- .NET guidance lives in `bot/.agents/skills/` (`aspnet-core`,
  `optimizing-ef-core-queries`).
- Codex discovers skills along the working-directory-to-root path. When working
  from the repository root, open the relevant nested `SKILL.md` explicitly;
  changing a shell command's directory does not reload the session's skill list.
  Load only skills relevant to the task. Repository rules take precedence over
  upstream examples, including test commands and authorization for live effects.
- See `docs/agent-skills.md` for usage, provenance, and update instructions.

## Setup and validation

Node.js is pinned in `.nvmrc` (22.15.1); use npm and the committed lockfile.
Run Node commands from the repository root:

```sh
npm ci
npx playwright install chromium
node --test src/popup-keywords.test.js src/popup-llm.test.js src/ingest-regions.test.js
node --test src/lite-editor.test.js
```

The first test command uses fixtures/stubs and needs no live credentials. The lite
editor test needs Playwright Chromium and intercepts its application requests.
Use `node --test src/*.test.js` for the complete Node suite. There are no npm
`test`, `lint`, or `build` scripts; do not invent commands in reports.

Run .NET commands from `bot/` so `global.json` selects the .NET 10 SDK
(10.0.100 with `latestFeature` roll-forward):

```sh
cd bot
dotnet restore DtekBot.sln
dotnet build DtekBot.sln --no-restore
dotnet test tests/DtekBot.Tests/DtekBot.Tests.csproj --no-build --filter 'FullyQualifiedName!~LiveTelegramTests'
```

For focused .NET checks, add a class-name filter, for example
`--filter 'FullyQualifiedName~PopupClassifier'`. Restore the EF tool with
`dotnet tool restore --tool-manifest dotnet-tools.json` when preparing migrations.

Run checks appropriate to the change. For documentation or comment-only edits,
review language, links, command accuracy, and `git diff --check`; a full application
test run is not required. For behavior changes, add or update regression coverage
and run the affected tests. Report commands executed, failures, and unavailable
prerequisites accurately; do not describe skipped tests as passed.

## Implementation conventions

- Keep Node code as ES modules. Use `.prettierrc.json` when formatting and avoid
  unrelated formatting churn in files with existing style differences.
- Follow the existing C# nullable-reference, dependency-injection, async, and
  cancellation patterns. Use injected `TimeProvider` and shared `KyivTime` for
  schedule logic; do not substitute the host's local time or a fixed UTC offset.
- Preserve DTO compatibility, persisted template keys, Ukrainian placeholders,
  legacy English aliases, and the existing template parser. Add EF migrations for
  schema changes instead of rewriting applied migrations or resetting user data.
- Keep bot messages and Mini App explanations short and descriptive, normally one
  or two lines stating what happened and what happens next. See
  `.github/copilot-instructions.md`; schedules and explicitly requested detail may
  need more space.
- Update README instructions when public behavior, configuration, or setup changes.
  Release-please manages versions and release notes; do not bump versions as a
  side effect of unrelated work. Use English Conventional Commit titles when
  preparing commits or PRs for this release workflow.

## Behavior that must be preserved

- Keep Kyiv city and Kyiv oblast distinct. District and group routing must use
  configured IDs and aliases; changes to classification must keep the Node keyword
  gate and C# `PopupClassifier` consistent. Test negation and geographic scope.
- Failed, stale, blocked, or missing source data is unknown, not an all-clear.
  Preserve the standalone monitor's repeated clearance check, the shared bot's
  DTEK/Ukrenergo reconciliation, and protection against older observations.
- `MonitoringCycle` collects sources before updating state and publishing. Preserve
  `TickGate` serialization and browser-fallback `collectionId` correlation so
  concurrent ticks cannot publish partial or duplicate transitions.
- A published schedule or a missing outage record does not measure actual power.
  Preserve unavailable-day states, source attribution, and planned-event wording.
- Preserve delivery deduplication, pause/subscription checks, message IDs and
  edit/reply/new/replace modes. Quiet hours silence automatic messages; they do
  not postpone delivery. Keep read-only viewing and preset previews free of sends.
- Address/group updates must preserve legacy clients and manually chosen groups.
  Retain the three-address limit, separate group streams, and complete weekly
  schedule aggregation for Svitlobot.
- Shared-bot popup interpretation is LLM first in the API, with deterministic
  clause-aware fallback; the standalone monitor is unchanged. Apply district
  states to every configured region, keeping Kyiv city distinct from oblast.
  Treat scraped
  notices as untrusted data; retain allowed-ID validation, grounding, caching,
  and deterministic fallback. Send only public notice/geography data to the model.

## Credentials and external effects

- Keep `.env`, `.env.telegram-test`, `.env.llm-test`, production settings, publish
  profiles, logs, browser artifacts, and build outputs out of commits. Never log
  tokens, signed Telegram init data, connection strings, or Svitlobot channel keys.
- Preserve webhook/ingest secret checks, Telegram init-data validation, chat-admin
  authorization, and AES-GCM protection of Svitlobot keys. Svitlobot URLs contain
  credentials; keep HTTP request logging disabled for that client.
- `npm start`, `npm run watch`, collectors, API hosting, and operational workflows
  can contact services or publish messages. They are not substitutes for tests.
  Live tests/scripts require explicit authorization for their external effects;
  do not enable `DTEK_LIVE_TELEGRAM` or `DTEK_LIVE_LLM` merely to validate an edit.
  Authorized live Telegram tests must use the dedicated test bot and allowlisted
  chat described in README, retain transport restrictions, and clean up messages.
- Prepare migrations locally without applying them to production as part of a
  routine code change. `ResetApplicationData` deletes application data; inspect
  pending migrations and arrange a backup before an authorized deployment.

## Guidance references

The structure follows [OpenAI's AGENTS.md guide](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
and its advice to [keep repository guidance focused and current](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra).
Prefer concrete commands and consequential project constraints over generic
instructions or a mandatory reading list for every edit.
