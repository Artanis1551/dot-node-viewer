# Reference: `DotNodeViewer.Cli`

`DotNodeViewer.Cli` (`tools/DotNodeViewer.Cli`) is a NativeAOT-compatible command-line tool built on `DotNodeViewer.Core` for detecting neural network formats, inspecting graph summaries, and generating golden topology snapshots (`*.expected.json`).

---

## Synopsis

```text
Usage: DotNodeViewer.Cli <command> <file> [options]

Commands:
  detect <file>                     Print the detected model format ID
  inspect <file>                    Print a human-readable model graph summary
  snapshot <file> --id <model-id>   Print the golden .expected.json topology snapshot
```

---

## Commands

### 1. `detect` — Detect Model Format ID

Probes the first 64 header bytes (and lazy ZIP central-directory entries when applicable) via `ModelProbe` and `ModelFormatRegistry.Default`, then prints the canonical lowercase `ModelFormatId` (`onnx`, `tflite`, `executorch`, `pytorch`, `pytorch-pt2`, or `keras`) to standard output.

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- detect tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx
```

### 2. `inspect` — Print Human-Readable Graph Summary

Parses the model via `ModelFormatRegistry.Default.ReadAsync` (without loading raw tensor weights into memory) and prints a structured summary of the model's format, producer, subgraphs, node/tensor counts, input/output tensor shapes, and any `ParseDiagnostic` entries.

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx
```

### 3. `snapshot` — Generate Golden Topology Snapshot (`*.expected.json`)

Parses the model and serializes a deterministic golden topology snapshot JSON document to standard output using source-generated `System.Text.Json` metadata.

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- snapshot tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx --id onnx-tiny-mlp
```

- `--id <model-id>`: Sets the `modelId` field in the emitted JSON. If omitted, defaults to the file name without extension.

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
