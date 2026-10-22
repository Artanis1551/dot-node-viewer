# Development Plugin for Avalonia

Codex plugin for building, reviewing, designing, porting, and migrating Avalonia applications with focused skills instead of one oversized catch-all skill.

## License

This repository is licensed under the MIT License. See `LICENSE` for the full terms.

## Plugin Identity

- Plugin name: `development-plugin-for-avalonia`
- Plugin manifest: [`.codex-plugin/plugin.json`](.codex-plugin/plugin.json)
- Canonical umbrella workflow: [`SKILL.md`](SKILL.md)
- Repo-local umbrella skill: [`.agents/skills/development-plugin-for-avalonia/SKILL.md`](.agents/skills/development-plugin-for-avalonia/SKILL.md)
- Specialist skills: [`skills/`](skills)
- Shared reference index: [`references/compendium.md`](references/compendium.md)
- Avalonia upstream repository: [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia)

## Discovery Model

This repo now supports both Codex discovery paths at the same time:

- Repo-local skill discovery uses [`.agents/skills/development-plugin-for-avalonia/SKILL.md`](.agents/skills/development-plugin-for-avalonia/SKILL.md) as the broad entrypoint when Codex is working inside this checkout.
- Plugin discovery uses [`.codex-plugin/plugin.json`](.codex-plugin/plugin.json), which exposes the focused skills under [`skills/`](skills).
- Repo marketplace discovery can use [`.agents/plugins/marketplace.json`](.agents/plugins/marketplace.json), which points back to this repo root as the plugin directory.

The repo-local skill is intentionally thin. It forwards broad requests into the focused plugin skills and shared references instead of duplicating the full guidance.
The root [`SKILL.md`](SKILL.md) is the canonical umbrella workflow source, not the repo-local discovery entrypoint.

## Local Install

This repository now includes a repo marketplace file at [`.agents/plugins/marketplace.json`](.agents/plugins/marketplace.json).

That makes local testing easier:

- from this checkout, Codex can read the repo marketplace and find the plugin without creating a separate personal marketplace just for local testing
- for personal install, you can still copy the plugin to `~/.codex/plugins/` and add an entry to `~/.agents/plugins/marketplace.json`

This repo intentionally uses the repository root as the plugin root, so the repo marketplace keeps `source.path` at `./` instead of copying the plugin under `./plugins/`.

The repo marketplace keeps a distinct internal `name` for catalog and cache identity while preserving the visible `displayName` used by the plugin brand.

The plugin manifest now uses plugin-specific PNG assets for `composerIcon` and `logo` so the plugin directory can render branded artwork from common image formats.

## Avalonia Version Coverage

This plugin is pinned to Avalonia **11.3.12** for default implementation guidance.

- API references and guidance are aligned to `11.3.12`.
- Generated stable API indexing is expected to use `--git-ref 11.3.12`.
- Guidance should avoid relying on `master`-only APIs unless a document explicitly states that exception.

This repository also carries a dedicated Avalonia 12 migration lane:

- curated migration chapter: [`references/68-avalonia-12-migration-guide.md`](references/68-avalonia-12-migration-guide.md)
- generated break/new API catalog: [`references/69-avalonia-12-breaking-changes-and-new-api-catalog.md`](references/69-avalonia-12-breaking-changes-and-new-api-catalog.md)
- generated Avalonia 12 API index: [`references/api-index-12.0.0-rc1-generated.md`](references/api-index-12.0.0-rc1-generated.md)

## Skill Catalog

### Umbrella

- [`.agents/skills/development-plugin-for-avalonia/SKILL.md`](.agents/skills/development-plugin-for-avalonia/SKILL.md): broad repo-local routing skill
- [`SKILL.md`](SKILL.md): canonical umbrella workflow source shared by the repo-local wrapper, not a repo-local discovery path
- [`.agents/plugins/marketplace.json`](.agents/plugins/marketplace.json): repo marketplace entry exposing this repo itself as the plugin directory

### Core Avalonia Skills

- [`avalonia-bootstrap-and-lifetime`](skills/avalonia-bootstrap-and-lifetime/SKILL.md): `AppBuilder`, lifetimes, platform bootstrap, build setup
- [`avalonia-bindings-and-xaml`](skills/avalonia-bindings-and-xaml/SKILL.md): compiled bindings, runtime XAML, converters, markup behavior
- [`avalonia-threading-and-dispatcher`](skills/avalonia-threading-and-dispatcher/SKILL.md): dispatcher, timers, reactive UI-thread discipline
- [`avalonia-styling-and-resources`](skills/avalonia-styling-and-resources/SKILL.md): styles, resources, themes, property system, packaging
- [`avalonia-views-and-templating`](skills/avalonia-views-and-templating/SKILL.md): view location, templates, logical/visual tree patterns
- [`avalonia-input-and-commands`](skills/avalonia-input-and-commands/SKILL.md): routed input, commands, focus, drag/drop, text editing
- [`avalonia-controls-and-windowing`](skills/avalonia-controls-and-windowing/SKILL.md): templated controls, windows, menus, popups, tray, notifications
- [`avalonia-layout-and-virtualization`](skills/avalonia-layout-and-virtualization/SKILL.md): layout authoring, measure/arrange, virtualization
- [`avalonia-rendering-and-graphics`](skills/avalonia-rendering-and-graphics/SKILL.md): animation, compositor, drawing, Skia, rendering interop
- [`avalonia-platform-services`](skills/avalonia-platform-services/SKILL.md): storage provider, clipboard, launcher, screens, external integration
- [`avalonia-accessibility-and-validation`](skills/avalonia-accessibility-and-validation/SKILL.md): validation, accessibility, automation
- [`avalonia-testing-diagnostics-and-performance`](skills/avalonia-testing-diagnostics-and-performance/SKILL.md): test stack, profiling, troubleshooting, performance

### Design Skills

- [`avalonia-design-systems`](skills/avalonia-design-systems/SKILL.md): professional design tokens, layout language, dense workflow UX
- [`avalonia-fluent-design`](skills/avalonia-fluent-design/SKILL.md): FluentTheme, palette customization, Fluent shells and motion

### Migration Skills

- [`html-css-to-avalonia`](skills/html-css-to-avalonia/SKILL.md): HTML/CSS mental-model and UI pattern migration
- [`winforms-to-avalonia`](skills/winforms-to-avalonia/SKILL.md): WinForms control, layout, owner-draw, and workflow migration
- [`wpf-to-avalonia`](skills/wpf-to-avalonia/SKILL.md): WPF property, binding, layout, styling, and rendering migration
- [`winui-to-avalonia`](skills/winui-to-avalonia/SKILL.md): WinUI shell, state, composition, and platform-integration migration
- [`avalonia-12-migration`](skills/avalonia-12-migration/SKILL.md): move existing Avalonia 11 code to Avalonia 12 safely

## Shared References

The skills share one reference corpus instead of duplicating docs inside every skill:

- [`references/compendium.md`](references/compendium.md): top-level navigation
- [`references/00-api-map.md`](references/00-api-map.md): curated app-facing API map
- [`references/api-index-generated.md`](references/api-index-generated.md): broad signature lookup
- [`references/professional-design/README.md`](references/professional-design/README.md): professional design lane
- [`references/fluent-design/README.md`](references/fluent-design/README.md): Fluent design lane
- [`references/html-to-avalonia/README.md`](references/html-to-avalonia/README.md): HTML/CSS migration lane
- [`references/winforms-to-avalonia/README.md`](references/winforms-to-avalonia/README.md): WinForms migration lane
- [`references/wpf-to-avalonia/README.md`](references/wpf-to-avalonia/README.md): WPF migration lane
- [`references/winui-to-avalonia/README.md`](references/winui-to-avalonia/README.md): WinUI migration lane

## Repository Structure

- [`SKILL.md`](SKILL.md): canonical umbrella workflow source
- [`.agents/plugins/marketplace.json`](.agents/plugins/marketplace.json): repo marketplace for loading this repo as a local plugin, with the repo root acting as the plugin root
- [`.agents/skills/development-plugin-for-avalonia/SKILL.md`](.agents/skills/development-plugin-for-avalonia/SKILL.md): repo-local skill entrypoint for this checkout
- [`skills/`](skills): focused skill folders with their own `SKILL.md` and `agents/openai.yaml`
- [`references/`](references): shared reference corpus
- [`scripts/generate_api_index.py`](scripts/generate_api_index.py): stable API index generator
- [`scripts/generate_api_migration_report.py`](scripts/generate_api_migration_report.py): Avalonia 12 migration report generator
- [`assets/`](assets): shared plugin and skill imagery

## Regenerating API Index (Pinned)

```bash
python3 scripts/generate_api_index.py \
  --repo <path-to-avalonia-repo> \
  --git-ref 11.3.12 \
  --output references/api-index-generated.md
```

Avalonia 12 lane regeneration:

```bash
python3 scripts/generate_api_index.py \
  --repo <path-to-avalonia-repo> \
  --git-ref 12.0.0-rc1 \
  --output references/api-index-12.0.0-rc1-generated.md \
  --max-per-file 100000

python3 scripts/generate_api_migration_report.py \
  --repo <path-to-avalonia-repo> \
  --from-ref 11.3.12 \
  --to-ref 12.0.0-rc1 \
  --output references/69-avalonia-12-breaking-changes-and-new-api-catalog.md
```
