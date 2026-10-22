# Reference: ONNX Model Format (`DotNodeViewer.Core`)

`DotNodeViewer.Core` includes a built-in, zero-allocation streaming parser for **Open Neural Network Exchange (ONNX)** models (`ModelFormatId.Onnx`, registered in `ModelFormatRegistry.Default`).

---

## 1. Format Identification & Probing

| Property | Value |
|---|---|
| **Format ID** | `onnx` (`ModelFormatId.Onnx`) |
| **Display Name** | `ONNX` (formatted as `ONNX IR v<ir_version>`, e.g., `ONNX IR v3`, `ONNX IR v9`) |
| **File Extensions** | `.onnx` (used only as a secondary tie-breaker) |
| **Wire Framing** | Protocol Buffers (`onnx.ModelProto`) |
| **Supported IR Versions** | ONNX IR `v1` through `v10+` (`opset_import` versions `1`–`22+`) |

Unlike container formats with a fixed 4-byte ASCII magic header, ONNX files are raw Protocol Buffers `ModelProto` streams. `OnnxFormatDetector` performs a bounded, non-destructive top-level Protobuf tag walk over `ModelProbe`:

1. Rejects ZIP archives (`PK\x03\x04`) and known non-ONNX signatures (`\x89HDF`, `TFL3`, `ET12`, Python pickle `0x80`).
2. Validates top-level `ModelProto` wire tags (`ir_version = 1`, `producer_name = 2`, `producer_version = 3`, `domain = 4`, `model_version = 5`, `doc_string = 6`, `graph = 7`, `opset_import = 8`, `metadata_props = 14`, `training_info = 20`, `functions = 25`) and verifies that all length-delimited field bounds lie within `probe.StreamLength`.
3. Confirms the presence of a non-empty `GraphProto` (field `7`) whose inner wire tag matches a valid `GraphProto` field (`node`, `name`, `initializer`, `input`, `output`, `value_info`, or `sparse_initializer`), alongside `ir_version`, `opset_import`, or a `.onnx` extension tie-breaker.
4. Restores `Stream.Position` before returning.

---

## 2. Unified Graph IR Mapping

### 2.1 `ModelProto` & Function Subgraphs

- **`Format`:** `new ModelFormatInfo(ModelFormatId.Onnx, "ONNX", $"IR v{ir_version}")`.
- **`Producer`:** Combines `producer_name` and `producer_version` (for example, `"pytorch 2.6.0"`).
- **`Metadata`:** Populated from `ModelProto.metadata_props` (`StringStringEntryProto`), supplemented by `"ir_version"`, `"domain"`, `"model_version"`, `"doc_string"`, and `"opset_import"` (for example, `"ai.onnx:18"`).
- **`Subgraphs`:**
  - `Subgraphs[0]` (`PrimarySubgraph`) is always the root `ModelProto.graph` (`GraphProto`).
  - Any model-local `FunctionProto` definitions (`ModelProto.functions`, field `25`) are appended as `Subgraphs[1..N]` with domain-qualified names (such as `"custom.domain.MyGelu"`).

### 2.2 Initializers vs. Graph Inputs (ONNX IR v3 Compatibility)

In ONNX IR `v3` and earlier exporters, static learned weights (`GraphProto.initializer`) were also duplicated inside `GraphProto.input`. `OnnxFormatReader` handles this automatically:

1. All entries in `GraphProto.initializer` (field `5`) and `GraphProto.sparse_initializer` (field `15`) are registered first with `TensorKind.Initializer`.
2. When building `Subgraph.Inputs` from `GraphProto.input` (field `11`), any entry whose name matches a registered initializer is filtered out of `Subgraph.Inputs` so only true runtime activations are exposed as `GraphPort` inputs.

### 2.3 Tensor Shapes & Symbolic Dimensions

`TypeProto.Tensor` (`elem_type` and `shape`) maps to `TensorDescriptor`:

- **Missing `shape` message:** Mapped to `Shape: null` (unranked tensor).
- **Empty `TensorShapeProto` (`0` dimensions):** Mapped to `TensorShape.Scalar` (`Rank == 0`, displayed as `[]`).
- **`TensorShapeProto.Dimension`:**
  - Non-empty `dim_param` (field `2`) → `new TensorDimension.Symbolic(dimParam)` (for example, `"batch"` or `"seq_len"`).
  - Non-negative `dim_value` (field `1`) → `new TensorDimension.Fixed(dimValue)` (for example, `224`).
  - Empty `Dimension` message (neither `dim_value` nor `dim_param` set, or negative wildcard) → `TensorDimension.Dynamic.Instance` (`"?"`).

### 2.4 Lazy Weight Slicing & External Data References

During `OnnxFormatReader.ReadAsync`, raw weight arrays in `TensorProto` are never copied onto the managed heap:

| `TensorProto` Storage | Unified Graph IR Representation |
|---|---|
| `raw_data` (field `9`, `bytes`) | `TensorDataSlice(StreamOffset, ByteLength, DataType, TensorDataEncoding.Raw)` |
| Packed `float_data` (field `4`) / `double_data` (field `10`) | `TensorDataSlice(StreamOffset, ByteLength, DataType, TensorDataEncoding.Raw)` |
| Packed `int32_data` (field `5`) / `int64_data` (field `7`) / `uint64_data` (field `11`) | `TensorDataSlice(StreamOffset, ByteLength, DataType, TensorDataEncoding.VarintPacked)` |
| `data_location == EXTERNAL` + `external_data` (field `13`) | `ExternalTensorDataReference(RelativePath, Offset, Length, DataType)` |

> [!IMPORTANT]
> External weight shard files (such as `external_data.bin`) are **never** opened automatically by `OnnxFormatReader`. Instead, their relative path, byte offset, and byte length are recorded in `ExternalTensorDataReference` so callers remain safe in sandboxed and WebAssembly environments.

### 2.5 Control-Flow Subgraphs (`If`, `Loop`, `Scan`)

ONNX control-flow operators embed nested `GraphProto` messages inside `AttributeProto` (`AttributeType.GRAPH` on field `6` or `AttributeType.GRAPHS` on field `11`):

- `If` nodes expose `then_branch` and `else_branch` as `NodeAttribute` values of type `AttributeValue.Graph(Subgraph)`.
- `Loop` and `Scan` nodes expose their `body` subgraph as `AttributeValue.Graph(Subgraph)`.
- Nested subgraph recursion is bounded by `ModelReadOptions.MaxSubgraphDepth` (default `16`).

