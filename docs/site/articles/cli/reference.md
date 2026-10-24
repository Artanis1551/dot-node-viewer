# Reference: `DotNodeViewer.Cli`

`DotNodeViewer.Cli` (`tools/DotNodeViewer.Cli`) is a NativeAOT-compatible command-line tool built on `DotNodeViewer.Core` for detecting neural network formats, inspecting graph summaries, and generating golden topology snapshots (`*.expected.json`).

---

## Command Dispatch Architecture

```mermaid
flowchart TD
    CLI["DotNodeViewer.Cli &lt;command&gt; &lt;file&gt;"] --> Dispatch{"Command Dispatch"}
    Dispatch -->|"detect"| Detect["ModelProbe Header Scan (first 64 bytes)"]
    Dispatch -->|"inspect"| Inspect["ModelFormatRegistry.ReadAsync (Lazy weight slicing)"]
    Dispatch -->|"snapshot"| Snapshot["ModelFormatRegistry.ReadAsync + Topology Projection"]

    Detect --> DetectOut["Print ModelFormatId (e.g. onnx, tflite)"]
    Inspect --> InspectOut["Print Human-Readable Graph &amp; Tensor Summary"]
    Snapshot --> SnapshotOut["Print Deterministic JSON Topology Snapshot"]

    DetectOut --> Exit0["Exit 0 (Success)"]
    InspectOut --> Exit0
    SnapshotOut --> Exit0
```

---

## Synopsis

```text
Usage: DotNodeViewer.Cli <command> <file> [options]

Commands:
  detect <file>                          Print the detected model format ID
  inspect <file> [--expand-delegates]    Print a human-readable model graph summary
  snapshot <file> --id <model-id>        Print the golden .expected.json topology snapshot
  cmdstream <file> [--node <name>]       Disassemble NPU hardware command stream
```

---

## Commands & Real-World Output Examples

### 1. `detect` — Detect Model Format ID

Probes the first 64 header bytes (and lazy ZIP central-directory entries when applicable) via `ModelProbe` and `ModelFormatRegistry.Default`, then prints the canonical lowercase `ModelFormatId` (`onnx`, `tflite`, `executorch`, `pytorch`, `pytorch-pt2`, or `keras`) to standard output.

#### ONNX Example:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- detect tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx
```

**Output:**
```text
onnx
```

#### TensorFlow Lite Example:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- detect tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite
```

**Output:**
```text
tflite
```

---

### 2. `inspect` — Print Human-Readable Graph Summary

Parses the model via `ModelFormatRegistry.Default.ReadAsync` (without loading raw tensor weights into memory) and prints a structured summary of the model's format, producer, subgraphs, node/tensor counts, input/output tensor shapes, and any `ParseDiagnostic` entries.

#### Single-Subgraph ONNX Model:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx
```

**Output:**
```text
Format:    ONNX IR v9 (onnx)
Producer:  dot-node-viewer-model-gen 1.0
Subgraphs: 1

Subgraph [0] 'tiny_mlp':
  Nodes:   3
  Tensors: 7
  Inputs:  1
    - x: Float32 [1, 8]
  Outputs: 1
    - y: Float32 [1, 4]
```

#### Quantized CNN TFLite Model:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite
```

**Output:**
```text
Format:    TensorFlow Lite v3 (tflite)
Producer:  MLIR Converted.
Subgraphs: 1

Subgraph [0] 'main':
  Nodes:   5
  Tensors: 11
  Inputs:  1
    - serving_default_input:0: Int8 [1, 8, 8, 3]
  Outputs: 1
    - StatefulPartitionedCall_1:0: Int8 [1, 3]
```

#### Multi-Subgraph Control-Flow Model (`tiny_while.tflite`):

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_while.tflite
```

**Output:**
```text
Format:    TensorFlow Lite v3 (tflite)
Producer:  MLIR Converted.
Subgraphs: 3

Subgraph [0] 'main':
  Nodes:   1
  Tensors: 4
  Inputs:  2
    - serving_default_i:0: Int32 []
    - serving_default_x:0: Float32 [1, 4]
  Outputs: 2
    - StatefulPartitionedCall:0: Int32 []
    - StatefulPartitionedCall:1: Float32 [1, 4]

Subgraph [1] 'while_cond':
  Nodes:   1
  Tensors: 4
  Inputs:  2
    - arg: Int32 []
    - arg: Float32 [1, 4]
  Outputs: 1
    - Less:0: Bool []

Subgraph [2] 'while_body':
  Nodes:   2
  Tensors: 6
  Inputs:  2
    - arg: Int32 []
    - arg: Float32 [1, 4]
  Outputs: 2
    - Add:0: Int32 []
    - Mul:0: Float32 [1, 4]
```

#### Delegate Unwrapping Example (`--expand-delegates`):

Pass `--expand-delegates` to decompile embedded NPU delegate microcode (such as Arm Ethos-U `CUSTOM(ethos-u)` or `EthosUBackend`) and display synthesized node counts, tensor counts, and operator distributions:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8_vela_u55.tflite --expand-delegates
```

**Output:**
```text
Format:    TensorFlow Lite v3 (tflite)
Producer:  Vela Optimised
Subgraphs: 1

Subgraph [0] 'main':
  Nodes:   1
  Tensors: 5
  Inputs:  1
    - serving_default_input:0: Int8 [1, 8, 8, 3]
  Outputs: 1
    - StatefulPartitionedCall_1:0: Int8 [1, 3]

  [Delegate Expansion] 'CUSTOM(ethos-u)_0' (ethos-u) -> CUSTOM(ethos-u)_0 (Ethos-U55-128):
    Synthesized Nodes:   91
    Synthesized Tensors: 209
    DMA Operations:      19
    Operator Distribution:
      - NPU_MUL: 27
      - NPU_MAXPOOL: 21
      - NPU_DMA: 19
      - NPU_SUB: 6
      - NPU_ADD: 5
      - NPU_AVGPOOL: 3
      - NPU_CONV: 3
      - NPU_DEPTHWISE: 2
      - NPU_SHR: 2
      - NPU_CLZ: 1
      - NPU_REDUCE_SUM: 1
      - NPU_SHL: 1
```

---

### 3. `snapshot` — Generate Golden Topology Snapshot (`*.expected.json`)

Parses the model and serializes a deterministic golden topology snapshot JSON document to standard output using source-generated `System.Text.Json` metadata.

#### ONNX Snapshot:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- snapshot tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx --id onnx-tiny_mlp
```

**Output:**
```json
{
  "modelId": "onnx-tiny_mlp",
  "format": "onnx",
  "producer": "dot-node-viewer-model-gen 1.0",
  "subgraphCount": 1,
  "nodeCount": 3,
  "tensorCount": 7,
  "inputCount": 1,
  "outputCount": 1,
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
  "operatorHistogram": {
    "Gemm": 1,
    "Relu": 1,
    "Add": 1
  },
  "sampleWeightSlice": {
    "tensorName": "fc1_w",
    "streamOffset": 197,
    "byteLength": 128
  }
}
```

#### TFLite Snapshot:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- snapshot tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite --id tflite-tiny_cnn_int8
```

**Output:**
```json
{
  "modelId": "tflite-tiny_cnn_int8",
  "format": "tflite",
  "producer": "MLIR Converted.",
  "subgraphCount": 1,
  "nodeCount": 5,
  "tensorCount": 11,
  "inputCount": 1,
  "outputCount": 1,
  "inputs": [
    {
      "name": "serving_default_input:0",
      "dataType": "Int8",
      "shape": [
        "1",
        "8",
        "8",
        "3"
      ]
    }
  ],
  "outputs": [
    {
      "name": "StatefulPartitionedCall_1:0",
      "dataType": "Int8",
      "shape": [
        "1",
        "3"
      ]
    }
  ],
  "operatorHistogram": {
    "CONV_2D": 1,
    "AVERAGE_POOL_2D": 1,
    "RESHAPE": 1,
    "FULLY_CONNECTED": 1,
    "SOFTMAX": 1
  },
  "sampleWeightSlice": {
    "tensorName": "tiny_cnn/conv/bias",
    "streamOffset": 448,
    "byteLength": 16
  }
}
```

- `--id <model-id>`: Sets the `modelId` field in the emitted JSON. If omitted, defaults to the file name without extension.

---

### 4. `cmdstream` — Disassemble NPU Hardware Command Stream

Disassembles the raw binary microcode instructions embedded inside an NPU delegate node (such as Arm Ethos-U `CUSTOM(ethos-u)` or `Delegate (EthosUBackend)`), printing hex offsets, opcodes, parameters, and mnemonic instruction names.

#### Full Disassembly Trace:

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- cmdstream tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8_vela_u55.tflite
```

**Output:**
```text
Delegate: 'CUSTOM(ethos-u)_0' (ethos-u)
Command Stream Disassembly (196 instructions):
--------------------------------------------------------------------------------
0x000000:          0001 - NPU_SET_IFM_REGION
0x000004: 00000100 0000 - NPU_SET_IFM_BASE0
0x00000C: 00000000 0000 - NPU_SET_IFM_BASE1
0x000014: 00000000 0000 - NPU_SET_IFM_BASE2
0x00001C:          0001 - NPU_SET_OFM_REGION
0x000020: 00000400 0000 - NPU_SET_OFM_BASE0
0x000028: 00070007 0000 - NPU_SET_IFM_HEIGHT0_M1
0x000030: 00070007 0000 - NPU_SET_IFM_WIDTH0_M1
0x000038: 00020002 0000 - NPU_SET_IFM_DEPTH_M1
0x000040:          0000 - NPU_OP_CONV
...
```

- `--node <name>`: Optionally filters disassembly to a specific delegate node when multiple delegate nodes exist in the model.

---

## Exit Codes

| Exit Code | Meaning |
|---|---|
| `0` | Command succeeded (`--help` displayed, format detected, graph inspected, or snapshot emitted). |
| `1` | Invalid command-line arguments or target file does not exist. |
| `2` | Unrecognized model format, corrupted model file (`ModelParseException`), or I/O error. |

---

## Publishing as a NativeAOT Executable

`DotNodeViewer.Cli` is fully compatible with NativeAOT compilation:

```bash
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
```
