# Stage S13 — (Optional) Avalonia 12 Migration

## Goal
If Stages S05–S12 were built on the `11.3.12` baseline from `development-plugin-for-avalonia`, migrate `DotNodeViewer.App`, `DotNodeViewer.Desktop`, `DotNodeViewer.Browser`, and `DotNodeViewer.App.Tests` to **Avalonia 12.x** using the skill's dedicated migration guide.

## Prerequisites
- Stage **S12** complete.
- Read [`AGENTS.md`](../../../AGENTS.md) and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/development-plugin-for-avalonia/SKILL.md` → route to:
  - `skills/avalonia-12-migration/SKILL.md`
  - `references/68-avalonia-12-migration-guide.md`
- **`.agents/skills/computer-use/SKILL.md`** (mandatory live desktop verification & screenshots after migration)

## Scope & Deliverables
1. Update `Avalonia*` package versions in `Directory.Packages.props` to the latest stable `12.x` release.
2. Apply any API, styling, rendering, or headless testing updates documented in `references/68-avalonia-12-migration-guide.md`.
3. Update `docs/adr/0005-avalonia-version.md`.
4. Run all headless UI tests and perform a live `computer-use` screenshot pass (`docs/screenshots/S13/`) to confirm visual and interactive parity.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish src/DotNodeViewer.Desktop -c Release -p:PublishAot=true
dotnet build src/DotNodeViewer.Browser -c Release
```

## Hand-off & Git Requirements
- Work on branch `stage/S13-avalonia-12-migration` (`git checkout -b stage/S13-avalonia-12-migration`).
- Commit your work incrementally in logical sub-commits prefixed with `S13:` and update `docs/roadmap/PROGRESS.md` marking **S13** complete.
