# Stage S07 — ExecuTorch Parser (`.pte`)

## Goal
Implement the ExecuTorch (`.pte`) format detector and streaming parser (`DotNodeViewer.Core.Formats.ExecuTorch`), reusing the FlatBuffers infrastructure from Stage S03, set up the PyTorch/ExecuTorch Python generator (`tools/model-gen/torch/`), and verify that `.pte` models (including XNNPACK delegated graphs) parse and render cleanly in `DotNodeViewer.Desktop`.

## Prerequisites
- Stage **S06** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`PREREQUISITES.md`](../../../PREREQUISITES.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/port-model-format/SKILL.md` (and `references/ir-mapping-checklist.md`)
- `.agents/skills/add-test-model/SKILL.md` (and `references/manifest-and-snapshot-schema.md`)
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`
- `.agents/skills/computer-use/SKILL.md` (optional visual sanity check of `.pte` in `DotNodeViewer.Desktop`)

## Scope & Deliverables
1. **ExecuTorch Detector & Reader (`src/DotNodeViewer.Core/Formats/ExecuTorch/`, `internal`):**
   - `ExecuTorchFormatDetector`: checks FlatBuffers identifier `ET12` at bytes `4..8` (and accepts `ET1x` with a version diagnostic); parses optional extended header `eh00` at offset `8` (`program_size`, `segment_base_offset`); leaves `stream.Position == 0`.
   - `ExecuTorchFormatReader`:
     - Maps `Program.execution_plan[]` → one `Subgraph` per plan (e.g., `forward`).
     - Decodes `values[]` (`EValue` union: `Tensor`, `Int`, `Double`, `Bool`, `String`, `IntList`, `TensorList`, etc.).
     - Maps `Tensor` metadata: `scalar_type` → `DataType`, `sizes` + `shape_dynamism` → `TensorShape` (`STATIC` → `Fixed`, `DYNAMIC_BOUND`/`DYNAMIC_UNBOUND` → `Dynamic`), `dim_order`.
     - Resolves constant tensor weights lazily:
       - Inline `constant_buffer[buffer_idx]` → `TensorDataSlice`
       - Segmented `constant_segment` + `segments[segment_index]` (`segment_base_offset + DataSegment.offset`) → `TensorDataSlice`
       - External `named_data` / `.ptd` → `ExternalTensorDataReference`
     - Maps `chains[].instructions[]`:
       - `KernelCall` → `GraphNode` named by `operators[op_index]` (`name` + `overload`, e.g., `aten::linear.out`, `aten::conv2d.out`), resolving input/output `EValue` indices into `NodePort`s and scalar `EValue` arguments into `NodeAttribute`s.
       - `DelegateCall` → `GraphNode` with `OperatorCategory.Custom`, operator name `Delegate (<backend_id>)` (e.g., `XnnpackBackend`, `CoreMLBackend`, `EthosUBackend`), compile specs as attributes, and the `BackendDelegate.processed` payload referenced as an `AttributeValue.Bytes(TensorDataSlice)`.
       - `JumpFalseCall` / `MoveCall` / `FreeCall` → handled per documented ADR (`MoveCall` wired as alias/identity or `Tensor` op; `JumpFalseCall` as `ControlFlow`; `FreeCall` omitted from visual topology).
   - `ExecuTorchOperatorCatalog`: maps `aten::*` and `executorch_prim::*` operators to `OperatorCategory`.
   - Register ExecuTorch in `ModelFormatRegistry.Default`.
2. **Python Model Generator (`tools/model-gen/torch/`):**
   - Set up `tools/model-gen/torch/requirements.txt` (per `PREREQUISITES.md`: CPU `torch` + `executorch`) and `gen_executorch.py` emitting `.pte` models and `.truth.json`.
3. **Base ExecuTorch Test Models:**
   - Committed (`<= 256 KB`):
     - `tiny_mlp_portable.pte` (portable `aten` kernels: `linear`, `relu`, `add`)
     - `tiny_cnn_xnnpack.pte` (lowered to `XnnpackBackend` delegate, testing `DelegateCall` + segmented constant weights)
   - Cached (`> 256 KB`): if a small public `.pte` URL is available on Hugging Face (`executorch-community`), register it in `models.manifest.json`.
4. **Tests (`tests/DotNodeViewer.Core.Tests/`):**
   - `Synthetic/ExecuTorchSyntheticTests.cs` (the 4 mandatory tests, including extended `eh00` segment offset math).
   - `Formats/ExecuTorchModelTests.cs` (verifying portable ops, `XnnpackBackend` delegate node & payload slice, and `*.truth.json` parity).

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
```

## Hand-off & Git Requirements
- Work on branch `stage/S07-executorch-parser` (`git checkout -b stage/S07-executorch-parser`).
- Commit your work incrementally in logical sub-commits prefixed with `S07:` (e.g., `S07: implement ExecuTorch detector and reader`, `S07: add torch/executorch model-gen, fixtures, and golden snapshots`, `S07: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S07** complete.
