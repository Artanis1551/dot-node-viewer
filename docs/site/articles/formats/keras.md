# Reference: Keras Format Reader (`.keras` / `.h5`)

This document describes the architectural framing, container structure, and graph extraction mechanics for modern Keras 3 `.keras` archives and legacy HDF5 `.h5` models in `DotNodeViewer.Core`.

---

## 1. Format Overview

Keras models encompass two primary serialization formats:

1. **Modern Keras 3 (`.keras` ZIP Archive):** Introduced in Keras 3, `.keras` files are standard PKZIP containers containing:
   - `config.json`: The complete computation graph, layer configurations, and tensor connectivity (`inbound_nodes`).
   - `metadata.json`: Keras version, date saved, and serialization parameters.
   - `model.weights.h5`: Uncompressed (`Stored`) HDF5 container storing learned layer weights.
2. **Legacy Keras 2 / tf.keras (`.h5` / HDF5 Container):** Self-contained HDF5 files (`\x89HDF\r\n\x1a\n`) carrying:
   - Root attribute `model_config`: Serialized JSON string describing layer configurations.
   - Root attribute `keras_version` and `backend` (`tensorflow`, `jax`, `torch`).
   - Group `model_weights`: Hierarchical groups per layer containing weight datasets (`kernel`, `bias`, etc.).
3. **Weights-Only HDF5 (`.weights.h5`):** HDF5 checkpoint files without `model_config`, generated via `model.save_weights()`.

---

## 2. Container Framing & Detection

### Detection Strategy (`KerasFormatDetector`)

- **ZIP Container Verification:**
  - Probes `ModelProbe.ZipEntries` for `config.json`.
  - Requires the presence of `metadata.json` or `model.weights.h5`, or file extension `.keras`.
  - Disambiguates modern `.keras` archives from PyTorch `.pt` archives (which contain `data.pkl`) and ExecuTorch archives.
- **HDF5 Header Verification:**
  - Detects `\x89HDF\r\n\x1a\n` signature.
  - Matches file extensions (`.h5`, `.hdf5`, `.keras`) or probes for Keras group markers (`model_config`, `model_weights`, `keras_version`, `layer_names`, `_layer_checkpoint_dependencies`).

---

## 3. Pure C# HDF5 Parser (`Hdf5File`)

`DotNodeViewer.Core` includes a pure managed C# reader (`DotNodeViewer.Core.IO.Hdf5.Hdf5File`) over `RandomAccessSource`:

- **Zero Third-Party Dependencies:** Fully NativeAOT- and trimming-clean with 0 runtime reflection.
- **Superblock Support:** Superblock versions 0, 1, 2, and 3.
- **Object Header Parsing:** Object Header v1 (with 8-byte alignment and `0x0010` continuation blocks) and v2 (`OHDR`).
- **Group Traversals:** Symbol Table messages (`0x0011`) resolving B-trees, Local Heaps (`HEAP`), and Symbol Table Nodes (`SNOD`).
- **Global Heap Resolution (`GCOL`):** Resolves variable-length sequence references (`hvl_t`), decoding UTF-8 strings for `model_config`, `keras_version`, and `backend`.
- **Contiguous Dataset Weight Slicing:**
  - For contiguous datasets (`DataLayoutClass == Contiguous`), extracts the exact file offset and byte length.
  - Rebased by the `.keras` ZIP entry's `DataOffset` when inside a `Stored` `model.weights.h5` entry, allowing direct on-demand streaming reads via `TensorDataLoader` with zero managed heap allocation.

---

## 4. Graph Extraction (`KerasFormatReader`)

### Model Architectures

1. **Sequential Models (`Sequential`):**
   - Chains layers sequentially from `InputLayer` (or the first layer's `batch_shape`) to the final layer.
   - Wires the previous layer's output tensor as the input port of the succeeding layer.
2. **Functional Models (`Functional`):**
   - In Keras 3: Parses `inbound_nodes` objects inspecting `args` and `keras_history: [layer_name, node_index, tensor_index]`.
   - In Keras 2: Parses legacy nested-list `inbound_nodes` arrays.
   - Accurately captures branching, merging, and residual skip connections (e.g., `Add`, `Concatenate`).
3. **Nested Sub-Models:**
   - Detects nested `Sequential` or `Functional` layers and extracts them as child `Subgraph`s attached to `NodeAttribute("subgraph", ...)`.

### Weights Binding

- Matches datasets in `model.weights.h5` or `model_weights` to graph layers:
  - In Keras 3: Matches by the group's `name` attribute (e.g., `layers/dense/vars` with `name="dense_1"`).
  - In Keras 2: Matches by hierarchical group paths (e.g., `model_weights/dense_1/...`).
- Binds datasets as `TensorKind.Initializer` ports (`kernel`, `bias`, `gamma`, `beta`, `moving_mean`, `moving_variance`) with lazy `TensorDataSlice` references.

### Weights-Only Checkpoint Fallback

When an HDF5 file contains weight groups without `model_config`:
- Constructs a summary graph grouping datasets into layer nodes.
- Emits an informational `ParseDiagnostic(Code: "DNV_KERAS_WEIGHTS_ONLY")` explaining that the model contains weights without a forward computation graph.

---

## 5. Type Mapping

| Keras Dtype | Core IR `DataType` | Element Size (Bytes) |
|---|---|---|
| `float32` | `DataType.Float32` | 4 |
| `float16` / `half` | `DataType.Float16` | 2 |
| `bfloat16` | `DataType.BFloat16` | 2 |
| `float64` / `double` | `DataType.Float64` | 8 |
| `int32` | `DataType.Int32` | 4 |
| `int64` | `DataType.Int64` | 8 |
| `int16` | `DataType.Int16` | 2 |
| `int8` | `DataType.Int8` | 1 |
| `uint8` | `DataType.UInt8` | 1 |
| `bool` | `DataType.Bool` | 1 |

