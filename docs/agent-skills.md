# Project agent skills

Skills are versioned with the code and scoped to the hybrid project's workspaces.
The inventory was reviewed on 2026-10-22. It supports the existing stack:
JavaScript ES modules, Node's built-in test runner, Playwright, .NET 10, ASP.NET
Core, EF Core SQL Server, xUnit, and the alternative Blazor client.

## Layout and selection

| Scope | Skill | Purpose |
| --- | --- | --- |
| Shared | [hybrid-contract-review](../.agents/skills/hybrid-contract-review/SKILL.md) | Trace JS/.NET producers and consumers, geographic classification, ingest correlation, and client compatibility. Authored for this repository. |
| Shared | [security-threat-model](../.agents/skills/security-threat-model/SKILL.md) | Explicit threat-modeling requests covering collectors, webhook/ingest endpoints, Telegram authorization, credentials, and persistence. |
| JavaScript | [node](../src/.agents/skills/node/SKILL.md) | Async handling, cleanup, modules, and `node:test`; adapted to this project's JavaScript implementation. |
| JavaScript | [playwright](../src/.agents/skills/playwright/SKILL.md) | Browser inspection and collector/Mini App diagnosis; complements the existing regression tests. |
| .NET | [aspnet-core](../bot/.agents/skills/aspnet-core/SKILL.md) | Minimal APIs, middleware, DI, authentication, background work, integration tests, and Blazor compatibility. |
| .NET | [optimizing-ef-core-queries](../bot/.agents/skills/optimizing-ef-core-queries/SKILL.md) | Query correctness, tracking, database round trips, indexes, and context concurrency. |

The shared skills are in `.agents/skills/`, JavaScript skills in
`src/.agents/skills/`, and .NET skills in `bot/.agents/skills/`. The active Mini App
is physically inside the .NET project, but its browser guidance comes from the
JavaScript workspace. No skill files are placed in the public `wwwroot` directory.

## Discovery and use

[Codex's official documentation](https://learn.chatgpt.com/docs/build-skills)
describes discovery from the current working directory upward to the repository
root. Starting in `src/` discovers the JavaScript and shared skills; starting in
`bot/` discovers the .NET and shared skills. Starting in the root does not imply
recursive discovery of child workspaces. The root and nested `AGENTS.md` files
therefore route agents to the appropriate skill explicitly.

Examples:

- From the root: “Use `src/.agents/skills/node/SKILL.md` to review collector cleanup.”
- From `bot/`: “Use `$aspnet-core` to review the API middleware.”
- Across workspaces: “Use `$hybrid-contract-review` to review the ingest DTO change.”
- For Mini App work: “Use `src/.agents/skills/playwright/SKILL.md` to inspect the local editor with mocked requests.”

Installed skills are available on the next turn in the appropriate workspace.
If discovery does not refresh, restart the session in that workspace or reference
the `SKILL.md` path directly. A shell `cd` alone does not change the session's
initial skill catalog. Run npm commands at the root and .NET commands in `bot/`.

## Provenance and review

[agent-skills.lock.json](agent-skills.lock.json) records exact upstream commits,
paths, local adaptations, and SHA-256 hashes of the installed files. This is a
review inventory, not a package-manager lockfile or an automatic updater.
Upstream license and notice files are retained.

| Source | Observed repository stars | Selection evidence |
| --- | ---: | --- |
| [openai/skills](https://github.com/openai/skills) | 28,245 | Official curated ASP.NET Core, Playwright, and threat-modeling skills; Playwright had about 8.4K installs in the skills registry. |
| [dotnet/skills](https://github.com/dotnet/skills) | 5,699 | Official .NET repository; EF Core skill had about 5.4K installs. |
| [mcollina/skills](https://github.com/mcollina/skills) | 1,956 | Node-focused source with explicit `node:test`, async, and cleanup references; `node` had about 5.8K installs. |

Counts are discovery-time observations, not quality guarantees. Selection also
included reading manifests, relevant references, executable helpers, and licenses.
The search checked the [skills.sh directory](https://skills.sh/) and used
`npx --yes skills find 'dotnet'`, `npx --yes skills find 'nodejs testing'`, and
`npx --yes skills find 'playwright'`, plus the official curated list.

Local adaptations:

- `node`: focus discovery on this project's JavaScript collectors; preserve
  ES modules and `node:test`; make upstream streaming/caching advice conditional
  on the workload. TypeScript reference examples remain available.
- `playwright`: resolve its wrapper from the repository instead of a global skill
  installation; store captures under ignored `artifacts/playwright/`; explain the
  local Mini App harness and credential-sensitive browser artifacts.
- `hybrid-contract-review`: local instructions with no downloaded implementation.

No upstream skill authorizes sending Telegram messages, applying production
migrations, or running live collectors. Follow the repository's existing rules.
The threat-model skill is installed for future explicit AppSec requests; it was
not invoked to produce the engineering audit.

## Deliberately omitted

- React, Next.js, Jest, Vitest, and Express scaffolding: these are not the current
  application or test framework.
- Generic .NET backend packs: overlap with the official ASP.NET Core and focused
  EF Core skills. `github/awesome-copilot` and `wshobson/agents` were considered
  during discovery, but are not installed.
- Deployment/provider packs: this audit does not establish a need to change the
  hosting platform or authorize deployment.
- Broad security-best-practices packs that omit C#: avoid implying complete
  coverage of the .NET security boundary.
- Automatic CI-repair packs: the current workflows are operational/release jobs;
  a build/test workflow needs to exist before repair automation is useful.

## Updating

Fetch a candidate at an explicit commit into a temporary directory first, using
the skill-installer's `install-skill-from-github.py --repo OWNER/REPO --ref SHA
--path SOURCE_PATH --dest TEMP_DIRECTORY`. Review instructions, executable code,
licenses, and referenced files. Preserve local adaptations and install only the
selected skills into their existing scopes. Do not use a global installation or
blindly replace the root `AGENTS.md`.

Validate each skill with the skill-creator's `quick_validate.py`, check reference
paths and local wrapper syntax, and update the review inventory and hashes.
Skill installation does not require running collectors or starting the API.
