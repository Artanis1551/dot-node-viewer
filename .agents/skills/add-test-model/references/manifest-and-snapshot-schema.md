# Test Model Manifest & Snapshot Schema Reference

This reference documents the file schemas and runtime resolution patterns used across the test suite for neural network models in `dot-node-viewer`.

---

## 1. `Fixtures/models.manifest.json` Schema

All committed (<= 256 KB) and cached (> 256 KB) test models are registered in `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json`.

```json
{
  "schemaVersion": 1,
  "models": [
    {
      "id": "keras-tiny_sequential",
      "format": "keras",
      "fileName": "tiny_sequential.keras",
      "tier": "committed",
      "relativePath": "Fixtures/keras/tiny_sequential.keras",
      "sourceUrl": null,
      "sha256": "cfcf13dacc52ed5e6783c4ccbffdb1538f63f867b06dff2b09f9de5e68505f42",
      "byteLength": 15067,
      "expectedSnapshot": "Fixtures/keras/tiny_sequential.expected.json"
    },
    {
      "id": "tflite-mobilenet_v2_1.0_224_quant",
      "format": "tflite",
      "fileName": "mobilenet_v2_1.0_224_quant.tflite",
      "tier": "cached",
      "relativePath": ".model-cache/tflite/mobilenet_v2_1.0_224_quant.tflite",
      "sourceUrl": "https://raw.githubusercontent.com/google-coral/test_data/master/mobilenet_v2_1.0_224_quant.tflite",
      "sha256": "7aad0c74c5e3c06e5eb3c827e13304fdd68a83da6087d92ee169c24ff9fd4776",
      "byteLength": 3610798,
      "expectedSnapshot": "Fixtures/tflite/mobilenet_v2_1.0_224_quant.expected.json"
    }
  ]
}
```

### Manifest Fields

| Field | Type | Description |
|---|---|---|
| `schemaVersion` | `int` | Manifest schema version number (currently `1`). |
| `id` | `string` | Unique model identifier. Structured as `<folder_prefix>-<stem>` (e.g. `onnx-tiny_mlp`, `pytorch_pt2-tiny_cnn_export`, `tflite-mobilenet_v2_1.0_224_quant`). |
| `format` | `string` | Canonical format identifier matching `ModelFormatId.Value` (`onnx`, `tflite`, `executorch`, `pytorch`, `pytorch-pt2`, `keras`). |
| `fileName` | `string` | Binary file name including extensions (e.g., `tiny_weights_only.weights.h5`, `mobilenetv2-7.onnx`). |
| `tier` | `string` | `"committed"` for files <= 256 KB tracked in git, or `"cached"` for files > 256 KB stored in git-ignored `.model-cache/`. |
| `relativePath` | `string` | Path relative to `tests/DotNodeViewer.Core.Tests/` (`Fixtures/<folder>/<fileName>` or `.model-cache/<folder>/<fileName>`). |
| `sourceUrl` | `string?` | Remote download URL for automatic fetching by CI / test loader, or `null` for committed/local-only fixtures. |
| `sha256` | `string` | Lowercase 64-character hexadecimal SHA-256 integrity digest. |
| `byteLength` | `long` | Exact model file size in bytes. |
| `expectedSnapshot` | `string` | Path relative to `tests/DotNodeViewer.Core.Tests/` to the golden `.expected.json` file. |

### Format & Directory Mapping

| Format Slug (`format`) | Fixture Folder | ID Prefix | Format Reader Display Name (`format` in snapshot) |
|---|---|---|---|
| `onnx` | `Fixtures/onnx` | `onnx-` | `ONNX IR v9` / `ONNX IR v3` |
| `tflite` | `Fixtures/tflite` | `tflite-` | `TensorFlow Lite v3` |
| `executorch` | `Fixtures/executorch` | `executorch-` | `ExecuTorch` |
| `pytorch` | `Fixtures/pytorch` | `pytorch-` | `PyTorch` |
| `pytorch-pt2` | `Fixtures/pytorch_pt2` | `pytorch_pt2-` | `PyTorch ExportedProgram (.pt2)` |
| `keras` | `Fixtures/keras` | `keras-` | `Keras (.keras/.h5)` |

---

## 2. `<model-id>.expected.json` Topology Snapshot Schema

The golden expected topology snapshot lives at `Fixtures/<folder>/<model-id-or-stem>.expected.json` (always committed to git, even when the model binary itself is `cached`).

It is constructed deterministically via the CLI snapshot harness:
```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release --no-build -- snapshot <path-to-binary> --id <model-id>
```

```json
{
  "modelId": "tflite-mobilenet_v2_1.0_224_quant",
  "format": "TensorFlow Lite v3",
  "subgraphCount": 1,
  "primarySubgraph": {
    "name": "main",
    "nodeCount": 66,
    "operatorHistogram": {
      "CONV_2D": 36,
      "DEPTHWISE_CONV_2D": 17,
      "ADD": 10,
      "AVERAGE_POOL_2D": 1,
      "RESHAPE": 1,
      "SOFTMAX": 1
    },
    "inputs": [
      {
        "name": "input",
        "dataType": "UInt8",
        "shape": [
          "1",
          "224",
          "224",
          "3"
        ],
        "quantization": {
          "scale": [
            0.0078125
          ],
          "zeroPoint": [
            128
          ]
        }
      }
    ],
    "outputs": [
      {
        "name": "MobilenetV2/Predictions/Softmax",
        "dataType": "UInt8",
        "shape": [
          "1",
          "1001"
        ],
        "quantization": {
          "scale": [
            0.00390625
          ],
          "zeroPoint": [
            0
          ]
        }
      }
    ],
    "sampleWeightSlice": {
      "tensorName": "MobilenetV2/Logits/Squeeze",
      "dataType": "Int32",
      "streamOffset": 3546420,
      "byteLength": 8
    }
  }
}
```

### Snapshot Structure

- **`modelId`**: Must match `entry.Id` in `models.manifest.json`.
- **`format`**: Formatted display name and IR/container schema version emitted by `ModelGraph.Format.ToDisplayString()`.
- **`subgraphCount`**: Total number of subgraphs in `ModelGraph.Subgraphs`.
- **`primarySubgraph`**: Summarizes `ModelGraph.PrimarySubgraph`:
  - `name`: Subgraph identifier.
  - `nodeCount`: Total operator nodes in this subgraph.
  - `operatorHistogram`: Map of operator names to occurrence counts.
  - `inputs`: Ordered list of graph input tensor snapshots with `name`, `dataType`, `shape`, and optional `quantization` (`scale`, `zeroPoint`, `quantizedDimension`).
  - `outputs`: Ordered list of graph output tensor snapshots.
  - `sampleWeightSlice` (optional): Verifies zero-allocation lazy streaming. Points to a representative initializer/weight tensor with `tensorName`, `dataType`, `streamOffset` (> 0), and `byteLength` (> 0).

---

## 3. Independent Ground-Truth (`*.truth.json`) Schema

For committed, deterministic model fixtures generated via Python scripts (`tools/model-gen/`), an independent ground-truth snapshot is emitted at `Fixtures/<folder>/<stem>.truth.json`.

Format integration tests (`*ModelTests.cs`) cross-check both the parsed `ModelGraph` and the golden `.expected.json` against this independent Python ground-truth file to ensure the parser never tautologically validates against itself.

```json
{
  "modelId": "onnx-tiny_mlp",
  "format": "ONNX IR v9",
  "subgraphCount": 1,
  "primarySubgraph": {
    "name": "tiny_mlp",
    "nodeCount": 3,
    "operatorHistogram": {
      "Gemm": 1,
      "Relu": 1,
      "Add": 1
    },
    "inputs": [
      {
        "name": "x",
        "dataType": "Float32",
        "shape": [
          "1",
          "8"
        ]
      }
    ],
    "outputs": [
      {
        "name": "y",
        "dataType": "Float32",
        "shape": [
          "1",
          "4"
        ]
      }
    ],
    "initializers": [
      {
        "name": "fc1_w",
        "dataType": "Float32",
        "shape": [
          "4",
          "8"
        ],
        "byteLength": 128,
        "isExternal": false,
        "externalLocation": null
      }
    ]
  }
}
```

---

## 4. C# Test Harness Resolution Pattern (`ModelFixtureLoader`)

`DotNodeViewer.Core.Tests.Harness.ModelFixtureLoader` is the single source of truth for locating, downloading, verifying, and streaming model fixtures:

1. **Locate Test Root:** Dynamically climbs from `AppContext.BaseDirectory` to find the directory containing `Fixtures/models.manifest.json`.
2. **Download on Demand:** If a file is absent and has a non-null `sourceUrl`, `OpenVerifiedModelStreamAsync` downloads it into `.model-cache/` using a temporary file (`.tmp`), validates the expected `byteLength` and `sha256` digest, and atomically renames it.
3. **Graceful Skip for Local-Only Large Files:** If a cached model without a `sourceUrl` is absent, `OpenVerifiedModelStreamAsync` returns `null` so tests can skip cleanly.
4. **Streaming Verification:** Returns a seekable `FileStream` positioned at byte 0. Readers consume the `Stream` directly—never calling `File.ReadAllBytes` or loading weights into the managed heap.

---

## 5. Manifest-Driven Parameterized Tests (`ModelManifestTests.cs`)

`ModelManifestTests` separates regression coverage into format-partitioned test methods discoverable by Microsoft.Testing.Platform filters:

```csharp
[Test]
[MethodDataSource(nameof(GetRegisteredOnnxModels))]
public async Task ParseRegisteredOnnxModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
    await AssertModelMatchesExpectedTopologyAsync(entry);

[Test]
[MethodDataSource(nameof(GetRegisteredTfliteModels))]
public async Task ParseRegisteredTfliteModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
    await AssertModelMatchesExpectedTopologyAsync(entry);

[Test]
[MethodDataSource(nameof(GetRegisteredExecuTorchModels))]
public async Task ParseRegisteredExecuTorchModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
    await AssertModelMatchesExpectedTopologyAsync(entry);

[Test]
[MethodDataSource(nameof(GetRegisteredPyTorchModels))]
public async Task ParseRegisteredPyTorchModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
    await AssertModelMatchesExpectedTopologyAsync(entry);

[Test]
[MethodDataSource(nameof(GetRegisteredPyTorchPt2Models))]
public async Task ParseRegisteredPyTorchPt2Model_MatchesExpectedTopology(ModelManifestEntry entry) =>
    await AssertModelMatchesExpectedTopologyAsync(entry);

[Test]
[MethodDataSource(nameof(GetRegisteredKerasModels))]
public async Task ParseRegisteredKerasModel_MatchesExpectedTopology(ModelManifestEntry entry) =>
    await AssertModelMatchesExpectedTopologyAsync(entry);
```
