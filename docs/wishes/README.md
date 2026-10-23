# Wishes — Post-Roadmap Feature Briefs

This folder holds **feature briefs for work we want to do after the planned roadmap (`S00`–`S13`) is finished**. Each brief uses the same structure as a stage brief in [`docs/roadmap/stages/`](../roadmap/stages/), so an agent can pick one up and run it like a stage once it is promoted.

## Conventions

- **File naming:** `Wxx-<short-slug>.md` (for example, `W01-ethos-u-command-stream-unwrapping.md`). `W` numbers only identify a wish. They do **not** set the order of work.
- **Status:** each brief starts with a `Status` line: `Proposed` → `Accepted` → `Promoted (Sxx)` → `Done`.
- **Promotion:** when a wish is scheduled, copy it into `docs/roadmap/stages/` as the next free `Sxx-*.md` brief, set its status here to `Promoted (Sxx)`, and follow the normal Stage-Agent Protocol in [`AGENTS.md`](../../AGENTS.md) (dedicated branch, incremental commits, Definition of Done, `PROGRESS.md` hand-off).
- **Frozen contracts:** a wish that changes the public Unified Graph IR or parser abstractions must list the ADR it needs under **Open Design Questions**. That ADR must be accepted before the code is written.
- **Out-of-scope discoveries** made while working on a stage still belong in the **Deferred** section of [`docs/roadmap/PROGRESS.md`](../roadmap/PROGRESS.md). Promote one to a wish here only when it is big enough to need its own brief.

## Index

| ID | Title | Depends on | Status |
|---|---|---|---|
| [W01](W01-ethos-u-command-stream-unwrapping.md) | Ethos-U Command Stream Unwrapping (TFLite & ExecuTorch) | S03, S07, S11 | Proposed |

