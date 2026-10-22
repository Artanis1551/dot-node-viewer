# Stage S00 — Bootstrap, Governance & Solution Scaffolding

## Goal
Create a buildable, warning-free .NET 10 solution with central package management, foundational ADRs, updated `.gitignore`, and CI workflow so every subsequent stage can focus strictly on domain implementation.

## Prerequisites
- Read [`AGENTS.md`](../../../AGENTS.md), [`PREREQUISITES.md`](../../../PREREQUISITES.md), [`GLOSSARY.md`](../../../GLOSSARY.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).
- Ensure `.NET 10.0` SDK is available on `PATH` (`dotnet --list-sdks`).

## Skills to Load
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/csharp-docs/SKILL.md`

## Scope & Deliverables
1. **`global.json`:** Pin the .NET 10 SDK (`10.0.100` with `rollForward: latestFeature`) and configure the `Microsoft.Testing.Platform` test runner.
2. **`Directory.Build.props`:**
   - `TargetFramework`: `net10.0` (unless overridden by `DotNodeViewer.Browser`)
   - `LangVersion`: `latest`, `Nullable`: `enable`, `ImplicitUsings`: `enable`
   - `TreatWarningsAsErrors`: `true`, `AnalysisLevel`: `latest-recommended`
   - For library projects (`DotNodeViewer.Core`, `DotNodeViewer.Layout`): `IsAotCompatible=true`, `IsTrimmable=true`, `EnableTrimAnalyzer=true`, `EnableAotAnalyzer=true`, `GenerateDocumentationFile=true`.
   - Add a custom MSBuild target that fails the build if `DotNodeViewer.Core` or `DotNodeViewer.Layout` references any `Avalonia*` or `SkiaSharp*` package.
3. **`Directory.Packages.props`:** Enable Central Package Management (`ManagePackageVersionsCentrally=true`) with pinned versions for:
   - `TUnit`
   - `Avalonia`, `Avalonia.Desktop`, `Avalonia.Browser`, `Avalonia.Themes.Fluent`, `Avalonia.Headless`, `Avalonia.Diagnostics` (`11.3.12` per the Avalonia skill baseline, or `12.x` if recorded in ADR 0005)
   - `CommunityToolkit.Mvvm`
   - `Google.Protobuf`, `Google.FlatBuffers` (or `FlatSharp.Runtime`), `PureHDF` (used internally by `DotNodeViewer.Core` in later stages)
4. **Solution (`dot-node-viewer.slnx`) and Empty Projects:**
   - `src/DotNodeViewer.Core/DotNodeViewer.Core.csproj`
   - `src/DotNodeViewer.Layout/DotNodeViewer.Layout.csproj` (references `Core`)
   - `src/DotNodeViewer.App/DotNodeViewer.App.csproj` (references `Core`, `Layout`)
   - `src/DotNodeViewer.Desktop/DotNodeViewer.Desktop.csproj` (references `App`)
   - `src/DotNodeViewer.Browser/DotNodeViewer.Browser.csproj` (references `App`, or stubbed until `wasm-tools` workload is installed)
   - `tools/DotNodeViewer.Cli/DotNodeViewer.Cli.csproj` (references `Core`)
   - `tests/DotNodeViewer.Core.Tests/DotNodeViewer.Core.Tests.csproj` (references `Core`, `TUnit`, with 1 smoke `[Test]`)
   - `tests/DotNodeViewer.Layout.Tests/DotNodeViewer.Layout.Tests.csproj` (references `Layout`, `TUnit`, with 1 smoke `[Test]`)
   - `tests/DotNodeViewer.App.Tests/DotNodeViewer.App.Tests.csproj` (references `App`, `TUnit`, `Avalonia.Headless`, with 1 smoke `[Test]`)
5. **`.gitignore` Updates:** Ensure `.model-cache/`, `tools/model-gen/**/.venv/`, and `__pycache__/` are ignored.
6. **Architecture Decision Records (`docs/adr/`):**
   - `0001-hybrid-parsing-and-encapsulation.md`
   - `0002-immutable-record-ir.md`
   - `0003-lazy-weight-slices.md`
   - `0004-tunit.md`
   - `0005-avalonia-version.md`
7. **GitHub Actions CI (`.github/workflows/ci.yml`):** Run `dotnet build -c Release` and `dotnet test -c Release` on Ubuntu.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
grep -r "NetronNet" .agents/    # must return no matches
```

## Hand-off & Git Requirements
- Work on branch `stage/S00-bootstrap-and-governance` (`git checkout -b stage/S00-bootstrap-and-governance`).
- Commit your work incrementally in logical sub-commits prefixed with `S00:` (e.g., `S00: add build props and empty solution`, `S00: add foundational ADRs and CI workflow`, `S00: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S00** complete.
