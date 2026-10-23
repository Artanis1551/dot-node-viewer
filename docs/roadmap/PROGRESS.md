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
| **S05** | [`S05-ui-iteration-1a-design-system-and-shell.md`](stages/S05-ui-iteration-1a-design-system-and-shell.md) | `stage/S05-ui-design-and-shell` | Pending | `frontend-design` token system, Avalonia shell, file loading, `computer-use` screenshots |
| **S06** | [`S06-ui-iteration-1b-graph-canvas-and-properties.md`](stages/S06-ui-iteration-1b-graph-canvas-and-properties.md) | `stage/S06-ui-graph-canvas` | Pending | Custom `GraphCanvas`, pan/zoom/hit-test, basic node properties sidebar, `computer-use` verification |
| **S07** | [`S07-executorch-parser.md`](stages/S07-executorch-parser.md) | `stage/S07-executorch-parser` | Pending | ExecuTorch `.pte` detector & reader, delegate payloads, `tools/model-gen/torch` |
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
  - None.
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
