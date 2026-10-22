# `dot-node-viewer` Multi-Stage Roadmap

This directory contains the sequential, self-contained stage briefs for building `dot-node-viewer`.

## How to Run a Stage with a New Agent

Each stage is executed on its own dedicated Git branch (`stage/Sxx-<slug>`) and the agent is authorized to make incremental, logical sub-commits (`Sxx: ...`) as it completes each part of the stage.

Give the agent a prompt like:

> Read `AGENTS.md`, `docs/roadmap/PROGRESS.md`, and `docs/roadmap/stages/S00-bootstrap-and-governance.md`, and execute Stage S00 on branch `stage/S00-bootstrap-and-governance`.

## Stage Dependency Order

```mermaid
flowchart TD
  S00["S00 Bootstrap and governance"] --> S01["S01 Core IR, harness, CLI"]
  S01 --> S02["S02 ONNX + Protobuf streaming + model-gen"]
  S02 --> S03["S03 TFLite + FlatBuffers"]
  S03 --> S04["S04 Layout engine (DotNodeViewer.Layout)"]
  S04 --> S05["S05 UI Iteration 1a: Design system + Avalonia shell"]
  S05 --> S06["S06 UI Iteration 1b: Graph canvas + basic node properties"]
  S06 --> S07["S07 ExecuTorch parser"]
  S07 --> S08["S08 PyTorch: ZIP, Pickle, state_dict, TorchScript"]
  S08 --> S09["S09 PyTorch .pt2 (torch.export)"]
  S09 --> S10["S10 Keras: .keras v3 + HDF5"]
  S10 --> S11["S11 UI Iteration 2: Polish, tensor weight inspector, search, subgraph drill-down"]
  S11 --> S12["S12 Browser (WASM) head, AOT, perf & packaging hardening"]
  S12 -.optional.-> S13["S13 Avalonia 12 migration"]
```

## Recommended User Checkpoints

- **During S05:** Approve `docs/design/design-system.md` before the agent writes the Avalonia shell XAML.
- **After S06 & After S10:** Optionally invoke `.agents/skills/improve-codebase-architecture/SKILL.md` to review module depth and seams.
