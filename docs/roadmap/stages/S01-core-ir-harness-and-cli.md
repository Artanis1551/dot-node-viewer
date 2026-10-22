# Stage S01 — Core IR, Abstractions, Test Harness & Snapshot CLI

## Goal
Implement the frozen public Unified Graph IR (`DotNodeViewer.Core.Ir`), public format detection/reading abstractions (`DotNodeViewer.Core.Abstractions`), shared internal I/O primitives (`RandomAccessSource`, `BinaryCursor`), the manifest-driven TUnit test harness (`ModelFixtureLoader`, `ModelManifestTests`), and the `DotNodeViewer.Cli` snapshot/inspect tool.

## Prerequisites
- Stage **S00** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`GLOSSARY.md`](../../../GLOSSARY.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/port-model-format/SKILL.md` (and `references/ir-mapping-checklist.md`)
- `.agents/skills/add-test-model/SKILL.md` (and `references/manifest-and-snapshot-schema.md`)
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`

## Scope & Deliverables
1. **Unified Graph IR (`src/DotNodeViewer.Core/Ir/`):**
   - Immutable `public sealed record` types with full XML documentation:
     - `ModelGraph(ModelFormatInfo Format, string? Producer, IReadOnlyList<Subgraph> Subgraphs, IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<ParseDiagnostic> Diagnostics)`
     - `ModelFormatInfo(ModelFormatId Id, string DisplayName, string? Version = null)` and `readonly record struct ModelFormatId(string Value)`
     - `Subgraph(string Name, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<TensorEdge> Tensors, IReadOnlyList<GraphPort> Inputs, IReadOnlyList<GraphPort> Outputs)`
     - `GraphNode(string Name, OperatorInfo Operator, IReadOnlyList<NodePort> Inputs, IReadOnlyList<NodePort> Outputs, IReadOnlyList<NodeAttribute> Attributes, string? Documentation = null)`
     - `OperatorInfo(string Name, OperatorCategory Category, string? Domain = null, int? Version = null)`
     - `OperatorCategory` enum (`Layer`, `Activation`, `Normalization`, `Pooling`, `Quantization`, `Shape`, `Tensor`, `Constant`, `ControlFlow`, `Data`, `Custom`, `Unknown`)
     - `NodePort(string Name, IReadOnlyList<TensorEdge> Tensors)` and `GraphPort(string Name, IReadOnlyList<TensorEdge> Tensors)`
     - `TensorEdge(string Name, TensorDescriptor Descriptor, TensorKind Kind, TensorDataSource? Data = null)`
     - `TensorKind` enum (`Activation`, `Initializer`, `Constant`, `GraphInput`, `GraphOutput`)
     - `TensorDescriptor(DataType DataType, TensorShape? Shape, QuantizationInfo? Quantization = null, string? Layout = null)` (`Shape == null` means unranked; `Shape.Dimensions.Count == 0` means scalar)
     - `TensorShape(IReadOnlyList<TensorDimension> Dimensions)` and closed hierarchy `TensorDimension` (`Fixed(long Value)`, `Symbolic(string Name)`, `Dynamic`)
     - Closed hierarchy `TensorDataSource`:
       - `TensorDataSlice(long StreamOffset, long ByteLength, DataType DataType, TensorDataEncoding Encoding = TensorDataEncoding.Raw)`
       - `ExternalTensorDataReference(string RelativePath, long Offset, long? Length, DataType DataType)`
     - Closed hierarchy `QuantizationInfo`:
       - `AffineQuantization(IReadOnlyList<float> Scale, IReadOnlyList<long> ZeroPoint, int? QuantizedDimension = null)`
       - `BlockQuantization(string Scheme, int BlockSize, int Bits)`
     - `NodeAttribute(string Name, AttributeValue Value)` and closed hierarchy `AttributeValue` (`Int64`, `Float64`, `Bool`, `String`, `Int64List`, `Float64List`, `StringList`, `DataTypeValue`, `ShapeValue`, `Tensor`, `Graph`, `GraphList`, `Bytes(TensorDataSlice Slice)`)
     - `DataType` enum (`Unknown`, `Bool`, `Int4`, `UInt4`, `Int8`, `UInt8`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Float8E4M3`, `Float8E5M2`, `Float16`, `BFloat16`, `Float32`, `Float64`, `Complex64`, `Complex128`, `String`, `Resource`, `Variant`, `QuantizedInt8`, `QuantizedUInt8`, `QuantizedInt16`, `QuantizedInt32`)
     - `ParseDiagnostic(DiagnosticSeverity Severity, string Code, string Message, string? Location = null)`
2. **Public Abstractions (`src/DotNodeViewer.Core/Abstractions/`):**
   - `IModelFormatDetector`, `IModelFormatReader`, `ModelProbe` (64-byte header peek + file name + lazy ZIP entry listing, always restoring `stream.Position`), `ModelFormatRegistry` (explicit registration, no reflection), `ModelReadOptions`, `ModelParseException` (with `FileOffset` and `Context`), `GraphIndex` (lazy producer/consumer lookup), `TensorDataLoader` (on-demand slice reading into caller buffer).
3. **Internal I/O Primitives (`src/DotNodeViewer.Core/IO/`):**
   - `RandomAccessSource`: LRU page cache using `ArrayPool<byte>.Shared` 64 KB pages over a seekable `Stream`. Throws `ModelParseException` on non-seekable streams or out-of-bounds reads.
   - `BinaryCursor`: bounds-checked `BinaryPrimitives` span reader.
4. **Test Harness (`tests/DotNodeViewer.Core.Tests/`):**
   - `ModelFixtureLoader` following `.agents/skills/add-test-model/references/manifest-and-snapshot-schema.md`.
   - Source-generated JSON serialization for `models.manifest.json` and `*.expected.json` snapshots.
   - `SnapshotBuilder` and `ModelManifestTests.ParseRegisteredModel_MatchesExpectedTopology` (`[Test]` + `[MethodDataSource]`).
   - `AllocationProbe` helper using `GC.GetAllocatedBytesForCurrentThread()`.
   - `PublicApiEncapsulationTests`: reflection test verifying no public type in `DotNodeViewer.Core` exposes any `*.Formats.*`, `*.IO.*`, or third-party schema type.
   - Initial `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json` (`{"schemaVersion": 1, "models": []}`).
5. **CLI Tool (`tools/DotNodeViewer.Cli/`):**
   - `snapshot <file> --id <model-id>` → prints `.expected.json` to stdout
   - `inspect <file>` → prints human-readable graph summary
   - `detect <file>` → prints detected format ID
6. **ADR:** Record `docs/adr/0006-ir-v1.md` freezing the v1 Unified Graph IR.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
```
All commands must succeed with zero warnings.

## Hand-off & Git Requirements
- Work on branch `stage/S01-core-ir-harness-and-cli` (`git checkout -b stage/S01-core-ir-harness-and-cli`).
- Commit your work incrementally in logical sub-commits prefixed with `S01:` (e.g., `S01: add Unified Graph IR records`, `S01: add abstractions and RandomAccessSource`, `S01: add ModelFixtureLoader, manifest tests, and snapshot CLI`, `S01: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S01** complete.
