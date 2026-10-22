# ADR 0005: Avalonia 11.3.12 Baseline with Dedicated Stage S13 Migration Path

- **Status:** Accepted
- **Date:** 2026-10-23
- **Stage:** S00

## Context

The cross-platform UI (`DotNodeViewer.App`, `DotNodeViewer.Desktop`, `DotNodeViewer.Browser`) and headless UI tests (`DotNodeViewer.App.Tests`) are built with Avalonia UI and `CommunityToolkit.Mvvm`. The repository's Avalonia development skill (`development-plugin-for-avalonia`) provides comprehensive API maps, compiled-binding patterns, custom `GraphCanvas` rendering patterns, and headless testing guidance pinned to **Avalonia `11.3.12`**, plus an explicit migration guide (`references/68-avalonia-12-migration-guide.md`) for upgrading to **Avalonia `12.x`**.

## Decision

1. **Pin Avalonia `11.3.12` for Stages S00–S12:**
   `Directory.Packages.props` pins `Avalonia`, `Avalonia.Desktop`, `Avalonia.Browser`, `Avalonia.Themes.Fluent`, `Avalonia.Headless`, and `Avalonia.Diagnostics` to `11.3.12`.
2. **Enforce Forward-Compatible Patterns:**
   All XAML and viewmodels in `DotNodeViewer.App` enable `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`, use explicit `x:DataType` bindings, avoid deprecated Avalonia 11 APIs, and isolate rendering and platform service calls.
3. **Optional Stage S13 Migration:**
   Stage `S13` (`docs/roadmap/stages/S13-avalonia-12-migration.md`) is dedicated to upgrading `Directory.Packages.props` and any affected call sites to Avalonia `12.x` once all core features, WASM support, and NativeAOT verification are complete in Stage `S12`.

## Consequences

- UI development in Stages S05, S06, S11, and S12 benefits from stable, well-tested `11.3.12` packages and matching skill reference documentation.
- Upgrading to Avalonia `12.x` in Stage S13 is isolated into a single, verifiable migration stage.

