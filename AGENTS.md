# AGENTS.md — Workspace Rules for `dot-node-viewer`

This repository contains **dot-node-viewer**, a high-performance .NET 10 rewrite of Netron split into a pure C# core model-parsing library (`DotNodeViewer.Core`), a pure C# layered layout engine (`DotNodeViewer.Layout`), and a cross-platform Avalonia visualizer (`DotNodeViewer.App`, `DotNodeViewer.Desktop`, `DotNodeViewer.Browser`).

Development is organized into sequential stages (`S00` through `S13`). Every agent working in this repository MUST follow these rules.

---

## 1. Stage-Agent Protocol (Read Before Making Changes)

1. **Read First:**
   - [`AGENTS.md`](AGENTS.md) (this file)
   - [`PREREQUISITES.md`](PREREQUISITES.md) (development machine SDKs, Python venvs, and `cua-driver` / AT-SPI setup)
   - [`docs/roadmap/PROGRESS.md`](docs/roadmap/PROGRESS.md) (hand-off log from prior stage agents)
   - Your assigned stage brief in [`docs/roadmap/stages/`](docs/roadmap/stages/)
   - [`GLOSSARY.md`](GLOSSARY.md) and relevant ADRs in `docs/adr/`
2. **Work on a Dedicated Stage Branch & Commit Incrementally:**
   - Before making changes, create and switch to a dedicated branch for your stage (branched off the previous stage's completed branch or `main`), named `stage/Sxx-<short-slug>` (e.g., `git checkout -b stage/S02-onnx-parser`).
   - You are explicitly **allowed and encouraged to make git commits** throughout your stage. Split your work into logical, self-contained sub-commits where it makes sense (for example: one commit for internal I/O primitives, one for the format detector/reader, one for Python model-gen & staged fixtures, and a final commit updating `docs/roadmap/PROGRESS.md`), prefixing commit messages with `Sxx: <summary>`.
3. **Load Assigned Skills:** Read the `SKILL.md` file in `.agents/skills/<skill-name>/SKILL.md` for every skill listed in your stage brief before writing code.
4. **Frozen Contracts:** Once Stage `S01` is complete, do not break the public Unified Graph IR (`DotNodeViewer.Core.Ir`), public parser abstractions (`DotNodeViewer.Core.Abstractions`), or `models.manifest.json` / `.expected.json` schemas without recording a new ADR in `docs/adr/` and noting it in `docs/roadmap/PROGRESS.md`.
5. **Stay in Scope:** Implement only your assigned stage. Log any out-of-scope follow-ups under the **Deferred** section of `docs/roadmap/PROGRESS.md`.
6. **Definition of Done (Every Stage):**
   - `dotnet build dot-node-viewer.slnx -c Release` succeeds with **zero warnings** (`TreatWarningsAsErrors=true`).
   - `dotnet test dot-node-viewer.slnx -c Release` is **100% green**.
   - All public types and members have XML documentation comments (`/// <summary>`).
   - Golden topology snapshots (`*.expected.json`) are cross-checked against an independent ground truth (`*.truth.json` from Python model generators or format inspection)—**never blindly regenerated**.
   - **UI Changes:** Whenever any UI change is made, you MUST follow the **Mandatory Live UI Verification & Screenshot Rule** (Section 4).
   - **Update `README.md` & Website Documentation (`docs/site/`) via `documentation-writer`:** Before a stage can be marked done:
     1. Read [`.agents/skills/documentation-writer/SKILL.md`](.agents/skills/documentation-writer/SKILL.md) and structure documentation updates following the **Diátaxis framework** (Tutorials, How-to Guides, Reference, Explanation).
     2. The repository's root [`README.md`](README.md) MUST be updated to reflect the capabilities, supported formats, CLI/UI features, or architecture added in that stage.
     3. The documentation site files in `docs/site/` (DocFX Markdown guides, format support pages, CLI/UI user guides, library API articles, and `toc.yml` table-of-contents files) MUST be updated in lockstep, and `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors` must pass cleanly.
7. **Hand-off:** Append a structured completion entry to `docs/roadmap/PROGRESS.md` (branch name, commits made, what was built, `README.md` and `docs/site/` updates, deviations, known limitations, new ADRs, test count, screenshots taken, and exact verification commands run) and commit it on your stage branch, leaving `git status` clean.

---

## 2. Project Map & Dependency Direction

```
dot-node-viewer.slnx
├── src/
│   ├── DotNodeViewer.Core/       # Pure C# Unified Graph IR + format detectors & streaming readers
│   ├── DotNodeViewer.Layout/     # Pure C# Sugiyama/dagre layered graph layout (depends ONLY on Core)
│   ├── DotNodeViewer.App/        # Shared Avalonia UI, ViewModels, GraphCanvas, Themes (depends on Core + Layout)
│   ├── DotNodeViewer.Desktop/    # Cross-platform desktop executable (Win / macOS / Linux)
│   └── DotNodeViewer.Browser/    # WebAssembly (net10.0-browser) head
├── tools/
│   ├── DotNodeViewer.Cli/        # CLI tool: `snapshot`, `inspect`, `detect`
│   └── model-gen/                # Deterministic Python scripts + pinned requirements (`onnx/`, `tf/`, `torch/`)
├── docs/
│   ├── site/                     # DocFX documentation website source (guides, format docs, CLI/UI manual, C# API metadata)
│   ├── adr/                      # Architecture Decision Records
│   ├── design/                   # UI design system & token specification
│   ├── screenshots/              # Stage verification screenshots (`S05/`, `S06/`, `S11/`, `S12/`)
│   └── roadmap/                  # `PROGRESS.md` and stage briefs (`stages/S00..S13-*.md`)
└── tests/
    ├── DotNodeViewer.Core.Tests/ # TUnit tests: synthetic fixtures + manifest-driven golden snapshots
    ├── DotNodeViewer.Layout.Tests/
    └── DotNodeViewer.App.Tests/  # Headless Avalonia + ViewModel tests
```

**Strict Dependency Direction:**
`DotNodeViewer.Core` ← `DotNodeViewer.Layout` ← `DotNodeViewer.App` ← (`DotNodeViewer.Desktop`, `DotNodeViewer.Browser`)
`DotNodeViewer.Core` ← `DotNodeViewer.Cli`

---

## 3. Non-Negotiable Architectural Guardrails (`DotNodeViewer.Core`)

1. **Zero UI or Layout Leakage in `Core`:**
   - `DotNodeViewer.Core` and `DotNodeViewer.Layout` MUST NOT reference Avalonia, SkiaSharp, or any UI/rendering packages.
   - `DotNodeViewer.Core` contains no X/Y coordinates, colors, or layout state.
2. **Pure Immutable C# Record Unified Graph IR (No Leaked Format Schemas):**
   - Output strictly maps to the public immutable records in `DotNodeViewer.Core.Ir` (`ModelGraph`, `Subgraph`, `GraphNode`, `NodePort`, `GraphPort`, `TensorEdge`, `TensorDescriptor`, `TensorShape`, `TensorDimension`, `NodeAttribute`, `AttributeValue`, `QuantizationInfo`, `TensorDataSource`).
   - Do **not** create a FlatBuffers schema for exporting our IR.
   - Format-specific deserialization types (Protobuf classes, FlatBuffers structs, HDF5 readers, Pickle VMs, JSON DTOs) MUST be marked `internal` inside `DotNodeViewer.Core`. They may use AOT/WASM-compatible libraries (`Google.Protobuf`, `Google.FlatBuffers`/`FlatSharp`, `PureHDF`, `System.IO.Compression.ZipArchive`) combined with custom span/stream readers, but MUST NEVER appear in public signatures or exceptions. Wrap all format errors in `ModelParseException` (with file offset and node/tensor context).
3. **Zero-Allocation Topology Scanning & Lazy Weight Slicing (WASM-Safe):**
   - **BANNED on model streams:** `File.ReadAllBytes`, `Stream.ToArray()` on full model files, or eagerly allocating `byte[]`/`float[]` arrays for raw tensor weights during `IModelFormatReader.ReadAsync`.
   - Record raw weights as a `TensorDataSlice(StreamOffset, ByteLength, DataType, Encoding)` and seek/skip past the payload so weights are only read on demand via `TensorDataLoader` when inspected in the UI.
   - Use `ReadOnlySpan<byte>`, `BinaryPrimitives`, and `ArrayPool<byte>.Shared` for header and metadata scanning.
4. **NativeAOT & WebAssembly Trimming Compatibility:**
   - All library code must pass `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsAotCompatible>` with zero warnings.
   - Never use unbounded reflection or untyped `JsonSerializer.Deserialize`. Always use source-generated `JsonSerializerContext` or `Utf8JsonReader` / `JsonDocument`.
5. **Async & API Hygiene:**
   - Async methods use the `Async` suffix, accept `CancellationToken`, and call `.ConfigureAwait(false)` in library code (`Core` and `Layout`).
   - All public classes/records are `sealed` unless designed as a closed discriminated-union hierarchy (`abstract record` with `sealed` nested/derived records).

---

## 4. Mandatory Live UI Verification & Screenshot Rule (`computer-use`)

Whenever you make **any UI changes** (in Stages `S05`, `S06`, `S11`, `S12`, `S13`, or any ad-hoc UI task), you MUST verify the live desktop application in addition to running headless tests:

1. Read [`.agents/skills/computer-use/SKILL.md`](.agents/skills/computer-use/SKILL.md) and [`.agents/skills/frontend-design/SKILL.md`](.agents/skills/frontend-design/SKILL.md).
2. Launch `DotNodeViewer.Desktop` in the background (e.g., `dotnet run --project src/DotNodeViewer.Desktop -- <path-to-fixture>`).
3. Use the `computer_use` tool (`computer_use(action="capture", mode="som", app="DotNodeViewer")` and `mode="vision"`) to capture screenshots and inspect the Linux AT-SPI accessibility tree **in the background without stealing focus** (`raise_window=false`).
4. Drive the modified controls (`click`, `scroll`, `type`, `key` with `capture_after=True`) to verify interactive behavior and capture before/after states.
5. Save the captured PNG screenshots to `docs/screenshots/<stage>/`, critique them against the design system (`docs/design/design-system.md`), and link them in `docs/roadmap/PROGRESS.md`.
6. Stop the background `DotNodeViewer.Desktop` process cleanly when verification is complete.

---

## 5. Test Fixtures & Golden Snapshots (`add-test-model`)

- **Test Framework:** **TUnit** (`[Test]`, `[Arguments]`, `[MethodDataSource]`, `await Assert.That(...)`). See [`.agents/skills/csharp-tunit/SKILL.md`](.agents/skills/csharp-tunit/SKILL.md).
- **Fixture Tiers (`256 KB` cutoff):**
  - `<= 256 KB`: committed in `tests/DotNodeViewer.Core.Tests/Fixtures/<format>/`.
  - `> 256 KB`: stored in git-ignored `tests/DotNodeViewer.Core.Tests/.model-cache/<format>/` with `sourceUrl`, lowercase `sha256`, and `byteLength` registered in `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json`.
- Use `.agents/skills/add-test-model/scripts/stage-model.sh` to stage models and compute SHA-256 digests.

---

## 6. Installed Repository Skills Reference

| Skill | Path | When to Use |
|---|---|---|
| `port-model-format` | `.agents/skills/port-model-format/SKILL.md` | Implementing any format detector/reader or mapping to the Unified Graph IR |
| `add-test-model` | `.agents/skills/add-test-model/SKILL.md` | Staging a test model, registering in `models.manifest.json`, and creating `.expected.json` |
| `csharp-tunit` | `.agents/skills/csharp-tunit/SKILL.md` | Writing or running unit/data-driven tests with TUnit |
| `csharp-docs` | `.agents/skills/csharp-docs/SKILL.md` | Documenting public/internal C# types with XML comments |
| `documentation-writer` | `.agents/skills/documentation-writer/SKILL.md` | Authoring and maintaining `README.md` and `docs/site/` (Diátaxis framework: Tutorials, How-to Guides, Reference, Explanation) |
| `csharp-async` | `.agents/skills/csharp-async/SKILL.md` | Writing async streaming I/O or background tasks |
| `analyzing-dotnet-performance` | `.agents/skills/analyzing-dotnet-performance/SKILL.md` | Auditing hot paths and allocation budgets |
| `frontend-design` | `.agents/skills/frontend-design/SKILL.md` | Designing or critiquing UI identity, typography, palette, and copywriting |
| `development-plugin-for-avalonia` | `.agents/skills/development-plugin-for-avalonia/SKILL.md` | All Avalonia XAML, compiled bindings, custom drawing, lifetimes, and headless testing |
| `computer-use` | `.agents/skills/computer-use/SKILL.md` | Running, driving, and screenshotting `DotNodeViewer.Desktop` in the background |
| `grill-with-docs` | `.agents/skills/grill-with-docs/SKILL.md` | User-invoked design/ADR interview |
| `improve-codebase-architecture` | `.agents/skills/improve-codebase-architecture/SKILL.md` | User-invoked architectural deepening review |

---

## 7. Standard Commands

```bash
# Build entire solution (Release, zero warnings allowed)
dotnet build dot-node-viewer.slnx -c Release

# Run all TUnit tests
dotnet test dot-node-viewer.slnx -c Release

# Run a filtered TUnit test class or method (Microsoft.Testing.Platform filter syntax)
dotnet test --project tests/DotNodeViewer.Core.Tests -c Release -- --treenode-filter "/*/*/ModelManifestTests/*"

# Generate or inspect a model snapshot via the CLI harness
dotnet run --project tools/DotNodeViewer.Cli -c Release -- snapshot <path-to-model> --id <model-id>
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect <path-to-model>

# Verify NativeAOT & trimming cleanliness on Core + CLI
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true

# Build the static documentation website (DocFX) from docs/site/ + C# XML docs
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```
