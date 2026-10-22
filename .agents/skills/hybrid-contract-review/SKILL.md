---
name: hybrid-contract-review
description: Review changes that cross the Node collectors, ASP.NET Core API, shared DTOs, or Telegram editors in this repository. Use for ingest payloads, popup classification, geographic routing, or client compatibility; skip changes confined to one implementation with no shared contract impact.
---

# Hybrid Contract Review

Resolve paths from the Git repository root. Read its `AGENTS.md` for project
invariants, commands, and external-effect boundaries. This skill adds a workflow
for finding the other side of a change; it does not authorize live sends.

## Trace the contract

Select the relevant producer/consumer pair and read nearby tests:

| Contract | Producer or first implementation | Consumer or second implementation |
| --- | --- | --- |
| Popup normalization, kind, and geography | `src/popup-keywords.js`, `src/popup-llm.js` | `bot/src/DtekBot.Api/Parsing/PopupClassifier.cs`, `bot/src/DtekBot.Api/dtek-settings.json` |
| Regional observation and collection correlation | `src/ingest-regions.js` | `bot/src/DtekBot.Api/Program.cs`, `Parsing/DtekPopupCollector.cs`, `Parsing/RegionObservationStore.cs` under the API |
| Address directory and queued results | `src/ingest-regions.js`, `src/check-addresses.js` | `Groups/AddressDirectoryApi.cs`, `Checks/CheckApi.cs` under the API |
| Editor settings, slots, and templates | `bot/src/DtekBot.Shared/` | API `MiniApp/`, `wwwroot/lite/app.js`, and `bot/src/DtekBot.WebApp/` |

For each changed field or rule, record the serialized name, optional/default
behavior, accepted IDs, timestamp meaning, and what an older client will send.
Distinguish the JavaScript keyword gate's “understood” decision from the C#
classifier's final kind and expanded district set; their output shapes differ.

## Challenge the boundary

Choose cases that exercise the changed behavior, rather than duplicating the
implementation. For classifiers, compare the same notice through both sides:
literal versus HTML-encoded apostrophes, negation, known and unknown districts,
Kyiv city versus oblast, and district groups with children. Account for the
upstream collector's text extraction when judging whether an encoding mismatch
is reachable. Keep Ukrainian input strings exact.

For ingest changes, trace a failed source, an older observation, and a late
browser result from another `collectionId`. Follow each through persistence and
notification eligibility. For editor changes, compare omitted fields and legacy
aliases across both clients, and verify that viewing or previewing does not send.

## Verify and report

Run the relevant fixture-only Node tests and affected .NET test classes using the
root instructions. Include the lite editor suite when UI contracts change. Never
substitute a live collector or production API for missing test prerequisites.

Report the producer/consumer paths, concrete mismatch or compatible behavior,
regression cases, and checks actually executed. Separate an observed runtime
result from source inspection, and name unavailable SDK/browser prerequisites.
