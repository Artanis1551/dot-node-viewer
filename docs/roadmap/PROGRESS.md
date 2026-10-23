# Sequential Agent Progress Log (`dot-node-viewer`)

Every stage agent MUST read this file before starting work and append a completion entry before committing.

## Stage Status Table

| Stage | Brief | Branch | Status | Notes |
|---|---|---|---|---|
| **S00** | [`S00-bootstrap-and-governance.md`](stages/S00-bootstrap-and-governance.md) | `stage/S00-bootstrap-and-governance` | Complete | Solution scaffolding, `Directory.Build.props`, ADRs `0001`–`0005`, DocFX site, CI |
| **S01** | [`S01-core-ir-harness-and-cli.md`](stages/S01-core-ir-harness-and-cli.md) | `stage/S01-core-ir-harness-and-cli` | Complete | Unified Graph IR records, `ModelFormatRegistry`, `ModelFixtureLoader`, `DotNodeViewer.Cli` |
| **S02** | [`S02-onnx-parser-and-model-gen.md`](stages/S02-onnx-parser-and-model-gen.md) | `stage/S02-onnx-parser` | Complete | `ProtoReader`, ONNX detector & streaming reader, `tools/model-gen/onnx`, base ONNX models |
| **S03** | [`S03-tflite-parser-and-flatbuffers.md`](stages/S03-tflite-parser-and-flatbuffers.md) | `stage/S03-tflite-parser` | Complete | FlatBuffers support, TFLite detector & reader, quantization metadata, `tools/model-gen/tf` |
| **S04** | [`S04-layout-engine.md`](stages/S04-layout-engine.md) | `stage/S04-layout-engine` | Complete | `DotNodeViewer.Layout` pure C# Sugiyama/dagre layered layout + `ModelGraphLayoutAdapter` |
| **S05** | [`S05-ui-iteration-1a-design-system-and-shell.md`](stages/S05-ui-iteration-1a-design-system-and-shell.md) | `stage/S05-ui-design-and-shell` | Complete | `frontend-design` token system, Avalonia shell, file loading, `computer-use` screenshots |
| **S06** | [`S06-ui-iteration-1b-graph-canvas-and-properties.md`](stages/S06-ui-iteration-1b-graph-canvas-and-properties.md) | `stage/S06-ui-graph-canvas` | Complete | Custom `GraphCanvas`, pan/zoom/hit-test, basic node properties sidebar, `computer-use` verification |
| **S07** | [`S07-executorch-parser.md`](stages/S07-executorch-parser.md) | `stage/S07-executorch-parser` | Complete | ExecuTorch `.pte` detector & reader, delegate payloads, `tools/model-gen/torch` |
| **S08** | [`S08-pytorch-zip-pickle-and-torchscript.md`](stages/S08-pytorch-zip-pickle-and-torchscript.md) | `stage/S08-pytorch-torchscript` | Pending | `ZipDirectory`, safe `PickleMachine`, `state_dict` & TorchScript `.pt` reader |
| **S09** | [`S09-pytorch-pt2-export.md`](stages/S09-pytorch-pt2-export.md) | `stage/S09-pytorch-pt2` | Pending | PyTorch `torch.export` `.pt2` archive & `ExportedProgram` JSON reader |
| **S10** | [`S10-keras-and-hdf5.md`](stages/S10-keras-and-hdf5.md) | `stage/S10-keras-and-hdf5` | Pending | Keras v3 `.keras` + legacy `.h5` reader |
| **S11** | [`S11-ui-iteration-2-polish-inspector-and-navigation.md`](stages/S11-ui-iteration-2-polish-inspector-and-navigation.md) | `stage/S11-ui-polish-and-inspector` | Pending | On-demand tensor weight inspector, search, subgraph drill-down, minimap, `computer-use` critique |
| **S12** | [`S12-browser-wasm-aot-and-perf-hardening.md`](stages/S12-browser-wasm-aot-and-perf-hardening.md) | `stage/S12-wasm-aot-and-perf` | Pending | `DotNodeViewer.Browser` WASM head, NativeAOT publish, perf audit, NuGet packaging |
| **S13** | [`S13-avalonia-12-migration.md`](stages/S13-avalonia-12-migration.md) | `stage/S13-avalonia-12-migration` | Optional | Migrate Avalonia `11.3.12` → `12.x` via `avalonia-12-migration` skill |

---

## Deferred Items (Out-of-Scope Discoveries)

*(Stage agents append deferred enhancements or follow-ups here instead of expanding their current stage's scope.)*

- Post-roadmap feature briefs live in [`docs/wishes/`](../wishes/README.md) (first entry: [W01 — Ethos-U Command Stream Unwrapping](../wishes/W01-ethos-u-command-stream-unwrapping.md)).

---

## Stage Completion Entries

### Planning Baseline (Initial Setup)
- **Completed:**
  - Created [`AGENTS.md`](../../AGENTS.md), [`PREREQUISITES.md`](../../PREREQUISITES.md), and [`GLOSSARY.md`](../../GLOSSARY.md).
  - Copied all required skills into `.agents/skills/` (`port-model-format`, `add-test-model`, `csharp-tunit`, `csharp-docs`, `csharp-async`, `analyzing-dotnet-performance`, `frontend-design`, `development-plugin-for-avalonia`, `computer-use`, `grill-with-docs`, `improve-codebase-architecture`).
  - Updated `port-model-format` and `add-test-model` skills to use `DotNodeViewer` naming and `TUnit`.
  - Authored self-contained stage briefs `S00`–`S13` under `docs/roadmap/stages/`.
- **Next Stage:** Run **Stage S00** (`docs/roadmap/stages/S00-bootstrap-and-governance.md`).

### Stage S00 — Bootstrap, Governance & Solution Scaffolding
- **Branch:** `stage/S00-bootstrap-and-governance`
- **Commits:**
  - `3656a87` — `S00: add build props, central package management, and solution scaffolding`
  - `b32087b` — `S00: add foundational ADRs, DocFX site scaffolding, CI workflow, and README`
- **What Was Built:**
  - Created `global.json` pinning the .NET 10 SDK (`10.0.100`, `rollForward: latestFeature`) and configuring `Microsoft.Testing.Platform`.
  - Created `Directory.Build.props` enforcing `net10.0`, `LangVersion: latest`, `Nullable: enable`, `ImplicitUsings: enable`, `TreatWarningsAsErrors: true`, `AnalysisLevel: latest-recommended`, NativeAOT/trimming analyzers (`IsAotCompatible=true`, `IsTrimmable=true`, `EnableTrimAnalyzer=true`, `EnableAotAnalyzer=true`, `GenerateDocumentationFile=true`) on `DotNodeViewer.Core` and `DotNodeViewer.Layout`, and custom MSBuild target `ValidateNoUiDependenciesInCoreAndLayout` blocking any `Avalonia*` or `SkiaSharp*` references in `Core` or `Layout`.
  - Created `Directory.Packages.props` with Central Package Management (`TUnit` `1.76.0`, `Avalonia*` `11.3.12`, `CommunityToolkit.Mvvm` `8.4.0`, `Google.Protobuf` `3.29.3`, `Google.FlatBuffers` `24.3.25`, `FlatSharp.Runtime` `7.8.0`, `PureHDF` `1.0.0-beta.17`, and transitive security bump `Tmds.DBus.Protocol` `0.21.3` for `GHSA-xrw6-gwf8-vvr9`).
  - Created `dot-node-viewer.slnx` and all 9 projects (`DotNodeViewer.Core`, `DotNodeViewer.Layout`, `DotNodeViewer.App`, `DotNodeViewer.Desktop`, `DotNodeViewer.Browser`, `DotNodeViewer.Cli`, `DotNodeViewer.Core.Tests`, `DotNodeViewer.Layout.Tests`, `DotNodeViewer.App.Tests`) with XML-documented baseline types and smoke `[Test]` methods.
  - Updated `.gitignore` to ignore `.model-cache/`, `tools/model-gen/**/.venv/`, `__pycache__/`, `docs/site/_site/`, and `docs/site/api/*.yml`.
  - Added `.config/dotnet-tools.json` registering `docfx` (`2.78.3`) and `.github/workflows/ci.yml` running build, TUnit tests, and DocFX build on Ubuntu.
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) with project vision, architectural highlights, solution structure, quick start commands, and documentation links.
  - Created `docs/site/docfx.json`, `docs/site/toc.yml`, `docs/site/index.md`, `docs/site/api/index.md`, `docs/site/articles/toc.yml`, and Diátaxis articles (`getting-started.md`, `library/unified-graph-ir.md`, `formats/overview.md`, `cli/reference.md`, `visualizer/user-guide.md`).
- **New ADRs Recorded:**
  - [`docs/adr/0001-hybrid-parsing-and-encapsulation.md`](../adr/0001-hybrid-parsing-and-encapsulation.md)
  - [`docs/adr/0002-immutable-record-ir.md`](../adr/0002-immutable-record-ir.md)
  - [`docs/adr/0003-lazy-weight-slices.md`](../adr/0003-lazy-weight-slices.md)
  - [`docs/adr/0004-tunit.md`](../adr/0004-tunit.md)
  - [`docs/adr/0005-avalonia-version.md`](../adr/0005-avalonia-version.md)
- **Deviations & Known Limitations:**
  - In .NET 10 SDK (`10.0.113`) with `"test": { "runner": "Microsoft.Testing.Platform" }` in `global.json`, `dotnet test` requires `--solution dot-node-viewer.slnx` when specifying a solution explicitly (`dotnet test --solution dot-node-viewer.slnx -c Release` or `dotnet test -c Release`).
  - Added `tests/TestEnvironmentInitializer.cs` (linked into all `*.Tests` projects via `Directory.Build.props`) and `DisableAvaloniaBuildTelemetry` target so builds and `Microsoft.Testing.Platform` runs remain hermetic in read-only `$HOME/.local` sandbox environments.
  - `DotNodeViewer.Browser` is stubbed as a `net10.0` library referencing `DotNodeViewer.App` and `Avalonia.Browser` until the `wasm-tools` workload and `net10.0-browser` head are activated in Stage S12.
- **Test Count:** 3 passed (`DotNodeViewer.Core.Tests`: 1, `DotNodeViewer.Layout.Tests`: 1, `DotNodeViewer.App.Tests`: 1), 0 failed, 0 skipped.
- **Screenshots Taken:** None (no UI changes in Stage S00).
- **Verification Commands Run:**
  - `dotnet tool restore`
  - `dotnet build dot-node-viewer.slnx -c Release`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
  - `grep -r "NetronNet" .agents/` (no matches)
- **Next Stage:** Run **Stage S01** (`docs/roadmap/stages/S01-core-ir-harness-and-cli.md`).

### Stage S01 — Core IR, Abstractions, Test Harness & Snapshot CLI
- **Branch:** `stage/S01-core-ir-harness-and-cli`
- **Commits:**
  - `a88de35` — `S01: add Unified Graph IR records and ADR 0006`
  - `d2f59d9` — `S01: add abstractions and RandomAccessSource`
  - `c44e958` — `S01: add ModelFixtureLoader, manifest tests, and snapshot CLI`
  - *(final commit)* — `S01: update README, DocFX site, and PROGRESS.md`
- **What Was Built:**
  - Implemented the frozen public Unified Graph IR in `src/DotNodeViewer.Core/Ir/` with complete XML documentation:
    - `ModelGraph`, `ModelFormatInfo`, `ModelFormatId`, `Subgraph`, `GraphNode`, `OperatorInfo`, `OperatorCategory`, `NodePort`, `GraphPort`
    - `TensorEdge`, `TensorKind`, `TensorDescriptor`, `DataType`, `TensorShape`, and closed union `TensorDimension` (`Fixed`, `Symbolic`, `Dynamic`)
    - Closed union `QuantizationInfo` (`AffineQuantization`, `BlockQuantization`)
    - Closed union `TensorDataSource` (`TensorDataSlice`, `ExternalTensorDataReference`) and `TensorDataEncoding` (`Raw`, `VarintPacked`)
    - `NodeAttribute` and 13-case closed union `AttributeValue` (`Int64`, `Float64`, `Bool`, `String`, `Int64List`, `Float64List`, `StringList`, `DataTypeValue`, `ShapeValue`, `Tensor`, `Graph`, `GraphList`, `Bytes`)
    - `ParseDiagnostic` and `DiagnosticSeverity`
  - Implemented public parser and inspection abstractions in `src/DotNodeViewer.Core/Abstractions/`:
    - `IModelFormatDetector`, `IModelFormatReader`, `ModelProbe` (64-byte header peek, normalized file extension, lazy ZIP central-directory entry listing, always restoring `Stream.Position`), `ModelFormatRegistry` (reflection-free registration, detection, and `ReadAsync`), `ModelReadOptions`, `ModelParseException` (with `FileOffset` and `Context`), `GraphIndex` (lazy producer/consumer and 1-hop upstream/downstream lookup), and `TensorDataLoader` (zero-allocation on-demand slice reading).
  - Implemented internal I/O primitives in `src/DotNodeViewer.Core/IO/`:
    - `RandomAccessSource`: LRU 64 KB page cache backed by `ArrayPool<byte>.Shared` over a seekable `Stream` with `GetSpan`, `ReadExactly`, and primitive readers, throwing `ModelParseException` on non-seekable or out-of-bounds reads.
    - `BinaryCursor`: bounds-checked sequential `BinaryPrimitives` span reader tracking `FileOffset`.
  - Implemented the manifest-driven TUnit test harness in `tests/DotNodeViewer.Core.Tests/`:
    - `ModelFixtureLoader`, `SnapshotBuilder`, `HarnessJsonContext` (source-generated JSON serialization for `models.manifest.json` and `*.expected.json`), `AllocationProbe` (`GC.GetAllocatedBytesForCurrentThread()`), `ModelManifestTests.ParseRegisteredModel_MatchesExpectedTopology` (`[Test]` + `[MethodDataSource]`), `PublicApiEncapsulationTests`, and unit tests for IR records, `ModelProbe`, `ModelFormatRegistry`, `GraphIndex`, `TensorDataLoader`, `RandomAccessSource`, and `BinaryCursor`.
    - Initialized `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json` (`{"schemaVersion": 1, "models": []}`).
  - Implemented `tools/DotNodeViewer.Cli/` (`detect <file>`, `inspect <file>`, and `snapshot <file> --id <model-id>`) with NativeAOT-compatible source-generated JSON snapshot output.
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) with programmatic `DotNodeViewer.Core` usage examples, `DotNodeViewer.Cli` command reference, and NativeAOT publish instructions.
  - Expanded [`docs/site/articles/library/unified-graph-ir.md`](../site/articles/library/unified-graph-ir.md) with comprehensive reference and C# usage examples for `ModelGraph`, `Subgraph`, `GraphNode`, `TensorEdge`, `TensorShape`, `TensorDataSlice`, `ModelFormatRegistry`, `GraphIndex`, and `TensorDataLoader`.
  - Expanded [`docs/site/articles/cli/reference.md`](../site/articles/cli/reference.md) with complete `detect`, `inspect`, and `snapshot` usage and exit codes.
- **New ADRs Recorded:**
  - [`docs/adr/0006-ir-v1.md`](../adr/0006-ir-v1.md) — Frozen v1 Unified Graph IR & Format Abstraction Contract.
- **Deviations & Known Limitations:**
  - None. When `models.manifest.json` has an empty `models` array in Stage S01, `ModelManifestTests.GetRegisteredModels()` yields a single `null` sentinel so TUnit executes `ParseRegisteredModel_MatchesExpectedTopology` and validates the manifest schema; once Stage S02 registers ONNX fixtures, `GetRegisteredModels()` yields each registered `ModelManifestEntry`.
- **Test Count:** 19 passed (`DotNodeViewer.Core.Tests`: 17, `DotNodeViewer.Layout.Tests`: 1, `DotNodeViewer.App.Tests`: 1), 0 failed, 0 skipped.
- **Screenshots Taken:** None (no UI changes in Stage S01).
- **Verification Commands Run:**
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true`
  - `./tools/DotNodeViewer.Cli/bin/Release/net10.0/linux-x64/publish/DotNodeViewer.Cli --help`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Run **Stage S02** (`docs/roadmap/stages/S02-onnx-parser-and-model-gen.md`).

### Stage S02 — ONNX Parser, Protobuf Streaming & Model-Generation Framework
- **Branch:** `stage/S02-onnx-parser`
- **Commits:**
  - `afaaf1c` — `S02: add streaming ProtoReader`
  - `98d13f8` — `S02: implement ONNX detector, reader, and operator catalog`
  - `560fa7e` — `S02: add ONNX model-gen, fixtures, and golden snapshots`
  - *(final commit)* — `S02: update README, DocFX site, and PROGRESS.md`
- **What Was Built:**
  - Implemented `ProtoReader` (`src/DotNodeViewer.Core/IO/ProtoReader.cs`, `internal`) over `RandomAccessSource` with nested message limit tracking (`PushMessageLimit` / `PushLimit` / `PopLimit`), varint and fixed-width primitive decoding, packed/unpacked repeated field readers, `SkipField`, and zero-allocation `CaptureLengthDelimitedRange` / `CaptureLengthDelimitedSlice` so raw tensor payloads are recorded by offset/length and skipped in $O(1)$ without allocating `byte[]` buffers.
  - Implemented the ONNX format parser under `src/DotNodeViewer.Core/Formats/Onnx/` (`internal`) and registered it in `ModelFormatRegistry.Default`:
    - `OnnxFormatDetector`: non-destructive bounded Protobuf tag-walk validating `ModelProto` (`ir_version`, `graph`, `opset_import`, and header fields) while rejecting ZIP, HDF5, FlatBuffers (`TFL3`/`ET12`), and Pickle signatures and restoring `Stream.Position`.
    - `OnnxFormatReader`: streams `ModelProto`, `GraphProto`, `NodeProto`, `AttributeProto`, `TensorProto`, `SparseTensorProto`, `ValueInfoProto`, `TypeProto`, `TensorShapeProto`, and `FunctionProto` into `ModelGraph`, filtering initializers out of `GraphInput` ports for ONNX IR v3 compatibility, mapping `Fixed`/`Symbolic`/`Dynamic` dimensions and unranked/scalar shapes, capturing `raw_data` (`Raw`), packed `float_data`/`double_data` (`Raw`), packed `int32_data`/`int64_data`/`uint64_data` (`VarintPacked`), and `external_data` (`ExternalTensorDataReference`), and recursively parsing control-flow subgraphs (`If`, `Loop`, `Scan`) into `AttributeValue.Graph` / `GraphList`.
    - `OnnxOperatorCatalog`: maps standard `ai.onnx` operators to `OperatorCategory`, formal input/output port names (`X`, `W`, `B`, `Y`, `A`, `C`, etc.), and `TensorProto.DataType` codes to `DataType`.
  - Created the deterministic ONNX Python model-generation toolchain (`tools/model-gen/onnx/requirements.txt` with `onnx==1.17.0`, `numpy==1.26.4`, `protobuf==5.29.3`, and `tools/model-gen/onnx/gen_onnx.py`) emitting both `.onnx` binaries and independent `.truth.json` ground-truth metadata.
  - Staged and registered 4 committed fixtures (`tiny_mlp.onnx`, `tiny_cnn_dynamic_batch.onnx`, `if_subgraph.onnx`, `external_data.onnx` + `external_data.bin`) and 1 cached fixture (`mobilenetv2-7.onnx`, 14.2 MB from the ONNX Model Zoo) in `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json` with golden `.expected.json` snapshots verified against `.truth.json`.
  - Added `ProtoReaderTests`, `Synthetic/OnnxSyntheticTests` (all 4 mandatory `port-model-format` tests), and `Formats/OnnxModelTests` (control-flow subgraphs, external data references, `.truth.json` cross-checks, and `mobilenetv2-7.onnx` `< 10%` allocation budget test).
  - **Pattern Established for Subsequent Format Stages (`S03`, `S07`–`S10`):**
    1. Keep all format-specific wire readers and catalogs `internal` under `DotNodeViewer.Core.Formats.<Format>` and register in `ModelFormatRegistry.CreateDefault()`.
    2. Collect raw graph/function items during wire traversal and resolve `TensorEdge` references after reading the container so field ordering on the wire never affects port or opset resolution.
    3. Emit deterministic `<stem>.truth.json` files alongside generated fixtures in `tools/model-gen/<toolchain>/` and assert in `Formats/<Format>ModelTests.cs` that `.expected.json` snapshots match `.truth.json`.
- **`README.md` & `docs/site/` Updates:**
  - Added the Supported Model Formats table to root [`README.md`](../../README.md) highlighting ONNX support.
  - Added [`docs/site/articles/formats/onnx.md`](../site/articles/formats/onnx.md) and linked it in [`docs/site/articles/formats/overview.md`](../site/articles/formats/overview.md) and [`docs/site/articles/toc.yml`](../site/articles/toc.yml).
- **New ADRs Recorded:**
  - None (strictly followed ADRs `0001`–`0006`).
- **Deviations & Known Limitations:**
  - In `github.com/onnx/models` on `main`, `mobilenetv2-7.onnx` resides under `validated/vision/classification/mobilenet/model/mobilenetv2-7.onnx`; registered that canonical URL in `models.manifest.json`.
- **Test Count:** 36 passed (`DotNodeViewer.Core.Tests`: 34, `DotNodeViewer.Layout.Tests`: 1, `DotNodeViewer.App.Tests`: 1), 0 failed, 0 skipped.
- **Screenshots Taken:** None (no UI changes in Stage S02).
- **Verification Commands Run:**
  - `tools/model-gen/onnx/.venv/bin/python tools/model-gen/onnx/gen_onnx.py`
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `dotnet publish tools/DotNodeViewer.Cli/DotNodeViewer.Cli.csproj -c Release -p:PublishAot=true --no-restore`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Run **Stage S03** (`docs/roadmap/stages/S03-tflite-parser-and-flatbuffers.md`).

### Stage S03 — TFLite Parser & FlatBuffers Support
- **Branch:** `stage/S03-tflite-parser`
- **Commits:**
  - `e03b969` — `S03: add FlatBuffers reading support`
  - `22c0d77` — `S03: implement TFLite detector, reader, and quantization mapping`
  - `e261c3a` — `S03: add TFLite model-gen, fixtures, and golden snapshots`
  - *(final commit)* — `S03: update README, DocFX site, and PROGRESS.md`
- **What Was Built:**
  - Implemented `FlatBufferTable` (`src/DotNodeViewer.Core/IO/FlatBuffers/FlatBufferTable.cs`, `internal`) providing bounds-checked FlatBuffers vtable/table/vector/union reading over `RandomAccessSource` and zero-allocation `CaptureByteVectorSlice` for extracting `TensorDataSlice` byte ranges without reading payload bytes onto the managed heap.
  - Implemented the TensorFlow Lite (`TFL3`) parser under `src/DotNodeViewer.Core/Formats/Tflite/` (`internal`) and registered it in `ModelFormatRegistry.Default`:
    - `TfliteFormatDetector`: validates the `TFL3` file identifier at bytes `4..8`, root table offset at `0..4`, and root vtable bounds while restoring `Stream.Position`.
    - `TfliteFormatReader`: decodes `operator_codes` (`builtin_code` vs `deprecated_builtin_code`, `version`, and `custom_code`), `buffers` (both inline `Buffer.data` byte vectors and v3c `offset`/`size` fields as lazy `TensorDataSlice` references), `subgraphs` (`tensors`, `inputs`, `outputs`, `operators`, `name`), `shape` and `shape_signature` (`-1` mapped to `TensorDimension.Dynamic`), `QuantizationParameters` (`scale`, `zero_point`, `quantized_dimension` mapped to `AffineQuantization`), `builtin_options` (including `padding`, `strides`, `dilations`, `depth_multiplier`, `filter_size`, `fused_activation_function`, `keep_dims`, `axis`, `new_shape`, etc.), `IF`/`WHILE`/`CALL_ONCE` nested `AttributeValue.Graph` attributes, and `CUSTOM` delegate operators (`custom_code` and `custom_options` as `AttributeValue.Bytes(TensorDataSlice)`).
    - `TfliteOperatorCatalog`: maps TFLite `BuiltinOperator` codes to canonical operator names, `OperatorCategory`, formal port names, and `DataType`.
  - Created `tools/model-gen/tf/requirements.txt` (`tensorflow-cpu==2.18.0`, `keras==3.8.0`, `h5py==3.12.1`, `numpy==1.26.4`) and `tools/model-gen/tf/gen_tflite.py` generating `tiny_cnn_float.tflite`, `tiny_cnn_int8.tflite`, `tiny_while.tflite` and independent `.truth.json` ground-truth metadata.
  - Staged and registered 3 committed fixtures (`tiny_cnn_float.tflite`, `tiny_cnn_int8.tflite`, `tiny_while.tflite`) and 1 cached fixture (`mobilenet_v2_1.0_224_quant.tflite`, 3.6 MB from `google-coral/test_data`) in `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json` with golden `.expected.json` snapshots verified against `.truth.json`.
  - Added `FlatBufferTableTests`, `Synthetic/TfliteSyntheticTests`, and `Formats/TfliteModelTests`.
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) supported-formats table with TensorFlow Lite (`TFL3`).
  - Added [`docs/site/articles/formats/tflite.md`](../site/articles/formats/tflite.md) and linked it in [`docs/site/articles/formats/overview.md`](../site/articles/formats/overview.md) and [`docs/site/articles/toc.yml`](../site/articles/toc.yml).
- **New ADRs Recorded:**
  - None (strictly followed ADRs `0001`–`0006`).
- **Deviations & Known Limitations:**
  - None.
- **Test Count:** 52 passed (`DotNodeViewer.Core.Tests`: 50, `DotNodeViewer.Layout.Tests`: 1, `DotNodeViewer.App.Tests`: 1), 0 failed, 0 skipped.
- **Screenshots Taken:** None (no UI changes in Stage S03).
- **Verification Commands Run:**
  - `tools/model-gen/tf/.venv/bin/python tools/model-gen/tf/gen_tflite.py`
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `dotnet publish tools/DotNodeViewer.Cli/DotNodeViewer.Cli.csproj -c Release -p:PublishAot=true --no-restore`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Run **Stage S04** (`docs/roadmap/stages/S04-layout-engine.md`).

### Stage S04 — Layout Engine (`DotNodeViewer.Layout`)
- **Branch:** `stage/S04-layout-engine`
- **Commits:**
  - `b3be57b` — `S04: implement Sugiyama layout engine with Brandes-Kopf coordinates and edge routing`
  - `63f397e` — `S04: add ModelGraphLayoutAdapter and layout tests`
  - *(final commit)* — `S04: update README, DocFX site, and PROGRESS.md`
- **What Was Built:**
  - Implemented the pure C# Sugiyama/dagre layered graph layout engine in `src/DotNodeViewer.Layout/` (`IGraphLayoutEngine`, `SugiyamaLayoutEngine`, `SugiyamaPipeline`):
    - Input/output geometry and graph models: `LayoutDirection` (`TopToBottom`, `LeftToRight`), `LayoutPoint`, `BoundingBox` (with `Intersects` and `Contains`), `CubicBezierSegment`, `LayoutOptions`, `LayoutPort`, `LayoutNode`, `LayoutEdge`, `LayoutGraph`, `PositionedPort`, `PositionedNode`, `PositionedEdge`, and `PositionedGraph`.
    - Six-stage Sugiyama pipeline over compact integer-indexed arrays:
      1. Iterative (stack-safe) DFS feedback arc set cycle removal (`RemoveCycles`).
      2. Kahn's topological longest-path layer ranking followed by tight-tree slack refinement (`AssignLayers`).
      3. Multi-layer edge normalization inserting dummy routing vertices for `rankDelta > 1` (`NormalizeAndBuildLayers`).
      4. Alternating top-down / bottom-up port-aware barycenter crossing minimization sweeps with adjacent-exchange transpose refinement (`MinimizeCrossings`).
      5. Directional Brandes–Köpf median coordinate alignment with 1D non-overlapping block compaction (`AssignCoordinates`).
      6. Port anchor resolution, dummy-chain polyline & cubic Bézier spline routing, midpoint label anchor computation, and reversed cycle endpoint restoration (`BuildPositionedGraph`).
  - Implemented `ITextMeasurer` and `MonospaceTextMeasurer` (`src/DotNodeViewer.Layout/ITextMeasurer.cs`) providing a UI-free text measurement seam.
  - Implemented `ModelGraphLayoutAdapter` and `VisualSubgraphLayout` (`src/DotNodeViewer.Layout/ModelGraphLayoutAdapter.cs`, `VisualSubgraphLayout.cs`):
    - Collapses static `TensorKind.Initializer` and `TensorKind.Constant` inputs into `CollapsedParameterSlot` rows inside operator node cards.
    - Represents `Subgraph.Inputs` and `Subgraph.Outputs` as compact `VisualNodeKind.GraphInput` / `GraphOutput` terminal pills.
    - Surfaces fused activation attributes (`fused_activation_function` / `activation`) as `VisualNode.FusedActivationBadge`.
  - Added `SugiyamaLayoutEngineTests` (determinism, `TopToBottom` and `LeftToRight` DAG flow and non-overlap, 2-node/3-node/self-loop cycle resilience, and 5,000-node `< 1,000 ms` performance budget test) and `ModelGraphLayoutAdapterTests` (real-fixture layout verification across all committed ONNX and TFLite models).
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) with a `Layout Engine (DotNodeViewer.Layout)` overview and programmatic example.
  - Added [`docs/site/articles/library/layout-engine.md`](../site/articles/library/layout-engine.md) and linked it in [`docs/site/articles/toc.yml`](../site/articles/toc.yml).
- **New ADRs Recorded:**
  - [`docs/adr/0007-isolated-model-test-ci-jobs-and-pipeline-reporting.md`](../adr/0007-isolated-model-test-ci-jobs-and-pipeline-reporting.md) — Isolated Model Test CI Jobs and Pipeline Inspection Reporting.
- **Deviations & Known Limitations:**
  - None.
- **Test Count:** 64 passed (`DotNodeViewer.Core.Tests`: 50, `DotNodeViewer.Layout.Tests`: 13, `DotNodeViewer.App.Tests`: 1), 0 failed, 0 skipped.
- **Screenshots Taken:** None (no UI changes in Stage S04).
- **Verification Commands Run:**
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Run **Stage S05** (`docs/roadmap/stages/S05-ui-iteration-1a-design-system-and-shell.md`).

### Stage S05 — UI Iteration 1a: Design System & Avalonia App Shell
- **Branch:** `stage/S05-ui-design-and-shell`
- **Commits:**
  - `9ad2120` — `S05: author design-system.md`
  - `ed36764` — `S05: implement Avalonia theme tokens, MainWindow shell, and headless tests`
  - `eb88d13` — `S05: add live desktop verification harness and screenshots`
  - *(final commit)* — `S05: update README, DocFX site, and PROGRESS.md`
- **What Was Built:**
  - Authored the Design System and UI identity specification in [`docs/design/design-system.md`](../design/design-system.md) following `frontend-design` and Avalonia token architecture, calibrated against common AI cliches, reviewed with user approval, and tailored for both ML and edge/embedded firmware engineers:
    - Base palette tokens: Deep Graphite (`#12151B`) / Charcoal Slate (`#1A1F29`) for Dark theme and Slate Porcelain (`#F8FAFC`) / White (`#FFFFFF`) for Light theme.
    - Colorblind-checked semantic palette for all 12 `OperatorCategory` enum values (`Layer`, `Activation`, `Normalization`, `Pooling`, `Quantization`, `Shape`, `Tensor`, `Constant`, `ControlFlow`, `Data`, `Custom`, `Unknown`).
    - Dedicated quantization scheme badges: `INT8`/`UINT8` (hardware acceleration green), `FP32` (slate), `FP16`/`BF16` (cyan), and `INT4` (purple).
    - Proportional sans-serif and tabular monospace font stacks and explicit type scale.
  - Implemented the theme token dictionaries in `src/DotNodeViewer.App/Themes/`:
    - `Tokens.axaml`: Canvas background, surface base/elevated, border, text primary/secondary, accent, and danger/warning/success brushes with `ThemeDictionaries` (`Default` for Dark and `Light`).
    - `NodePalette.axaml`: SolidColorBrush tokens for all 12 operator categories and quantization badges.
  - Implemented the Avalonia application shell (`src/DotNodeViewer.App/` and `src/DotNodeViewer.Desktop/`):
    - `App.axaml` and `App.axaml.cs`: Configures `FluentTheme`, merges theme token dictionaries, and wires desktop lifetime to `MainWindow` and `MainWindowViewModel`.
    - CLI Model Launch Argument: Passing a model file path to `DotNodeViewer.Desktop` (e.g., `dotnet run --project src/DotNodeViewer.Desktop -- <path-to-model>`) automatically streams, parses, and renders that model upon window launch.
    - `MainWindowViewModel`: Built with `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`), background model loading pipeline with `FileStream`, `ModelFormatRegistry.Default.ReadAsync`, and `ModelGraphLayoutAdapter.Default.CreateLayout`, dynamic theme toggling (`Dark` vs `Light`), error handling, recent models tracking, and subgraph switching.
    - `MainWindow.axaml` and `MainWindow.axaml.cs`: Top toolbar (brand mark, `Open Model...`, subgraph dropdown, theme toggle), drop target with drag-and-drop handling via `e.DataTransfer.TryGetFiles()`, inviting empty state with quick sample launchers, actionable error overlay with hex byte offsets, and Model Overview Card (format badge, producer, metrics grid, inputs/outputs lists with shapes and quantization badges, and operator category distribution).
    - Accessibility / AT-SPI: Assigned `AutomationProperties.Name` and `AutomationProperties.AutomationId` across interactive controls (`OpenModelButton`, `ThemeToggleButton`, `SubgraphSelector`, `ModelSummaryPanel`, `ErrorBanner`).
  - Added unit and headless integration tests in `tests/DotNodeViewer.App.Tests/MainWindowViewModelTests.cs`:
    - Verified initial empty state contract.
    - Verified opening committed ONNX models (`tiny_mlp.onnx`, `tiny_cnn_dynamic_batch.onnx`).
    - Verified opening committed quantized TFLite model (`tiny_cnn_int8.tflite`) and asserting `INT8` badges and scale/zero-point parameters.
    - Verified error state populating `ModelParseException` file offset and context cleanly.
    - Verified theme toggling (`Dark` <-> `Light`).
    - Verified subgraph selection.
  - Created `tools/desktop_harness.py` for live X11/AT-SPI window capture and automation.
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) with a new Desktop Visualizer section, CLI launch arguments, visualizer feature list, and embedded shell screenshots.
  - Authored Diátaxis How-To guide in [`docs/site/articles/visualizer/user-guide.md`](../site/articles/visualizer/user-guide.md) with tutorials and screenshots for desktop launch, file loading, telemetry inspection, and theme toggling.
  - Added `resource` pattern in `docs/site/docfx.json` and mirrored screenshots to `docs/site/images/s05/`.
- **New ADRs Recorded:**
  - None (strictly followed ADRs `0001`–`0007`).
- **Deviations & Known Limitations:**
  - None.
- **Test Count:** 74 passed (`DotNodeViewer.Core.Tests`: 50, `DotNodeViewer.Layout.Tests`: 13, `DotNodeViewer.App.Tests`: 11), 0 failed, 0 skipped.
- **Screenshots Taken:**
  - [Empty State (Dark)](../screenshots/S05/01-empty-state-dark.png)
  - [Loaded ONNX Dynamic Batch Model (Dark)](../screenshots/S05/02-loaded-onnx-dark.png)
  - [Loaded Quantized TFLite Model (Dark)](../screenshots/S05/03-loaded-tflite-int8-dark.png)
  - [Loaded Quantized TFLite Model (Light)](../screenshots/S05/04-loaded-tflite-int8-light.png)
- **Verification Commands Run:**
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S05/01-empty-state-dark.png`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S05/02-loaded-onnx-dark.png`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S05/03-loaded-tflite-int8-dark.png`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S05/04-loaded-tflite-int8-light.png`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Run **Stage S06** (`docs/roadmap/stages/S06-ui-iteration-1b-graph-canvas-and-properties.md`).

### Stage S06 — UI Iteration 1b: Graph Canvas & Basic Node Properties
- **Branch:** `stage/S06-ui-graph-canvas`
- **Commits:**
  - `087eae4` — `S06: implement custom GraphCanvas with pan/zoom and spatial hit-testing`
  - `7c2020b` — `S06: add PropertiesPanelView v1 and wire to MainWindow`
  - `b2fa2c7` — `S06: add headless render tests and computer-use screenshots`
  - `2deb3e8` — `S06: update README, DocFX site, and PROGRESS.md`
  - *(current commit)* — `S06: dynamic sizing of node boxes, robust parameter row layouts, touchpad pinch, scroll wheel zoom/pan, and keyboard shortcuts`
- **What Was Built:**
  - Implemented `AvaloniaTextMeasurer` (`src/DotNodeViewer.App/Rendering/AvaloniaTextMeasurer.cs`) implementing `ITextMeasurer` via Avalonia's `FormattedText` text engine with bounding cache and safe fallback, guaranteeing node widths in layout calculation match rendered font metrics on the active platform.
  - Implemented `SpatialGridIndex` (`src/DotNodeViewer.App/Rendering/SpatialGridIndex.cs`) providing $O(1)$ spatial hash lookups over `VisualNode` bounding boxes for pointer hit-testing and viewport region culling.
  - Implemented `GraphCanvas` (`src/DotNodeViewer.App/Controls/GraphCanvas.cs`):
    - Direct immediate-mode rendering via Avalonia's `DrawingContext` with zero control-per-node allocations.
    - Viewport culling skips non-visible nodes and Bézier curves.
    - Multi-tiered Level-of-Detail (LOD): degrades fine labels and subtitle strings at low zoom levels to sustain 60 fps rendering on large models (`mobilenetv2`).
    - Full interaction suite:
      - Smooth panning via left-click canvas background drag, middle/right button drag anywhere, Shift + scroll wheel, two-finger scroll gesture, and keyboard arrow keys (`Left`, `Right`, `Up`, `Down`).
      - Zoom-around-cursor via `Ctrl` + mouse wheel, unmodified mouse wheel, touchpad pinch gesture (`PinchGestureRecognizer` / `Gestures.PinchEvent` with `ScaleOrigin`), touchpad magnify (`PointerTouchPadGestureMagnifyEvent`), keyboard shortcuts (`Ctrl` + `+`/`=`, `Ctrl` + `-`, `Ctrl` + `0`), and floating HUD buttons.
    - Dynamic sizing & visual clutter fixes in `ModelGraphLayoutAdapter.cs`:
      - Dynamic node width calculation accurately measuring port name, margin, summary pill, and quantization badge to prevent line-wrapping or overlapping adjacent elements.
      - Dynamic terminal pill height: expands to 46px when subtitle/shape is present to guarantee clean vertical clearance between title and subtitle.
      - Increased layer separation to 68px for clean Bézier spline flow and edge shape callout breathing room.
      - Clean single-badge quantization rendering without duplicate suffixes.
    - Rounded operator node cards using design tokens with colorblind-checked `OperatorCategory` header bands, titles, subtitles, collapsed initializer parameter rows (`W · float32 [64, 3, 3, 3]`), and fused activation badges (`+ RELU`).
    - Cubic Bézier spline routing with directional arrowheads, tensor shape pills, and emerald green hardware acceleration indicators for quantized activations (`INT8` / `UINT8`).
    - Spatial hit-testing for hover highlights and click-to-select.
  - Implemented Basic Properties Sidebar v1 (`src/DotNodeViewer.App/Views/PropertiesPanelView.axaml` and `PropertiesPanelView.axaml.cs`):
    - **When no node is selected (Graph Overview):** Displays format badge, producer, active subgraph, primary metrics (total nodes, edges, tensors, quantized tensors), inputs and outputs lists with dimensions and quantization parameters, and category distribution.
    - **When a node is selected (Node Inspector v1):** Displays operator name, category badge, domain/version, typed attributes list (`Int64[]`, `Float64`, `String`, `TensorShape`), input connections distinguishing runtime activations from initializer weights, output tensor descriptors with affine quantization parameters, and a deselect action (`✕`).
  - Added unit, integration, and headless rendering tests in `tests/DotNodeViewer.App.Tests/`:
    - `SpatialGridIndexTests`: bounds checking, corner hits, miss detection, and region deduplication.
    - `AvaloniaTextMeasurerTests`: positive metrics, bold weight widening, null/empty safety.
    - `GraphCanvasTests`: default state, zoom commands and percent formatting, `ScreenToWorld` / `WorldToScreen` roundtrip accuracy, headless bitmap render of `tiny_mlp.onnx` and `tiny_cnn_int8.tflite` to `RenderTargetBitmap`, node selection, attributes inspection, deselect reset, and keyboard (`Ctrl`+`+`, `Ctrl`+`-`, `Ctrl`+`0`, arrows) / pointer wheel / pinch gesture navigation tests.
  - Extended live desktop verification harness (`tools/desktop_harness.py`) with `click-ratio`, `drag-ratio`, `scroll`, `key-combo`, and window navigation.
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) visualizer feature list and added interactive graph canvas and properties inspector screenshots.
  - Updated [`docs/site/articles/visualizer/user-guide.md`](../site/articles/visualizer/user-guide.md) with canvas navigation instructions, semantic operator card visuals, edge callouts, and the contextual properties sidebar.
  - Authored Diátaxis How-To article [`docs/site/articles/visualizer/embedding-controls.md`](../site/articles/visualizer/embedding-controls.md) demonstrating how .NET developers can embed `GraphCanvas` in their own Avalonia apps, and linked it in `docs/site/articles/toc.yml`.
  - Mirrored live UI screenshots to `docs/site/images/s06/` and verified `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors` passes with 0 warnings.
- **New ADRs Recorded:**
  - None (strictly followed ADRs `0001`–`0007`).
- **Deviations & Known Limitations:**
  - None.
- **Test Count:** 86 passed (`DotNodeViewer.Core.Tests`: 50, `DotNodeViewer.Layout.Tests`: 13, `DotNodeViewer.App.Tests`: 23), 0 failed, 0 skipped.
- **Screenshots Taken:**
  - [Interactive Graph Canvas Dark (ONNX Dynamic Batch)](../screenshots/S06/01-canvas-onnx-dynamic-batch.png)
  - [Node Inspector Sidebar with Selected Node](../screenshots/S06/02-canvas-node-selected-properties.png)
  - [Quantized TFLite INT8 Graph with Quantized Callouts](../screenshots/S06/03-canvas-tflite-quantized-int8.png)
  - [Level-of-Detail (LOD) Zoomed Out Canvas](../screenshots/S06/04-canvas-lod-zoomed-out.png)
  - [Light Theme Canvas](../screenshots/S06/05-canvas-light-theme.png)
- **Verification Commands Run:**
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `DISPLAY=:0 dotnet run --project src/DotNodeViewer.Desktop -c Release --no-restore -- tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_cnn_dynamic_batch.onnx`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S06/01-canvas-onnx-dynamic-batch.png`
  - `DISPLAY=:0 python3 tools/desktop_harness.py click-ratio 0.35 0.35`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S06/02-canvas-node-selected-properties.png`
  - `DISPLAY=:0 python3 tools/desktop_harness.py scroll down 0.35 0.35 10`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S06/04-canvas-lod-zoomed-out.png`
  - `DISPLAY=:0 python3 tools/desktop_harness.py click-ratio 0.94 0.03`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S06/05-canvas-light-theme.png`
  - `DISPLAY=:0 dotnet run --project src/DotNodeViewer.Desktop -c Release --no-restore -- tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite`
  - `DISPLAY=:0 python3 tools/desktop_harness.py capture docs/screenshots/S06/03-canvas-tflite-quantized-int8.png`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Optional checkpoint: run `.agents/skills/improve-codebase-architecture/SKILL.md` before proceeding to **Stage S07** (`docs/roadmap/stages/S07-executorch-parser.md`).

### Stage S07 — ExecuTorch Parser (`.pte`)
- **Branch:** `stage/S07-executorch-parser`
- **Commits:**
  - `S07: add ExecuTorch detector, reader, and operator catalog`
  - `S07: add torch/executorch model-gen, fixtures, and golden snapshots`
  - `S07: add synthetic and model integration tests`
  - `S07: update README, DocFX site, CI, and PROGRESS.md`
- **What Was Built:**
  - Implemented the ExecuTorch format parser under `src/DotNodeViewer.Core/Formats/ExecuTorch/` (`internal`) and registered it in `ModelFormatRegistry.Default`:
    - `ExecuTorchFormatDetector`: detects `ET12` (and `ET1x` with version diagnostic) file identifiers at bytes `4..8`, validates FlatBuffer root table and vtable offsets, and inspects optional extended headers (`eh00` at byte offset `8`) without modifying `stream.Position`.
    - `ExecuTorchFormatReader`:
      - Decodes `Program.execution_plan[]` into `Subgraph` instances.
      - Decodes `Program.values[]` (`EValue` union: `Tensor`, `Int`, `Double`, `Bool`, `String`, `IntList`, `DoubleList`, `BoolList`, `TensorList`).
      - Resolves `Tensor` metadata: scalar type mapping, static vs. dynamic/symbolic shapes (`STATIC` -> `Fixed`, `DYNAMIC_BOUND` with `-1` or `DYNAMIC_UNBOUND` -> `Dynamic`).
      - Resolves zero-allocation lazy weights into `TensorDataSlice`: inline `constant_buffer` and segmented `constant_segment` with `DataSegment` offsets rebased by `segment_base_offset`, as well as `ExternalTensorDataReference` for external data references.
      - Maps `chains[].instructions[]`: `KernelCall` (resolving out-variant formal arguments, operator inputs/outputs, and scalar hyperparameters into `NodeAttribute`s), `DelegateCall` (`OperatorCategory.Custom`, `Delegate (<backend_id>)`, with compiled delegate payloads captured as lazy `AttributeValue.Bytes(TensorDataSlice)` under `processed`), `MoveCall` (identity tensor op), `JumpFalseCall` (`OperatorCategory.ControlFlow`), and `FreeCall` (omitted from visual topology).
    - `ExecuTorchOperatorCatalog`: maps ATen operators and ExecuTorch primitives to `OperatorCategory`, scalar types to `DataType`, and computes element storage byte sizes.
  - Extended `FlatBufferTable`: added bounds-checked `ReadDouble`, `ReadDoubleVector`, and `ReadBoolVector` methods over `RandomAccessSource`.
  - Set up `tools/model-gen/torch/`: configured `requirements.txt` with `executorch==1.5.1` and `torch==2.14.1` and created deterministic generator `gen_executorch.py` emitting `.pte` models and independent ground-truth `.truth.json` metadata.
  - Staged and registered 2 committed fixtures in `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json`:
    - `tiny_mlp_portable.pte`: portable ATen kernels (`aten::permute_copy.out`, `aten::addmm.out`, `aten::relu.out`, `aten::add.out`) and lazy weight slices.
    - `tiny_cnn_xnnpack.pte`: lowered to `XnnpackPartitioner` delegate (`Delegate (XnnpackBackend)`) verifying delegate payload slicing.
  - Added unit, synthetic, and integration tests in `tests/DotNodeViewer.Core.Tests/`:
    - `Synthetic/ExecuTorchSyntheticTests.cs`: the 4 mandatory tests (header probing & position restoration, dynamic bound shapes and XNNPACK delegate payload slicing, zero heap allocation lazy weight slices, and corrupted/truncated stream resilience).
    - `Formats/ExecuTorchModelTests.cs`: portable ops and weight slices verification, XNNPACK delegate payload verification, and `.truth.json` parity.
    - `ModelManifestTests`: added `GetRegisteredExecuTorchModels` and `ParseRegisteredExecuTorchModel_MatchesExpectedTopology`.
    - `.github/workflows/ci.yml`: added `model-tests-executorch` job and updated synthetic test filter.
- **`README.md` & `docs/site/` Updates:**
  - Updated root [`README.md`](../../README.md) supported-formats table with ExecuTorch (`.pte`).
  - Added Diátaxis Reference article [`docs/site/articles/formats/executorch.md`](../site/articles/formats/executorch.md) covering ExecuTorch architecture, extended headers, segmented weights, and backend delegate payloads, and linked it in `toc.yml` and `overview.md`.
  - Built DocFX metadata and site cleanly with 0 warnings.
- **New ADRs Recorded:** None (strictly followed ADRs `0001`–`0007`).
- **Deviations & Known Limitations:**
  - Upgraded PyTorch virtual environment to `executorch==1.5.1` and `torch==2.14.1` per user request and compatibility with Python 3.12.
- **Test Count:** 94 passed (`DotNodeViewer.Core.Tests`: 58, `DotNodeViewer.Layout.Tests`: 13, `DotNodeViewer.App.Tests`: 23), 0 failed, 0 skipped.
- **Screenshots Taken:** None (format parser stage, no UI alterations).
- **Verification Commands Run:**
  - `tools/model-gen/torch/.venv/bin/python tools/model-gen/torch/gen_executorch.py`
  - `dotnet build dot-node-viewer.slnx -c Release --no-restore`
  - `dotnet test --solution dot-node-viewer.slnx -c Release --no-build`
  - `dotnet publish tools/DotNodeViewer.Cli/DotNodeViewer.Cli.csproj -c Release -p:PublishAot=true -r linux-x64`
  - `dotnet tool run docfx metadata docs/site/docfx.json --warningsAsErrors`
  - `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`
- **Next Stage:** Run **Stage S08** (`docs/roadmap/stages/S08-pytorch-zip-pickle-and-torchscript.md`).
