# Stage S08 — PyTorch: ZIP Container, Safe Pickle Machine, `state_dict` & TorchScript (`.pt` / `.pth`)

## Goal
Implement the shared `ZipDirectory` reader (exposing exact file offsets for stored entries), a safe, non-executing Python Pickle VM (`PickleMachine` supporting protocols 2–5), and the PyTorch `.pt` / `.pth` format detector and reader supporting both `state_dict` / pickled `nn.Module` checkpoints and TorchScript (`trace` / `script`) archives.

## Prerequisites
- Stage **S07** complete (the `tools/model-gen/torch/.venv` environment is already set up).
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
1. **ZIP Container & Offset Scanner (`src/DotNodeViewer.Core/IO/Zip/ZipDirectory.cs`, `internal`):**
   - Parses End-of-Central-Directory (EOCD), ZIP64 EOCD, Central Directory headers, and Local File Headers over `RandomAccessSource`.
   - For `Stored` (uncompressed) entries (such as `<archive>/data/<key>` in PyTorch ZIPs), computes the exact absolute `DataOffset` and `UncompressedSize` in the outer `Stream` without reading the entry payload.
   - For small metadata entries (`data.pkl`, `constants.pkl`, `version`, `code/**/*.py`), opens a bounded stream (supporting `Stored` and `Deflate`).
2. **Safe Pickle Virtual Machine (`src/DotNodeViewer.Core/IO/Pickle/PickleMachine.cs`, `internal`):**
   - Supports Pickle protocols 2 through 5 (`PROTO`, `FRAME`, `BININT*`, `LONG1`, `BINFLOAT`, `SHORT_BINUTF8`, `BINUNICODE`, `BINBYTES*`, `EMPTY_TUPLE`, `TUPLE*`, `EMPTY_LIST`, `APPEND`, `APPENDS`, `EMPTY_DICT`, `SETITEM`, `SETITEMS`, `MARK`, `MEMOIZE`, `BINGET`, `LONG_BINGET`, `BINPUT`, `LONG_BINPUT`, `GLOBAL`, `STACK_GLOBAL`, `REDUCE`, `BUILD`, `NEWOBJ`, `NEWOBJ_EX`, `BINPERSID`, `PERSID`, `STOP`).
   - **Security invariant:** Never invokes C# reflection or arbitrary code. `GLOBAL`/`STACK_GLOBAL` produce symbolic `PyGlobal(Module, Name)` descriptors.
   - Built-in whitelisted reducers for PyTorch tensor reconstruction:
     - `torch._utils._rebuild_tensor_v2`, `torch._utils._rebuild_tensor`, `torch._utils._rebuild_parameter`, `torch._utils._rebuild_qtensor`
     - `torch.*Storage` (`FloatStorage`, `HalfStorage`, `BFloat16Storage`, `LongStorage`, `IntStorage`, `CharStorage`, `ByteStorage`, `BoolStorage`, `QInt8Storage`, `QUInt8Storage`, `TypedStorage`, `UntypedStorage`)
     - `collections.OrderedDict`, `torch.Size`, `torch.device`, `_codecs.encode`
   - Maps `BINPERSID` storage keys to `<prefix>/data/<key>` ZIP entries and computes the tensor's `TensorDataSlice` from `storage_offset * element_size` and `numel * element_size`.
   - Unrecognized `nn.Module` classes from pickled checkpoints preserve their class name (`PyObject.Type`), `_modules` dict, and `_parameters` / `_buffers` dicts.
3. **PyTorch Detector & Reader (`src/DotNodeViewer.Core/Formats/PyTorch/`, `internal`):**
   - `PyTorchFormatDetector`: detects PyTorch ZIP archives (`*/data.pkl` + `*/version` or `*/byteorder`) while distinguishing `.pt2` (`archive_format = pt2` handled in Stage S09) and Keras `.keras` (`config.json`); also detects legacy raw pickle checkpoints (`0x80 0x02..0x05` header with torch magic).
   - **TorchScript `.pt` Reader:**
     - Parses `data.pkl` and `constants.pkl` to build the `nn.Module` hierarchy and parameter/constant `TensorEdge`s with lazy `TensorDataSlice`s.
     - Parses `code/__torch__/**/*.py` via an internal **TorchScript mini-parser** (tokenizer + expression AST supporting `def forward(...)`, typed variable assignments `_0 = ...`, attribute access `self.conv1`, submodule `(self.conv1).forward(x)`, tuple unpack `_1, _2 = ...`, `torch.*` / `ops.prim.*` / `ops.aten.*` calls, and `return`).
     - Inlines leaf `nn.Module` calls (`Conv2d`, `BatchNorm2d`, `ReLU`, `Linear`, `MaxPool2d`, `AdaptiveAvgPool2d`, etc.) into `GraphNode`s with their attached weight/bias `Initializer` ports and inferred hyperparameter attributes.
     - **Graceful Fallback:** If `code/*.py` uses unsupported scripted control flow (`if`/`while` blocks that cannot be statically lowered), emits a clear `ParseDiagnostic` and returns the module-hierarchy graph so the file still opens cleanly.
   - **`state_dict` / Pickled Module Reader:**
     - Groups hierarchical parameter keys (`layer1.0.conv1.weight`, `layer1.0.conv1.bias`) into module nodes (`layer1.0.conv1`) with `Initializer` ports and exact `TensorShape` + `TensorDataSlice`, without fabricating fake activation edges, and attaches an informational `ParseDiagnostic` explaining that the file contains weights/module parameters without a recorded forward graph.
4. **Python Generator (`tools/model-gen/torch/gen_pytorch.py`) & Base Models:**
   - Committed (`<= 256 KB`):
     - `tiny_cnn_traced.pt` (`torch.jit.trace` of a Conv2d + BatchNorm2d + ReLU + Linear network)
     - `tiny_cnn_scripted_if.pt` (`torch.jit.script` with data-dependent `if` branch, exercising either control-flow node or documented hierarchy fallback + diagnostic)
     - `tiny_cnn_state_dict.pt` (`torch.save(model.state_dict(), ...)` verifying hierarchical parameter grouping and lazy slices)
5. **Tests (`tests/DotNodeViewer.Core.Tests/`):**
   - Unit tests for `ZipDirectory` and `PickleMachine` (including hostile/unknown globals).
   - `Synthetic/PyTorchSyntheticTests.cs` (the 4 mandatory tests).
   - `Formats/PyTorchModelTests.cs` (traced graph vs `.truth.json`, scripted fallback diagnostic, state_dict weight slices).
6. **Documentation (`README.md` & `docs/site/`):**
   - Update root `README.md` supported-formats table with PyTorch TorchScript & `state_dict` (`.pt` / `.pth`).
   - Add `docs/site/articles/formats/pytorch.md` (and update `toc.yml` / `overview.md`) explaining safe Pickle VM execution, TorchScript traced vs. scripted graphs, and `state_dict` inspection.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
dotnet publish tools/DotNodeViewer.Cli -c Release -p:PublishAot=true
dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors
```

## Hand-off & Git Requirements
- Work on branch `stage/S08-pytorch-torchscript` (`git checkout -b stage/S08-pytorch-torchscript`).
- Commit your work incrementally in logical sub-commits prefixed with `S08:` (e.g., `S08: add ZipDirectory and safe PickleMachine`, `S08: implement PyTorch state_dict and TorchScript reader`, `S08: add PyTorch fixtures and golden snapshots`, `S08: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S08** complete.
