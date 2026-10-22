# .NET workspace

The root `AGENTS.md` applies. Run .NET commands from this directory so
`global.json` selects the intended SDK.

- Use `.agents/skills/aspnet-core/SKILL.md` for API, middleware, dependency
  injection, authorization, background services, and Blazor changes.
- Use `.agents/skills/optimizing-ef-core-queries/SKILL.md` for EF Core correctness
  and performance reviews. Distinguish SQLite test behavior from SQL Server
  production behavior; prepare schema migrations locally without applying them
  to production.
- The active editor in `src/DtekBot.Api/wwwroot/lite/` is plain HTML/CSS/JS.
  For browser investigation, read `../src/.agents/skills/playwright/SKILL.md`;
  run its regression tests from the repository root. Do not introduce a frontend
  build or treat this editor as Blazor.
- For shared DTOs, ingest, classification, or editor compatibility, also read
  `../.agents/skills/hybrid-contract-review/SKILL.md`.
