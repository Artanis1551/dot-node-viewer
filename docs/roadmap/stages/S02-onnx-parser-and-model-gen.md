# Stage S02 — ONNX Parser, Protobuf Streaming & Model-Generation Framework

## Goal
Implement the ONNX format detector and streaming parser (`DotNodeViewer.Core.Formats.Onnx`), establish the first Python model-generation toolchain (`tools/model-gen/onnx/`), and register committed + cached ONNX test fixtures in `models.manifest.json`.

## Prerequisites
- Stage **S01** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`PREREQUISITES.md`](../../../PREREQUISITES.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/port-model-format/SKILL.md` (and `references/ir-mapping-checklist.md`)
- `.agents/skills/add-test-model/SKILL.md` (and `references/manifest-and-snapshot-schema.md`)
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/documentation-writer/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`

## Scope & Deliverables
1. **Streaming Protobuf Reader (`src/DotNodeViewer.Core/IO/ProtoReader.cs`, `internal`):**
   - Wraps `Google.Protobuf.CodedInputStream` or a custom span/stream wire reader with nested-message limit stack, varint/fixed decoding, `SkipField()`, and **capture-as-slice** (`CaptureLengthDelimitedSlice()`) so `TensorProto.raw_data` (field 9) is recorded as a `TensorDataSlice(StreamOffset, ByteLength, DataType, TensorDataEncoding.Raw)` and skipped without allocating a `byte[]` or `ByteString`.
2. **ONNX Format Detector & Reader (`src/DotNodeViewer.Core/Formats/Onnx/`, `internal`):**
   - `OnnxFormatDetector`: validates bounded Protobuf tag-walk for `ModelProto` (`ir_version`, `opset_import`, `graph`), using `.onnx` extension only as a secondary tie-breaker; leaves `stream.Position == 0`.
   - `OnnxFormatReader`:
     - Parses `ModelProto` (`ir_version`, `producer_name`, `producer_version`, `domain`, `model_version`, `opset_import`, `metadata_props`, `functions` → extra `Subgraph`s).
     - Parses `GraphProto` (`node`, `initializer`, `sparse_initializer`, `input`, `output`, `value_info`).
     - Classifies initializers as `TensorKind.Initializer` (filtering them out of `GraphInput` ports when ONNX IR v3 lists them in both `input` and `initializer`).
     - Handles `TensorProto` weight payloads: `raw_data` → `Raw` slice; packed `float_data`/`double_data` → `Raw` slice; packed `int32_data`/`int64_data` → `VarintPacked` slice; `data_location == EXTERNAL` + `external_data` → `ExternalTensorDataReference`.
     - Maps `TensorShapeProto`: `dim_value` → `TensorDimension.Fixed`, `dim_param` → `TensorDimension.Symbolic`, empty `Dimension` → `TensorDimension.Dynamic`; missing `shape` → `Shape: null` (unranked).
     - Maps `AttributeProto` including `GRAPH` / `GRAPHS` (`If`, `Loop`, `Scan` subgraphs → `AttributeValue.Graph` / `GraphList`).
   - `OnnxOperatorCatalog`: maps standard `ai.onnx` operators to `OperatorCategory` (`Conv`/`Gemm`/`MatMul` → `Layer`, `Relu`/`Sigmoid`/`Gelu` → `Activation`, `BatchNormalization`/`LayerNormalization` → `Normalization`, `MaxPool`/`AveragePool` → `Pooling`, `QuantizeLinear`/`DequantizeLinear` → `Quantization`, `Reshape`/`Transpose`/`Concat`/`Slice` → `Shape`, `Constant` → `Constant`, `If`/`Loop`/`Scan` → `ControlFlow`).
   - Register ONNX in `ModelFormatRegistry.Default`.
3. **Python Model Generator (`tools/model-gen/onnx/`):**
   - Set up `tools/model-gen/onnx/requirements.txt` (`onnx==1.17.0`, `numpy==1.26.4`, `protobuf==5.29.3`) and `gen_onnx.py` (deterministic seed, emits both `.onnx` and `.truth.json` ground truth).
4. **Base ONNX Test Models (staged via `.agents/skills/add-test-model/scripts/stage-model.sh`):**
   - Committed (`<= 256 KB`):
     - `tiny_mlp.onnx` (`Gemm` + `Relu` + `Add`, float32 weights)
     - `tiny_cnn_dynamic_batch.onnx` (`Conv` + `BatchNormalization` + `Relu` + `GlobalAveragePool`, symbolic `batch` dimension)
     - `if_subgraph.onnx` (`If` node with `then_branch` and `else_branch` subgraphs)
     - `external_data.onnx` (+ `external_data.bin`, exercising `ExternalTensorDataReference`)
   - Cached (`> 256 KB` via public URL in `models.manifest.json`):
     - `squeezenet1.1-7.onnx` and/or `mobilenetv2-7.onnx` from the official ONNX model zoo.
5. **Unit & Regression Tests (`tests/DotNodeViewer.Core.Tests/`):**
   - `Synthetic/OnnxSyntheticTests.cs`: all 4 mandatory tests from `port-model-format` (magic-byte detection, topology/symbolic shapes, lazy weight skipping with allocation check, corrupted input resilience throwing `ModelParseException`).
   - `Formats/OnnxModelTests.cs`: assertions for `If` subgraph attribute, external data reference, and comparing snapshots against `*.truth.json`.
6. **Documentation (`README.md` & `docs/site/`):**
   - Update root `README.md` supported-formats table to include ONNX.
   - Add `docs/site/articles/formats/onnx.md` (and link in `docs/site/articles/formats/overview.md` + `toc.yml`) documenting ONNX IR versions supported, initializer vs. input handling, external data references, control-flow subgraphs, and lazy weight slicing.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```
Verify that parsing `mobilenetv2-7.onnx` allocates `< 10%` of the file's weight byte length on the managed heap.

## Hand-off & Git Requirements
- Work on branch `stage/S02-onnx-parser` (`git checkout -b stage/S02-onnx-parser`).
- Commit your work incrementally in logical sub-commits prefixed with `S02:` (e.g., `S02: add streaming ProtoReader`, `S02: implement ONNX detector, reader, and operator catalog`, `S02: add ONNX model-gen, fixtures, and golden snapshots`, `S02: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S02** complete (including notes on the pattern established for subsequent formats).
