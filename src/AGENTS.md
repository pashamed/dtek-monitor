# JavaScript workspace

The root `AGENTS.md` applies. npm dependencies and `.nvmrc` remain at the
repository root; run installation and `node --test` commands there.

- Use `.agents/skills/node/SKILL.md` for collector runtime changes, asynchronous
  cleanup, error handling, or Node tests. Its upstream TypeScript examples do not
  change this project's JavaScript ES-module convention.
- Use `.agents/skills/playwright/SKILL.md` for browser investigation. Keep the
  existing `node:test` browser harness in `lite-editor.test.js`.
- For classification or ingest changes, also read the root
  `.agents/skills/hybrid-contract-review/SKILL.md` and the corresponding C# code.
