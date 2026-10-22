# Stage S03 — TFLite Parser & FlatBuffers Support

## Goal
Implement FlatBuffers reading support (`internal` in `DotNodeViewer.Core`), the TensorFlow Lite (`TFL3`) detector and streaming parser (`DotNodeViewer.Core.Formats.Tflite`), the TensorFlow/TFLite Python generator (`tools/model-gen/tf/`), and committed + cached TFLite fixtures (including per-channel `INT8` quantization and `WHILE` subgraphs).

## Prerequisites
- Stage **S02** complete.
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
1. **FlatBuffers Support (`src/DotNodeViewer.Core/IO/FlatBuffers/`, `internal`):**
   - Use `Google.FlatBuffers` (with `internal` TFLite schema structs) or a bounds-checked `FlatBufferTable` / `FlatBufferVector` reader over `RandomAccessSource`.
   - **Critical rule:** Never materialize `Buffer.data` byte vectors onto the managed heap—extract the vector's absolute stream offset and byte length directly as a `TensorDataSlice`. Every offset calculation must be bounds-checked and throw `ModelParseException` on malformed/corrupted files.
2. **TFLite Format Detector & Reader (`src/DotNodeViewer.Core/Formats/Tflite/`, `internal`):**
   - `TfliteFormatDetector`: checks `TFL3` file identifier at bytes `4..8` and validates root table offset; leaves `stream.Position == 0`.
   - `TfliteFormatReader`:
     - Resolves `operator_codes` (`deprecated_builtin_code` vs `builtin_code` for codes `>= 127`, `version`, and `custom_code`).
     - Maps `subgraphs` (`tensors`, `operators`, `inputs`, `outputs`, `name`).
     - Maps `shape` and `shape_signature` (`-1` → `TensorDimension.Dynamic`, `is_variable`).
     - Classifies tensors with non-empty `buffers[buffer_index]` as `TensorKind.Initializer` (or `Constant`) and attaches `TensorDataSlice` (supporting both inline `Buffer.data` vectors and TFLite `offset`/`size` external/appended buffer fields).
     - Maps `QuantizationParameters` (`scale`, `zero_point`, `quantized_dimension`) → `AffineQuantization`.
     - Extracts `builtin_options` into `NodeAttribute`s for core operators (`CONV_2D`, `DEPTHWISE_CONV_2D`, `FULLY_CONNECTED`, `AVERAGE_POOL_2D`, `MAX_POOL_2D`, `ADD`, `SUB`, `MUL`, `DIV`, `RESHAPE`, `CONCATENATION`, `SOFTMAX`, `STRIDED_SLICE`, `PAD`, `TRANSPOSE`, `MEAN`, `QUANTIZE`, `DEQUANTIZE`, `IF`, `WHILE`, `CALL_ONCE`), including `fused_activation_function` (`RELU`, `RELU6`, `TANH`, etc.).
     - Maps `IF` / `WHILE` / `CALL_ONCE` subgraph indices to nested `AttributeValue.Graph` attributes.
     - Maps `CUSTOM` operators (including NPU delegates such as Ethos-U / XNNPACK / GPU delegates) with `custom_code` and `custom_options` recorded as an `AttributeValue.Bytes(TensorDataSlice)`.
   - `TfliteOperatorCatalog`: maps TFLite builtin operators to `OperatorCategory`.
   - Register TFLite in `ModelFormatRegistry.Default`.
3. **Python Model Generator (`tools/model-gen/tf/`):**
   - Set up `tools/model-gen/tf/requirements.txt` (per `PREREQUISITES.md`) and `gen_tflite.py` generating `.tflite` models + `.truth.json` ground truth.
4. **Base TFLite Test Models:**
   - Committed (`<= 256 KB`):
     - `tiny_cnn_float.tflite` (`CONV_2D` + `DEPTHWISE_CONV_2D` + `AVERAGE_POOL_2D` + `FULLY_CONNECTED` + `SOFTMAX`)
     - `tiny_cnn_int8.tflite` (full integer per-channel quantized `INT8` model with `scale`, `zero_point`, and `quantized_dimension`)
     - `tiny_while.tflite` (model with `WHILE` control-flow cond/body subgraphs)
   - Cached (`> 256 KB` via public URL):
     - `mobilenet_v2_1.0_224_quant.tflite` (`https://raw.githubusercontent.com/google-coral/test_data/master/mobilenet_v2_1.0_224_quant.tflite`)
5. **Tests (`tests/DotNodeViewer.Core.Tests/`):**
   - `Synthetic/TfliteSyntheticTests.cs` (the 4 mandatory tests + malicious FlatBuffer offset resilience).
   - `Formats/TfliteModelTests.cs` (verifying `AffineQuantization`, fused activations, `WHILE` subgraphs, and `*.truth.json` parity).
6. **Documentation (`README.md` & `docs/site/`):**
   - Update root `README.md` supported-formats table with TensorFlow Lite (`TFL3`).
   - Add `docs/site/articles/formats/tflite.md` (and update `toc.yml` / `overview.md`) covering TFLite FlatBuffers zero-copy weight slices, per-tensor/per-channel `INT8` quantization metadata, fused activations, control-flow subgraphs, and custom delegate payloads (e.g., TFLite Micro / Ethos-U).

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```

## Hand-off & Git Requirements
- Work on branch `stage/S03-tflite-parser` (`git checkout -b stage/S03-tflite-parser`).
- Commit your work incrementally in logical sub-commits prefixed with `S03:` (e.g., `S03: add FlatBuffers reading support`, `S03: implement TFLite detector, reader, and quantization mapping`, `S03: add TFLite model-gen, fixtures, and golden snapshots`, `S03: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S03** complete.
