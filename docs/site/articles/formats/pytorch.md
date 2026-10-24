# Reference: PyTorch Format Reader (`.pt` / `.pth`)

This document describes the architectural framing, container structure, and graph extraction mechanics for PyTorch checkpoints and TorchScript archives in `DotNodeViewer.Core`.

---

## 1. Format Overview

PyTorch model files commonly carry `.pt`, `.pth`, or `.bin` extensions and encompass three distinct structural categories:

1. **TorchScript Traced Archives (`torch.jit.trace`):** Serialized computation graphs with static forward control flow, embedded operator inlining, and parameter weights.
2. **TorchScript Scripted Archives (`torch.jit.script`):** Serialized Python TorchScript modules with potential data-dependent control flow (`if`, `while`).
3. **`state_dict` Parameter Checkpoints (`torch.save(model.state_dict())`):** Parameter dictionaries containing learned tensor weights and buffers grouped by module naming hierarchy.

All modern PyTorch archives (generated with PyTorch 1.6+) are structured as PKZIP containers containing pickled metadata, serialized code, and uncompressed raw tensor storage buffers.

---

## 2. Container Framing & Detection

### Detection Strategy (`PyTorchFormatDetector`)

Format detection operates non-destructively through `ModelProbe`:

- **ZIP Container Verification:** Detects `PK\x03\x04` at offset 0 and scans central directory entry paths:
  - Requires the presence of `*/data.pkl` or `data.pkl`.
  - Requires supporting PyTorch markers: `*/version`, `*/byteorder`, `*/.format_version`, `*/data/`, or `*/code/`.
  - Excludes `.pt2` (`archive_format` or `serialized_exported_program.json`, handled in Stage S09).
  - Excludes Keras v3 (`config.json`, handled in Stage S10).
- **Raw Pickle Checkpoints:** Probes for Python pickle protocol bytes (`0x80 0x02..0x05`) and inspects the opening byte window for torch identifiers (`torch`, `_rebuild_tensor`, `_rebuild_parameter`).

### ZIP Directory Scanner (`ZipDirectory`)

The internal `ZipDirectory` inspects End-of-Central-Directory (EOCD), ZIP64 EOCD, and Central Directory headers over `RandomAccessSource`:
- Computes exact absolute file offsets (`DataOffset`) for uncompressed `Stored` entries (`<prefix>/data/<key>`) without decompressing or copying payloads.
- Bounded streaming decompression (supporting `Deflate` method 8) for metadata files (`data.pkl`, `constants.pkl`, and `code/**/*.py`).

---

## 3. Safe Pickle Virtual Machine (`PickleMachine`)

Python pickle streams can theoretically execute arbitrary code via `GLOBAL` and `REDUCE` opcodes. `DotNodeViewer.Core` completely eliminates this security risk by implementing an internal, sandboxed virtual machine:

- **Zero Reflection & Zero Process Execution:** `GLOBAL` and `STACK_GLOBAL` opcodes emit symbolic `PyGlobal(Module, Name)` records. No arbitrary methods or constructors are ever invoked.
- **Protocol Support:** Full execution support for Pickle protocols 2 through 5.
- **Whitelisted Reducers:** Explicit deterministic handling for PyTorch tensor reconstruction:
  - `torch._utils._rebuild_tensor_v2` and `_rebuild_tensor`: constructs `PyTensor` with `TensorDataSlice`.
  - `torch._utils._rebuild_parameter`: extracts underlying tensor.
  - `torch._utils._rebuild_qtensor`: quantized tensor reconstruction.
  - `collections.OrderedDict`: preserves key insertion order.
  - `torch.Size` and `torch.device`: formats shapes and device strings.
- **Persistent Storage Resolution (`BINPERSID`):** Maps `('storage', storageCls, key, device, numel)` storage tuples to corresponding `<prefix>/data/<key>` entries in `ZipDirectory`, binding exact stream offsets directly to `TensorDataSlice`.

---

## 4. TorchScript AST Mini-Parser (`TorchScriptParser`)

When an archive contains `code/__torch__/**/*.py`, `TorchScriptParser` parses the generated Python AST:

- **Method Signature:** Extracts graph input ports from `def forward(self, ...)` parameters.
- **Submodule Inlining:** Maps `(self.conv1).forward(x)` or `(conv1).forward(x)` calls to `GraphNode`s representing leaf modules (`Conv2d`, `BatchNorm2d`, `Linear`, `ReLU`, `MaxPool2d`).
- **Initializer Binding:** Attaches learned weights (`weight`, `bias`, `running_mean`, `running_var`) directly to the inlined node as `NodePort` initializers with exact `TensorDataSlice` references.
- **Control Flow Fallback:** If unsupported scripted control flow (`if`, `while`, `for`) is detected, the parser records a warning `ParseDiagnostic(Code: "DNV_TORCH_CONTROL_FLOW")` and gracefully falls back to building the module hierarchy graph from `data.pkl`.

---

## 5. `state_dict` Inspection

When inspecting parameter checkpoints:

- **Hierarchical Grouping:** Keys such as `layer1.0.conv1.weight` and `layer1.0.conv1.bias` are grouped into parent module nodes (`layer1.0.conv1`).
- **No Fabricated Activations:** Graph inputs and outputs are left empty to reflect that parameter checkpoints contain static parameters rather than an executable computation graph.
- **Diagnostic:** Emits an informational `ParseDiagnostic(Code: "DNV_TORCH_STATE_DICT")` explaining that the model contains weights without a forward computation graph.

---

## 6. Type Mapping

| PyTorch Storage / DataType | Core IR `DataType` | Element Size (Bytes) |
|---|---|---|
| `FloatStorage` / `float32` | `DataType.Float32` | 4 |
| `DoubleStorage` / `float64` | `DataType.Float64` | 8 |
| `HalfStorage` / `float16` | `DataType.Float16` | 2 |
| `BFloat16Storage` / `bfloat16` | `DataType.BFloat16` | 2 |
| `LongStorage` / `int64` | `DataType.Int64` | 8 |
| `IntStorage` / `int32` | `DataType.Int32` | 4 |
| `ShortStorage` / `int16` | `DataType.Int16` | 2 |
| `CharStorage` / `int8` | `DataType.Int8` | 1 |
| `ByteStorage` / `uint8` | `DataType.UInt8` | 1 |
| `BoolStorage` / `bool` | `DataType.Bool` | 1 |
| `ComplexFloatStorage` | `DataType.Complex64` | 8 |
| `ComplexDoubleStorage` | `DataType.Complex128` | 16 |

---

<a id="pytorch-2-exportedprogram-pt2"></a>
## 7. PyTorch 2 `ExportedProgram` (`.pt2`)

Modern PyTorch 2 uses `torch.export.export` and `torch.export.save` to serialize self-contained computation graphs known as `ExportedProgram`s into `.pt2` archives. These play a foundational role in modern edge deployment pipelines (e.g., exporting PyTorch models to ExecuTorch `.pte` or TFLite).

### Archive Layout

A `.pt2` export is a PKZIP container structured as follows:

```text
model.pt2
├── archive_format                     # Contains ASCII "pt2" or "exported_program"
├── archive_version                    # Archive schema version
├── byteorder                          # Target byteorder ("little")
├── models/
│   └── model.json                     # Serialized ExportedProgram schema (FX graph, signatures, constraints)
└── data/
    ├── weights/
    │   ├── model_weights_config.json  # Map from parameter names to raw weight files
    │   ├── weight_0                   # Raw uncompressed little-endian tensor data (ZipCompressionMethod.Stored)
    │   └── weight_1
    └── constants/
        └── model_constants_config.json
```

### Detection Strategy (`PyTorchPt2FormatDetector`)

- Probes `ModelProbe.ZipEntries` for the marker entry `archive_format` or `models/model.json` / `serialized_exported_program.json`.
- Disambiguates modern `.pt2` archives from legacy TorchScript `.pt` archives (which contain `data.pkl`).

### Graph Extraction & Signature Separation (`PyTorchPt2FormatReader`)

1. **Input Signature Analysis (`input_specs`):**
   In the raw FX graph, parameters, buffers, lifted constants, and user inputs are all represented as placeholder nodes. `PyTorchPt2FormatReader` parses `graph_module.signature`:
   - `parameter`, `buffer`, and `lifted_tensor_constant` specs are classified as `TensorKind.Initializer` or `TensorKind.Constant`.
   - `user_input` specs are classified as `TensorKind.GraphInput` and exposed on the primary `Subgraph.Inputs` boundary.
   - `user_output` specs are classified as `TensorKind.GraphOutput` and exposed on `Subgraph.Outputs`.
2. **Dynamic Shapes & Symbolic Constraints (`SymInt`):**
   Dimensions can be either statically known integers (`as_int`) or symbolic dimension expressions (`as_sym_int` / `as_expr`, such as `s0` or `batch`).
   - Symbolic dimensions are mapped to `new TensorDimension.Symbolic(symName)`.
   - Root `range_constraints` (e.g., `s0 in [1, 32]`) are recorded in `ModelGraph.Metadata["range_constraints.<sym>"] = "[min, max]"`.
3. **Lazy Weight Slices:**
   Weights are referenced via `data/weights/model_weights_config.json` and stored uncompressed (`CompressionMethod.Stored`) in the ZIP.
   - `PyTorchPt2FormatReader` extracts the exact stream offset and byte length from `ZipEntry.DataOffset` and `ZipEntry.UncompressedSize`.
   - Recorded as `TensorDataSlice(offset, byteLength, dataType, TensorDataEncoding.Raw)` with zero managed heap allocations.
4. **Operator Normalization:**
   ATen operators (e.g., `torch.ops.aten.conv2d.default`, `torch.ops.aten.relu.default`, `torch.ops.aten.linear.default`) are mapped to semantic categories (`Layer`, `Activation`, `Shape`, `Tensor`) via `PyTorchOperatorCatalog`.

---

## 8. Cross-Format Comparison: PyTorch `.pt2` vs ExecuTorch `.pte` & TFLite

When deploying neural networks from PyTorch 2 to edge runtimes, `dot-node-viewer` enables inspecting each stage of the export pipeline side-by-side:

| Aspect | PyTorch `.pt2` (`ExportedProgram`) | ExecuTorch `.pte` | TensorFlow Lite `.tflite` |
|---|---|---|---|
| **Format ID** | `pytorch-pt2` | `executorch` | `tflite` |
| **Container** | PKZIP (`models/model.json` + `data/weights/`) | FlatBuffers (`ET12` + segmented data) | FlatBuffers (`TFL3` + `Buffer` table) |
| **Operator Domain** | Core ATen operators (`torch.ops.aten.*`) | Portable ATen kernels + Backend Delegates | Built-in TFLite operators + Custom Delegates |
| **Weights Backing** | Uncompressed stored ZIP entries | Segmented FlatBuffers file offsets | FlatBuffers buffer table offsets |
| **Dynamic Shapes** | Exact `SymInt` symbolic symbols & bounds | Upper-bound dynamic shapes (`DynamicBound`) | Dynamic dims (`-1` or signature defs) |
| **Quantization** | High-level `torch.ao.quantization` or unquantized | Lowered quantized ATen ops | Per-tensor & per-channel `AffineQuantization` |


