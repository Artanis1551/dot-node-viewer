# Sequential Agent Progress Log (`dot-node-viewer`)

Every stage agent MUST read this file before starting work and append a completion entry before committing.

## Stage Status Table

| Stage | Brief | Branch | Status | Notes |
|---|---|---|---|---|
| **S00** | [`S00-bootstrap-and-governance.md`](stages/S00-bootstrap-and-governance.md) | `stage/S00-bootstrap-and-governance` | Pending | Solution scaffolding, `Directory.Build.props`, ADRs, CI |
| **S01** | [`S01-core-ir-harness-and-cli.md`](stages/S01-core-ir-harness-and-cli.md) | `stage/S01-core-ir-harness-and-cli` | Pending | Unified Graph IR records, `ModelFormatRegistry`, `ModelFixtureLoader`, `DotNodeViewer.Cli` |
| **S02** | [`S02-onnx-parser-and-model-gen.md`](stages/S02-onnx-parser-and-model-gen.md) | `stage/S02-onnx-parser` | Pending | `ProtoReader`, ONNX detector & streaming reader, `tools/model-gen/onnx`, base ONNX models |
| **S03** | [`S03-tflite-parser-and-flatbuffers.md`](stages/S03-tflite-parser-and-flatbuffers.md) | `stage/S03-tflite-parser` | Pending | FlatBuffers support, TFLite detector & reader, quantization metadata, `tools/model-gen/tf` |
| **S04** | [`S04-layout-engine.md`](stages/S04-layout-engine.md) | `stage/S04-layout-engine` | Pending | `DotNodeViewer.Layout` pure C# Sugiyama/dagre layered layout + `ModelGraphLayoutAdapter` |
| **S05** | [`S05-ui-iteration-1a-design-system-and-shell.md`](stages/S05-ui-iteration-1a-design-system-and-shell.md) | `stage/S05-ui-design-and-shell` | Pending | `frontend-design` token system, Avalonia shell, file loading, `computer-use` screenshots |
| **S06** | [`S06-ui-iteration-1b-graph-canvas-and-properties.md`](stages/S06-ui-iteration-1b-graph-canvas-and-properties.md) | `stage/S06-ui-graph-canvas` | Pending | Custom `GraphCanvas`, pan/zoom/hit-test, basic node properties sidebar, `computer-use` verification |
| **S07** | [`S07-executorch-parser.md`](stages/S07-executorch-parser.md) | `stage/S07-executorch-parser` | Pending | ExecuTorch `.pte` detector & reader, delegate payloads, `tools/model-gen/torch` |
| **S08** | [`S08-pytorch-zip-pickle-and-torchscript.md`](stages/S08-pytorch-zip-pickle-and-torchscript.md) | `stage/S08-pytorch-torchscript` | Pending | `ZipDirectory`, safe `PickleMachine`, `state_dict` & TorchScript `.pt` reader |
| **S09** | [`S09-pytorch-pt2-export.md`](stages/S09-pytorch-pt2-export.md) | `stage/S09-pytorch-pt2` | Pending | PyTorch `torch.export` `.pt2` archive & `ExportedProgram` JSON reader |
| **S10** | [`S10-keras-and-hdf5.md`](stages/S10-keras-and-hdf5.md) | `stage/S10-keras-and-hdf5` | Pending | Keras v3 `.keras` + legacy `.h5` reader |
| **S11** | [`S11-ui-iteration-2-polish-inspector-and-navigation.md`](stages/S11-ui-iteration-2-polish-inspector-and-navigation.md) | `stage/S11-ui-polish-and-inspector` | Pending | On-demand tensor weight inspector, search, subgraph drill-down, minimap, `computer-use` critique |
| **S12** | [`S12-browser-wasm-aot-and-perf-hardening.md`](stages/S12-browser-wasm-aot-and-perf-hardening.md) | `stage/S12-wasm-aot-and-perf` | Pending | `DotNodeViewer.Browser` WASM head, NativeAOT publish, perf audit, NuGet packaging |
| **S13** | [`S13-avalonia-12-migration.md`](stages/S13-avalonia-12-migration.md) | `stage/S13-avalonia-12-migration` | Optional | Migrate Avalonia `11.3.12` → `12.x` via `avalonia-12-migration` skill |

---

## Deferred Items (Out-of-Scope Discoveries)

*(Stage agents append deferred enhancements or follow-ups here instead of expanding their current stage's scope.)*

---

## Stage Completion Entries

### Planning Baseline (Initial Setup)
- **Completed:**
  - Created [`AGENTS.md`](../../AGENTS.md), [`PREREQUISITES.md`](../../PREREQUISITES.md), and [`GLOSSARY.md`](../../GLOSSARY.md).
  - Copied all required skills into `.agents/skills/` (`port-model-format`, `add-test-model`, `csharp-tunit`, `csharp-docs`, `csharp-async`, `analyzing-dotnet-performance`, `frontend-design`, `development-plugin-for-avalonia`, `computer-use`, `grill-with-docs`, `improve-codebase-architecture`).
  - Updated `port-model-format` and `add-test-model` skills to use `DotNodeViewer` naming and `TUnit`.
  - Authored self-contained stage briefs `S00`–`S13` under `docs/roadmap/stages/`.
- **Next Stage:** Run **Stage S00** (`docs/roadmap/stages/S00-bootstrap-and-governance.md`).
