# Engineering and skill audit — 2026-10-22

## Scope

Reviewed the repository map, manifests, operational workflows, selected collector,
classification, ingest, authentication, monitoring, and Mini App paths, and their
nearby tests. The purpose was to identify engineering risks and select scoped
agent skills. This is a targeted source audit, not an exhaustive security review
or a verification of the deployed service, database, or hosting configuration.

## Findings

### Medium: HTML normalization differs across the JavaScript and C# classifiers

Evidence: [`normalize`](../src/popup-keywords.js) replaces only `&nbsp;` and
`&amp;`, while [`PopupClassifier.Normalize`](../bot/src/DtekBot.Api/Parsing/PopupClassifier.cs)
uses `WebUtility.HtmlDecode` before folding apostrophes.

A local Node reproduction with the actual Kyiv settings returned:

| Notice | JavaScript keyword gate |
| --- | --- |
| `Екстрені відключення у Солом’янському районі` | Understood |
| `Екстрені відключення у Солом&#8217;янському районі` | Not understood |

These application-data strings differ only in the encoding of the apostrophe.
The C# source decodes the numeric entity; that result was inferred from source,
not executed in this environment. The JS path can unnecessarily fall through to
optional LLM extraction. Reachability depends on whether a collector supplies
encoded or already-decoded text; this example does not establish a production
misdelivery or an all-clear failure.

Recommendation: agree on the normalization contract and add paired fixtures for
numeric/named entities, apostrophes, negation, and geographic scope. Preserve the
intentional difference between the JS gate and C# final classification. The new
shared `hybrid-contract-review` skill supports this work; application behavior was
not changed during this audit.

### Medium: no repository workflow validates pull requests with the test suites

Evidence: [monitor.yml](../.github/workflows/monitor.yml),
[schedule-refresh.yml](../.github/workflows/schedule-refresh.yml), and
[release-please.yml](../.github/workflows/release-please.yml) perform operational
collection, schedule refresh, and releases. None builds/tests JS and .NET changes
on pull requests. External branch protection and organization CI were not inspected.

Recommendation: add a separate credential-free pull-request workflow using the
commands in root `AGENTS.md`, pinned Node/.NET versions, Chromium on a supported
runner, and the non-live .NET filter. Keep operational workflows separate. This
audit did not create or run a workflow.

### Low: large UI and API files increase the cost of shared-contract changes

Evidence: [`app.js`](../bot/src/DtekBot.Api/wwwroot/lite/app.js) has 1,787 lines and
[`MiniAppApi.cs`](../bot/src/DtekBot.Api/MiniApp/MiniAppApi.cs) has 1,520 lines at
audit time. The lite editor, Blazor alternative, and shared DTOs must evolve
together. File size is a maintenance signal, not a demonstrated runtime defect.

Recommendation: when changing these areas, extract cohesive rendering/state or
endpoint/validation units where that reduces coupling; preserve the no-build
frontend and existing contracts. Avoid a standalone framework migration.

## Existing controls worth preserving

- [`MonitoringCycle`](../bot/src/DtekBot.Api/Bot/MonitoringCycle.cs) waits for
  collection before applying state and publishing. [`TickGate`](../bot/src/DtekBot.Api/Bot/TickGate.cs)
  provides process-local serialization; it does not by itself establish a
  distributed lock for multiple API replicas.
- [`RegionObservationStore`](../bot/src/DtekBot.Api/Parsing/RegionObservationStore.cs)
  rejects older observations, and the ingest path carries `collectionId` for
  browser-fallback correlation.
- [`Security.SecretMatches`](../bot/src/DtekBot.Api/Security.cs) uses constant-time
  comparison of hashed secrets. [`TelegramInitData`](../bot/src/DtekBot.Api/MiniApp/TelegramInitData.cs)
  verifies HMAC and age; related tests cover tampering and expiration.
- The Svitlobot HTTP client disables request logging in
  [`Program.cs`](../bot/src/DtekBot.Api/Program.cs). Keep credential-bearing URLs
  out of logs, traces, and browser artifacts.
- [`ResetApplicationData`](../bot/src/DtekBot.Api/Data/Migrations/20261018153536_ResetApplicationData.cs)
  is destructive, and the README already warns about it. `Database:AutoMigrate`
  defaults to false in code. Treat pending migration inspection and a backup as
  deployment prerequisites; no database was contacted in this audit.

## Validation and limitations

| Check | Result |
| --- | --- |
| `npm ci --ignore-scripts` | Passed; installed the committed dependencies. npm reported zero vulnerabilities in its package audit, not a whole-project security guarantee. |
| `node --test src/popup-keywords.test.js src/popup-llm.test.js src/ingest-regions.test.js` | 16 tests passed outside sandbox process restrictions. The initial sandbox attempt failed the child-process collector fixture; the same command passed when those restrictions were removed. |
| `npx playwright install chromium` | Failed: the locked Playwright version did not support this environment's `ubuntu26.04-x64`. |
| `node --test src/lite-editor.test.js` | Both tests failed before executing UI assertions because the Chromium executable was unavailable. |
| .NET restore/build/tests | Not run: `dotnet` was not found on PATH. |
| Node runtime | Available Node was 22.23.2; `.nvmrc` pins 22.15.1, so the passing results are not validation on the exact pinned release. |
| Installed skills | All six passed skill-creator `quick_validate.py`; installed-file hashes and relative Markdown links were verified. |
| Documentation and wrapper | `git diff --check` passed for tracked changes; authored and entrypoint Markdown had no trailing whitespace. `bash -n src/.agents/skills/playwright/scripts/playwright_cli.sh` passed; the browser CLI itself was not executed. |

No live Telegram/LLM tests, operational collectors, API hosting, production
migrations, or message sends were used. Browser and .NET validation remain
outstanding on a supported development or CI environment.

## Skills delivered

See [agent-skills.md](agent-skills.md) for the six installed skills, their scopes,
source selection, adaptations, and update procedure. Shared skills are rooted at
`.agents/skills/`, JS skills at `src/.agents/skills/`, and .NET skills at
`bot/.agents/skills/`. The added instructions route Mini App tasks to the JS
browser skill despite the UI's location inside the API project.
