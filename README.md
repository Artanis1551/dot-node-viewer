# `dot-node-viewer`

**`dot-node-viewer`** is a high-performance, NativeAOT- and WebAssembly-compatible `.NET 10` rewrite of [Netron](https://github.com/lutzroeder/netron), split into reusable pure-C# libraries for neural network graph extraction (`DotNodeViewer.Core`) and layered graph layout (`DotNodeViewer.Layout`), paired with a cross-platform Avalonia visualizer (`DotNodeViewer.App`, `DotNodeViewer.Desktop`, `DotNodeViewer.Browser`) and CLI tool (`DotNodeViewer.Cli`).

---

## Architectural Highlights

- **Pure Immutable C# Record Unified Graph IR (`DotNodeViewer.Core.Ir`):**
  Every supported model format maps into a strongly typed, immutable record hierarchy (`ModelGraph`, `Subgraph`, `GraphNode`, `NodePort`, `GraphPort`, `TensorEdge`, `TensorDescriptor`, `TensorShape`, `NodeAttribute`, `QuantizationInfo`, `TensorDataSource`). Format-specific binary schemas are strictly `internal`.
- **Zero-Allocation Topology Scanning & Lazy Weight Slices:**
  Model parsers never buffer full model files in memory or allocate arrays for tensor weights during graph extraction. Raw weight payloads are recorded as lightweight `TensorDataSlice(StreamOffset, ByteLength, DataType, Encoding)` references and loaded on demand via `TensorDataLoader`.
- **Zero UI Leakage in Core & Layout:**
  `DotNodeViewer.Core` and `DotNodeViewer.Layout` have zero dependencies on `Avalonia`, `SkiaSharp`, or rendering packages—enforced at build time via MSBuild guardrails in `Directory.Build.props`.
- **NativeAOT & WebAssembly Ready:**
  Library projects compile with `IsAotCompatible=true`, `IsTrimmable=true`, `EnableTrimAnalyzer=true`, and `EnableAotAnalyzer=true` with `TreatWarningsAsErrors=true`.

---

## Solution Structure

```text
dot-node-viewer.slnx
├── src/
│   ├── DotNodeViewer.Core/       # Pure C# Unified Graph IR + format detectors & streaming readers
│   ├── DotNodeViewer.Layout/     # Pure C# Sugiyama/dagre layered graph layout (depends ONLY on Core)
│   ├── DotNodeViewer.App/        # Shared Avalonia UI, ViewModels, GraphCanvas, Themes (depends on Core + Layout)
│   ├── DotNodeViewer.Desktop/    # Cross-platform desktop executable (Linux / macOS / Windows)
│   └── DotNodeViewer.Browser/    # WebAssembly (net10.0-browser) head
├── tools/
│   ├── DotNodeViewer.Cli/        # CLI tool: detect, inspect, snapshot
│   └── model-gen/                # Deterministic Python scripts + pinned requirements (onnx/, tf/, torch/)
├── docs/
│   ├── site/                     # DocFX documentation website source (Diátaxis guides + C# API reference)
│   ├── adr/                      # Architecture Decision Records (0001..0005+)
│   └── roadmap/                  # PROGRESS.md and sequential stage briefs (S00..S13)
└── tests/
    ├── DotNodeViewer.Core.Tests/ # TUnit tests: synthetic fixtures + manifest-driven golden snapshots
    ├── DotNodeViewer.Layout.Tests/
    └── DotNodeViewer.App.Tests/  # Headless Avalonia + ViewModel tests
```

---

## Quick Start

See [`PREREQUISITES.md`](PREREQUISITES.md) for full development machine setup instructions.

```bash
# Restore local .NET tools (DocFX)
dotnet tool restore

# Build entire solution in Release mode (zero warnings allowed)
dotnet build dot-node-viewer.slnx -c Release

# Run all TUnit tests on Microsoft.Testing.Platform
dotnet test dot-node-viewer.slnx -c Release

# Build the DocFX documentation website and API reference
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```

---

## Documentation & Governance

- [**Documentation Website Source (`docs/site/`)**](docs/site/index.md)
  - [Tutorial: Getting Started](docs/site/articles/getting-started.md)
  - [Explanation: Unified Graph IR & Architecture](docs/site/articles/library/unified-graph-ir.md)
  - [Reference: Supported Model Formats](docs/site/articles/formats/overview.md)
  - [Reference: CLI Tool (`DotNodeViewer.Cli`)](docs/site/articles/cli/reference.md)
  - [How-To: Visualizer User Guide](docs/site/articles/visualizer/user-guide.md)
- [**Development Prerequisites (`PREREQUISITES.md`)**](PREREQUISITES.md)
- [**Domain Glossary (`GLOSSARY.md`)**](GLOSSARY.md)
- [**Architecture Decision Records (`docs/adr/`)**](docs/adr/)
- [**Roadmap & Stage Progress (`docs/roadmap/PROGRESS.md`)**](docs/roadmap/PROGRESS.md)
