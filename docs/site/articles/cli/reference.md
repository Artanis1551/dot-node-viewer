# Reference & How-To: `DotNodeViewer.Cli`

> [!NOTE]
> The CLI commands (`detect`, `inspect`, `snapshot`) are implemented in **Stage S01** (`S01-core-ir-harness-and-cli`).

---

## Overview

`DotNodeViewer.Cli` (`tools/DotNodeViewer.Cli`) is a NativeAOT-compatible command-line tool built on `DotNodeViewer.Core` for inspecting neural network files and generating golden topology snapshots (`*.expected.json`).

---

## Planned Commands (Stage S01)

### Detect Format (`detect`)

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- detect <path-to-model>
```

Probes the model file header and prints the detected format identifier.

### Inspect Summary (`inspect`)

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- inspect <path-to-model>
```

Prints a human-readable summary of the model's format, producer, subgraphs, inputs, outputs, and operator counts.

### Generate Topology Snapshot (`snapshot`)

```bash
dotnet run --project tools/DotNodeViewer.Cli -c Release -- snapshot <path-to-model> --id <model-id>
```

Outputs a deterministic `.expected.json` golden topology snapshot to stdout for regression testing.

