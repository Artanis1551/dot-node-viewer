# How-To: Installing NuGet Packages & Publishing Releases

This guide covers how downstream `.NET 8+` developers install and verify **`DotNodeViewer.Core`**, **`DotNodeViewer.Layout`**, and **`DotNodeViewer.Cli`**, as well as how repository maintainers cut SemVer-tagged releases via GitHub Actions.

---

## 1. Installing the Libraries (`DotNodeViewer.Core` & `DotNodeViewer.Layout`)

Both core libraries target `net8.0` (compatible with `.NET 8`, `.NET 9`, and `.NET 10`), compile cleanly with `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>`, and carry zero UI or SkiaSharp dependencies.

### From NuGet.org

```bash
# Core model format detection, streaming graph IR extraction, and lazy weight slicing
dotnet add package DotNodeViewer.Core

# Sugiyama/dagre layered graph layout engine + spatial grid index (depends on DotNodeViewer.Core)
dotnet add package DotNodeViewer.Layout
```

### From GitHub Packages (Pre-Releases & Backup Feed)

Every tagged release is also pushed to GitHub Packages (`https://nuget.pkg.github.com/Artanis1551/index.json`). To consume packages from GitHub Packages, register the feed with a GitHub Personal Access Token (`read:packages`):

```bash
dotnet nuget add source "https://nuget.pkg.github.com/Artanis1551/index.json" \
  --name github-dot-node-viewer \
  --username <your-github-username> \
  --password <your-github-pat> \
  --store-password-in-clear-text
```

### Minimal Consumption Example

```csharp
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

await using FileStream stream = File.OpenRead("model.onnx");

// 1. Probe & parse topology without eagerly loading raw weight buffers
ModelGraph graph = await ModelFormatRegistry.Default.ReadAsync(
    stream,
    new ModelReadOptions { FileName = "model.onnx" });

// 2. Compute deterministic layered coordinates and spatial index
VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(
    graph,
    new LayoutOptions { Direction = LayoutDirection.TopToBottom });

Console.WriteLine($"Nodes: {layout.Nodes.Count}, Edges: {layout.Edges.Count}");
```

---

## 2. Installing the CLI Global Tool (`DotNodeViewer.Cli`)

`DotNodeViewer.Cli` is packaged as a .NET Global Tool exposing the `dot-node-viewer` command:

```bash
# Install globally from NuGet.org
dotnet tool install -g DotNodeViewer.Cli

# Verify installation and inspect a model
dot-node-viewer --help
dot-node-viewer detect model.onnx
dot-node-viewer inspect model.onnx --expand-delegates
```

To update to the latest version:

```bash
dotnet tool update -g DotNodeViewer.Cli
```

---

## 3. Source Link & Symbol Debugging (`.snupkg`)

`DotNodeViewer.Core` and `DotNodeViewer.Layout` embed deterministic **Source Link** metadata (`PublishRepositoryUrl=true`, `EmbedUntrackedSources=true`) and publish companion symbol packages (`.snupkg`) to NuGet.org.

- **Visual Studio / Rider / VS Code:** Enable *Source Link support* and the *NuGet.org Symbol Server* in debugger settings to step directly into `DotNodeViewer.Core` and `DotNodeViewer.Layout` source code pinned to the exact release commit.
- **GitHub Packages Note:** GitHub Packages does not host `.snupkg` symbol packages; download `.snupkg` files from the corresponding [GitHub Release](https://github.com/Artanis1551/dot-node-viewer/releases) or NuGet.org if you need offline symbols.

---

## 4. Verifying SLSA Build Provenance & Checksums

Every release published by `.github/workflows/publish-nuget.yml` generates:
- `SHA256SUMS.txt` attached to the GitHub Release alongside the `.nupkg` and `.snupkg` artifacts.
- Cryptographic **SLSA Build Provenance** attestations via `actions/attest-build-provenance@v2`.

You can verify a downloaded `.nupkg` against the repository's GitHub attestation before consuming it:

```bash
gh attestation verify DotNodeViewer.Core.0.12.0.nupkg --repo Artanis1551/dot-node-viewer
```

---

## 5. Maintainer Release & Git Tagging Protocol

### Centralized Versioning (`Directory.Build.props`)

Package versions for `DotNodeViewer.Core`, `DotNodeViewer.Layout`, and `DotNodeViewer.Cli` are governed centrally by `<VersionPrefix>` in `Directory.Build.props` (following **SemVer 2.0.0**). All shared NuGet metadata (`Authors`, `License`, `RepositoryUrl`, `PackageReadmeFile`, Source Link, and `<EnablePackageValidation>true</EnablePackageValidation>`) lives there as well.

Release tags MUST follow `v<MAJOR>.<MINOR>.<PATCH>` (for example `v0.12.0`) or `v<MAJOR>.<MINOR>.<PATCH>-<PRERELEASE>` (for example `v0.13.0-preview.1`). Once a tag is pushed and published to NuGet.org, it is immutable.

### Cutting a Release with `tools/tag-release.sh`

1. Ensure you are on an up-to-date `main` branch with a clean working tree.
2. Preview validation checks without modifying Git state:

```bash
./tools/tag-release.sh --dry-run 0.12.0
```

3. Run the release script to validate SemVer syntax, run `dotnet build dot-node-viewer.sln -c Release` and `dotnet test dot-node-viewer.sln -c Release --no-build`, bump `<VersionPrefix>` in `Directory.Build.props` if needed, commit `chore: release v0.12.0`, and create an annotated Git tag:

```bash
./tools/tag-release.sh 0.12.0
```

4. Push the commit and tag to trigger `.github/workflows/publish-nuget.yml`:

```bash
git push origin main
git push origin v0.12.0
```

### NuGet Trusted Publishing (OIDC)

The `publish` job in `.github/workflows/publish-nuget.yml` authenticates to NuGet.org using **Trusted Publishing** (`NuGet/login@v1`) with GitHub OIDC (`id-token: write`)—no long-lived `NUGET_API_KEY` secret is stored in GitHub.

One-time repository & NuGet.org setup:
1. Configure a NuGet.org Trusted Publishing policy scoped to `DotNodeViewer.*` for repository `Artanis1551/dot-node-viewer`, workflow `publish-nuget.yml`, and environment `nuget-publish`.
2. In GitHub **Settings → Environments**, create the `nuget-publish` environment.
3. Add the `NUGET_USER` secret (your NuGet.org username / profile name, not email address) to the `nuget-publish` environment (or repository Actions secrets).

### Dry-Run Verification via GitHub Actions

Before pushing a live release tag, maintainers can manually trigger **Publish NuGet Packages** (`workflow_dispatch`) from the GitHub Actions UI with `dry_run: true`. The workflow will:
- Validate SemVer syntax and `<VersionPrefix>` parity against `Directory.Build.props`.
- Build in `Release` mode (`TreatWarningsAsErrors=true`) and run all `Core`, `Layout`, and `App` TUnit suites.
- Verify NativeAOT compilation of `DotNodeViewer.Cli` (`PublishAot=true`).
- Pack `DotNodeViewer.Core`, `DotNodeViewer.Layout`, and `DotNodeViewer.Cli`, smoke-test installing them into a fresh console project and tool path, and upload the `.nupkg`, `.snupkg`, and `SHA256SUMS.txt` workflow artifacts without publishing.
