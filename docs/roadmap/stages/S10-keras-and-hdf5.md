# Stage S10 — Keras Parser: `.keras` v3 Archive & Legacy `.h5` (HDF5)

## Goal
Implement the Keras format detector and streaming parser (`DotNodeViewer.Core.Formats.Keras`) supporting both modern Keras 3 `.keras` ZIP archives (`config.json` + `metadata.json` + `model.weights.h5`) and legacy HDF5 `.h5` files (including `Sequential`, `Functional`, nested models, and weights-only `.weights.h5` files) with lazy `TensorDataSlice` offsets for contiguous HDF5 datasets.

## Prerequisites
- Stage **S09** complete (reuses `ZipDirectory` from S08 and `tools/model-gen/tf/.venv` from S03).
- Read [`AGENTS.md`](../../../AGENTS.md), [`PREREQUISITES.md`](../../../PREREQUISITES.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/port-model-format/SKILL.md` (and `references/ir-mapping-checklist.md`)
- `.agents/skills/add-test-model/SKILL.md` (and `references/manifest-and-snapshot-schema.md`)
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`

## Scope & Deliverables
1. **HDF5 Inspection & Dataset Slice Extraction (`internal` in `DotNodeViewer.Core`):**
   - Either wrap `PureHDF` (pure managed C#, verifying `PublishAot=true` cleanliness) or implement a minimal read-only `Core/IO/Hdf5/Hdf5File` reader over `RandomAccessSource` ( supporting superblock v0/v2/v3, object headers v1/v2, symbol-table & dense groups, fixed/variable-length string attributes for `model_config` / `keras_version` / `backend`, and dataset dataspace/datatype/data-layout messages).
   - For **contiguous** datasets (`DataLayoutClass == Contiguous`), record the absolute stream offset (rebased by the `.keras` ZIP entry's `DataOffset` when inside a `Stored` `model.weights.h5` entry) and byte length as a `TensorDataSlice` without reading the weight array.
   - For **chunked/compressed** datasets (or deflated `model.weights.h5` entries), extract the `TensorShape` and `DataType` and emit a `ParseDiagnostic` noting that raw weight slicing requires contiguous storage.
2. **Keras Detector & Reader (`src/DotNodeViewer.Core/Formats/Keras/`, `internal`):**
   - `KerasFormatDetector`: detects `.keras` ZIP archives containing `config.json` + `metadata.json` (or `model.weights.h5`), and HDF5 files (`\x89HDF\r\n\x1a\n` signature) containing `model_config` / `layer_names` / `model_weights` or `_layer_checkpoint_dependencies`.
   - `KerasFormatReader`:
     - Parses `config.json` (Keras 3) or HDF5 `model_config` JSON attribute (Keras 2 / tf.keras) via source-generated `JsonSerializerContext` / `Utf8JsonReader`.
     - **Sequential Models:** wires layers in order (`InputLayer` → `Layer[0]` → `Layer[1]` → …), inferring or reading `batch_input_shape` / `build_input_shape` (`None` → `TensorDimension.Dynamic`).
     - **Functional Models:** supports both Keras 3 `inbound_nodes` (`args` with `__keras_tensor__` / `keras_history: [layer_name, node_index, tensor_index]`) and legacy Keras 2 nested-list `inbound_nodes`.
     - **Nested Sub-Models:** maps nested `Functional` / `Sequential` layers to `AttributeValue.Graph(Subgraph)`.
     - **Layer Attributes & Weights:** maps layer `config` properties (`filters`, `kernel_size`, `strides`, `padding`, `activation`, `units`, `rate`, etc.) to `NodeAttribute`s, and binds HDF5 weight datasets (`kernel`/`weights`, `bias`, `gamma`, `beta`, `moving_mean`, `moving_variance`) as `TensorKind.Initializer` ports on the corresponding `GraphNode`.
     - **Weights-Only `.h5` Fallback:** if an HDF5 file has weight groups without `model_config`, constructs a layer-parameter summary graph and emits a `ParseDiagnostic`.
   - `KerasOperatorCatalog`: maps Keras layer classes (`Conv2D`, `DepthwiseConv2D`, `Dense`, `BatchNormalization`, `LayerNormalization`, `ReLU`, `Activation`, `MaxPooling2D`, `GlobalAveragePooling2D`, `Add`, `Concatenate`, `Reshape`, `Dropout`, `LSTM`, `MultiHeadAttention`) to `OperatorCategory`.
   - Register Keras in `ModelFormatRegistry.Default`.
3. **Python Generator (`tools/model-gen/tf/gen_keras.py`) & Base Models:**
   - Committed (`<= 256 KB`):
     - `tiny_sequential.keras` (Keras 3 `.keras` Sequential model)
     - `tiny_functional_residual.keras` (Keras 3 `.keras` Functional model with a residual `Add` skip connection)
     - `tiny_cnn_legacy.h5` (legacy HDF5 `.h5` model containing `model_config` + `model_weights`)
     - `tiny_weights_only.weights.h5` (weights-only HDF5 file exercising the fallback + diagnostic)
4. **Tests (`tests/DotNodeViewer.Core.Tests/`):**
   - `Synthetic/KerasSyntheticTests.cs` (the 4 mandatory tests).
   - `Formats/KerasModelTests.cs` (Sequential, Functional skip connections, `.h5` contiguous dataset slices, weights-only diagnostic, and `.truth.json` parity).

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
```

## Recommended Checkpoint After S10
All 5 target model ecosystems (ONNX, TFLite, ExecuTorch, PyTorch, Keras) are now complete in `DotNodeViewer.Core`. Recommend to the user that they can optionally run `.agents/skills/improve-codebase-architecture/SKILL.md` on `DotNodeViewer.Core`.

## Hand-off & Git Requirements
- Work on branch `stage/S10-keras-and-hdf5` (`git checkout -b stage/S10-keras-and-hdf5`).
- Commit your work incrementally in logical sub-commits prefixed with `S10:` (e.g., `S10: add HDF5 inspection and contiguous dataset slice extraction`, `S10: implement Keras .keras and .h5 reader`, `S10: add Keras fixtures and golden snapshots`, `S10: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S10** complete.
