# Stage S12 — Browser (WASM) Head, NativeAOT Publishing, Performance & Packaging Hardening

## Goal
Complete the `DotNodeViewer.Browser` WebAssembly head, verify zero-warning NativeAOT publishing for `DotNodeViewer.Desktop` and `DotNodeViewer.Cli`, run a comprehensive `.NET` performance audit (`analyzing-dotnet-performance`), configure NuGet package metadata for `DotNodeViewer.Core` and `DotNodeViewer.Layout`, and write the comprehensive project `README.md`.

## Prerequisites
- Stage **S11** complete.
- Ensure `dotnet workload install wasm-tools` and NativeAOT prerequisites (`clang`, `zlib1g-dev`) are installed per [`PREREQUISITES.md`](../../../PREREQUISITES.md).
- Read [`AGENTS.md`](../../../AGENTS.md) and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- **`.agents/skills/analyzing-dotnet-performance/SKILL.md`** (comprehensive scan across `Core`, `Layout`, `App`)
- **`.agents/skills/computer-use/SKILL.md`** (verify published NativeAOT desktop binary)
- `.agents/skills/development-plugin-for-avalonia/SKILL.md` → route to:
  - `skills/avalonia-bootstrap-and-lifetime/SKILL.md`
  - `skills/avalonia-platform-services/SKILL.md`
  - `skills/avalonia-testing-diagnostics-and-performance/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/documentation-writer/SKILL.md`

## Scope & Deliverables
1. **WebAssembly Head (`src/DotNodeViewer.Browser/`):**
   - Configure `net10.0-browser` with `Avalonia.Browser` and `SingleViewApplicationLifetime` (sharing `MainView` / `MainWindowViewModel` with `DotNodeViewer.Desktop`).
   - Implement browser-safe seekable stream adapter for `IStorageProvider` file pickers (reading or staging into a seekable stream with a documented memory guard for browser tabs).
   - Verify `dotnet build src/DotNodeViewer.Browser -c Release` and `dotnet publish src/DotNodeViewer.Browser -c Release` succeed cleanly.
2. **NativeAOT Publishing Verification:**
   - Configure and run `dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true` and `dotnet publish src/DotNodeViewer.Desktop -c Release -p:PublishAot=true` with **zero trim or AOT warnings**.
   - Launch the compiled NativeAOT `DotNodeViewer.Desktop` binary on a fixture and capture a verification screenshot via `computer-use` (`docs/screenshots/S12/`).
3. **Comprehensive Performance Audit (`analyzing-dotnet-performance`):**
   - Scan `DotNodeViewer.Core`, `DotNodeViewer.Layout`, and `DotNodeViewer.App` for allocation, span, async, collection, and rendering anti-patterns.
   - Fix all critical and high-severity findings and write the audit summary to `docs/perf/performance-audit.md`.
4. **NuGet Packaging & Documentation Website Finalization:**
   - Configure NuGet package metadata (`PackageId`, `Description`, `Authors`, `License`, `PackageReadmeFile`, deterministic build flags) on `DotNodeViewer.Core` and `DotNodeViewer.Layout` and verify `dotnet pack -c Release`.
   - Polish root `README.md` and `docs/site/` (adding browser/WASM deployment instructions, NativeAOT publishing guide, and API reference index) and optionally add a GitHub Pages deployment job for `docs/site/_site` in `.github/workflows/ci.yml`.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
dotnet publish src/DotNodeViewer.Desktop -c Release -p:PublishAot=true
dotnet build src/DotNodeViewer.Browser -c Release
dotnet pack src/DotNodeViewer.Core -c Release
dotnet pack src/DotNodeViewer.Layout -c Release
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```

## Hand-off & Git Requirements
- Work on branch `stage/S12-wasm-aot-and-perf` (`git checkout -b stage/S12-wasm-aot-and-perf`).
- Commit your work incrementally in logical sub-commits prefixed with `S12:` (e.g., `S12: complete DotNodeViewer.Browser WASM head`, `S12: verify NativeAOT publishing and apply performance audit fixes`, `S12: configure NuGet packaging and update README.md`, `S12: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S12** complete.
