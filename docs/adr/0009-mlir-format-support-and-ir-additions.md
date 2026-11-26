# ADR 0009: MLIR Format Support & Frozen IR Additions

- **Status:** Accepted
- **Date:** 2026-11-26
- **Stage:** W13

---

## Context

**Wish W13** introduces first-class support in `DotNodeViewer.Core` for detecting and parsing **MLIR** (Multi-Level Intermediate Representation) files—both **MLIR Bytecode (`.mlirbc`)** and **Textual MLIR (`.mlir`)**—across machine learning compiler dialects (`stablehlo.*`, `vhlo.*`, `mhlo.*`, `chlo.*`, `xla.*`, `torch.*`, `torch.quantized_decomposed.*`, `torch.torchao.*`, `tosa.*`, `linalg.*`, `iree_input.*`, `flow.*`, `hal.*`, `tt.*`, `ttg.*`, `ttng.*`, `arith.*`, `math.*`, `tensor.*`, `memref.*`, `scf.*`, `cf.*`, `func.*`, `builtin.*`).

**ADR 0006** froze the v1 Unified Graph IR (`DotNodeViewer.Core.Ir`), including `ModelFormatId` and `DataType`, and requires a new ADR whenever `DotNodeViewer.Core.Ir` is extended or new structural mapping conventions are introduced.

In addition, MLIR compiler pipelines introduce three architectural questions that must be resolved cleanly:
1. Adding the canonical MLIR format identifier (`ModelFormatId.Mlir`).
2. Handling emerging compiler tensor element types (`float8e5m3fnu`, `mxi8`, `complex<float16>`) without destabilizing the v1 `DataType` enum and downstream tensor decoders.
3. Mapping nested MLIR regions, multi-block control flow, large dense resources, and extreme/dynamic compiler tensor shapes into the Unified Graph IR safely.

---

## Decision

### 1. Frozen IR Addition: `ModelFormatId.Mlir`
- Add `public static ModelFormatId Mlir => new("mlir");` to `DotNodeViewer.Core.Ir.ModelFormatId` (`src/DotNodeViewer.Core/Ir/ModelFormatInfo.cs`).
- Emit `ModelFormatInfo.DisplayName = "MLIR"` with version `"Textual"` (display string `"MLIR Textual"`) for textual `.mlir` streams and `"Bytecode v<N>"` (display string `"MLIR Bytecode v<N>"`) for binary `.mlirbc` streams.

### 2. Deferral of Emerging `DataType` Enum Extensions
- The v1 `DotNodeViewer.Core.Ir.DataType` enum remains unchanged in this stage.
- Emerging MLIR / Triton / TorchAO element types not present in v1 `DataType`—specifically `float8e5m3fnu` (`f8E5M3FNU` / `f8E5M3FNUZ`), `mxi8` (`f8E8M0FNU` / OCP microscaling 8-bit types), `f4E2M1FN`, and `complex<float16>` (`complex<f16>`)—map gracefully to `DataType.Unknown` and emit a non-fatal `ParseDiagnostic`:
  - `Severity = DiagnosticSeverity.Warning`
  - `Code = "DNV_MLIR_UNSUPPORTED_DTYPE"`
  - `Message = "Unsupported MLIR element type '<raw>' mapped to DataType.Unknown."`
  - `Location = <tensor or op location>`
- Introducing first-class `DataType` enum values and corresponding numeric decoders in `TensorInspectorViewModel` is deferred to a dedicated IR v1.x DataType ADR.

### 3. Nested Regions, Functions, and Multi-Block Control Flow
- **Module Functions:** Top-level functions (`func.func`, `tt.func`, `llvm.func`, `util.func`) map to `Subgraph` entries in `ModelGraph.Subgraphs`. When multiple functions are present, `@main` or `@forward` is placed at `Subgraphs[0]` (`PrimarySubgraph`) so visualizers open the primary entry point by default.
- **Nested Regions:** Operations carrying nested regions (`scf.if`, `scf.for`, `scf.while`, `stablehlo.while`, `stablehlo.reduce`, `linalg.generic`, `tt.reduce`, etc.) map each child region to a nested `Subgraph` attached via `NodeAttribute` with `AttributeValue.Graph(childSubgraph)` (or `AttributeValue.GraphList` for multi-region ops, matching ONNX `If`/`Loop` conventions).
- **Multi-Block Control Flow:** Block transitions (`cf.br`, `cf.cond_br`) map to `OperatorCategory.ControlFlow` nodes carrying a `successors` (`AttributeValue.StringList`) attribute listing target block labels (`^bbN`), while SSA values and block arguments are wired as `TensorEdge`s.

### 4. Defensive Shape Arithmetic & Lazy Bytecode Slicing
- **128-Bit Element-Count & Byte-Length Overflow Guard:** Element counts and expected buffer byte lengths are computed with `UInt128` checked multiplication. Any shape whose product or byte length overflows `UInt128` or `long.MaxValue` never wraps around to negative values; instead, it emits a `ParseDiagnostic(DiagnosticSeverity.Warning, "DNV_MLIR_SHAPE_OVERFLOW", ...)` (or throws `ModelParseException` when attempting an out-of-bounds stream slice).
- **Single-`?` Dynamic Dimension Inference:** When a constant/dense resource tensor has a concrete payload of known `byteLength` and known element bit-width, and its `TensorShape` contains **exactly one** `TensorDimension.Dynamic` (`?`) and zero `Symbolic` dimensions, the parser resolves that `?` to `Fixed(inferred)` if `totalElements` is an exact integer multiple of the static dimensions' product; otherwise it remains `TensorDimension.Dynamic`.
- **Zero-Allocation Bytecode Resource & Dense Slices:** `MlirBytecodeReader` skips `kResource` blobs and `kDenseIntOrFPElementsAttr` raw payloads during `ReadAsync`, recording lightweight `TensorDataSlice(StreamOffset, ByteLength, DataType, TensorDataEncoding.Raw)` descriptors (per **ADR 0003**).

---

## Consequences

### Positive
- Preserves full backward compatibility with all v1 IR consumers (`DotNodeViewer.Layout`, `DotNodeViewer.App`, `DotNodeViewer.Cli`, and `SnapshotBuilder`) while adding first-class MLIR detection and parsing.
- Protects WebAssembly and NativeAOT runtimes against multi-megabyte resource allocations and integer-overflow shape bombs.
- Keeps the path clean for a future IR v1.x `DataType` ADR without breaking existing `switch` statements or `TensorDataLoader` consumers today.

### Tradeoffs & Mitigations
- Emerging types (`float8e5m3fnu`, `mxi8`, `complex<float16>`) render as `Unknown` in the UI until a future `DataType` ADR lands, mitigated by preserving the raw type signature and emitting `DNV_MLIR_UNSUPPORTED_DTYPE` diagnostics.
- Arbitrary third-party dialect custom assembly printers outside the supported dialects fall back gracefully to generic attribute/operand parsing or emit non-fatal diagnostics rather than failing the entire module.
