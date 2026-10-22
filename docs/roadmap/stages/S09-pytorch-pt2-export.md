# Stage S09 — PyTorch `.pt2` (`torch.export` Archive)

## Goal
Implement detection and parsing for modern PyTorch 2 `torch.export.save` (`.pt2`) archives (`ExportedProgram` JSON schema + stored weight slices), which provide exact ATen operator graphs and symbolic dimension constraints (`SymInt`), and play a central role for users exporting PyTorch models to TFLite or ExecuTorch.

## Prerequisites
- Stage **S08** complete (reuses `ZipDirectory` and `PickleMachine`).
- Read [`AGENTS.md`](../../../AGENTS.md), [`GLOSSARY.md`](../../../GLOSSARY.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/port-model-format/SKILL.md` (and `references/ir-mapping-checklist.md`)
- `.agents/skills/add-test-model/SKILL.md` (and `references/manifest-and-snapshot-schema.md`)
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/documentation-writer/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`

## Scope & Deliverables
1. **PyTorch `.pt2` Detector & Reader (`src/DotNodeViewer.Core/Formats/PyTorchPt2/`, `internal`):**
   - `PyTorchPt2FormatDetector`: inspects `ModelProbe` ZIP entries for `archive_format` (`pt2` / `exported_program`) or `models/*.json` (`ExportedProgram` JSON).
   - `PyTorchPt2FormatReader`:
     - Uses source-generated `JsonSerializerContext` + `Utf8JsonReader` to parse `models/model.json` (and handles both PyTorch `<= 2.5` and `>= 2.6` archive directory layouts).
     - Parses `graph_module.graph`:
       - `tensor_values`: maps `dtype` → `DataType`, `sizes` (both static integers and `as_sym_int` symbolic names like `s0` / `batch`) → `TensorShape` (`Fixed` / `Symbolic`), `layout`.
       - `inputs` & `outputs` cross-referenced with `graph_signature` (`input_specs` / `user_inputs`, `inputs_to_parameters`, `inputs_to_buffers`, `inputs_to_lifted_tensor_constants`, `user_outputs`, `buffers_to_mutate`):
         - Parameters, buffers, and lifted constants are classified as `TensorKind.Initializer` (or `Constant`) rather than user `GraphInput`s.
       - `nodes`: maps `target` (e.g., `torch.ops.aten.conv2d.default`, `torch.ops.aten.relu.default`, `torch.ops.aten.add.Tensor`), `inputs` (`NamedArgument` with `as_tensor`, `as_tensors`, scalar `as_int`/`as_float`/`as_bool`/`as_string`/`as_ints` → `NodePort`s vs `NodeAttribute`s), `outputs`, and `metadata` (`nn_module_stack`, `stack_trace` summary).
     - Resolves weight slices lazily from `data/weights/<name>` (or `weights/` config + stored ZIP entries, or fallback `serialized_weights.pt` via `PickleMachine`) into `TensorDataSlice`.
     - Records `range_constraints` (e.g., `s0 in [1, 64]`) in `ModelGraph.Metadata`.
2. **Python Generator (`tools/model-gen/torch/gen_pt2.py`) & Base Models:**
   - Committed (`<= 256 KB`):
     - `tiny_cnn_export.pt2` (`torch.export.export` + `torch.export.save` of a CNN)
     - `tiny_dynamic_batch_export.pt2` (`torch.export.Dim("batch", min=1, max=32)` exercising `TensorDimension.Symbolic` and `range_constraints`)
3. **Tests (`tests/DotNodeViewer.Core.Tests/`):**
   - `Synthetic/PyTorchPt2SyntheticTests.cs` (the 4 mandatory tests).
   - `Formats/PyTorchPt2ModelTests.cs` (verifying `Initializer` separation from user inputs, symbolic dimension extraction, lazy weight slices, and `.truth.json` parity).
4. **Documentation (`README.md` & `docs/site/`):**
   - Update root `README.md` and `docs/site/articles/formats/pytorch.md` to cover PyTorch 2 `torch.export` (`.pt2`) archives, `SymInt` dynamic dimensions, and how to compare a `.pt2` export against a downstream `.tflite` or `.pte` conversion.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```

## Hand-off & Git Requirements
- Work on branch `stage/S09-pytorch-pt2` (`git checkout -b stage/S09-pytorch-pt2`).
- Commit your work incrementally in logical sub-commits prefixed with `S09:` (e.g., `S09: implement PyTorch .pt2 ExportedProgram detector and reader`, `S09: add .pt2 fixtures and golden snapshots`, `S09: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S09** complete.
