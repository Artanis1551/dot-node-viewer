---
name: add-test-model
description: >
  Workflow for adding a specific neural network model (from a local file path or a download URL)
  to the .NET parser test suite. Handles downloading, SHA-256 hashing, Git-safe storage routing
  (in-repo fixture vs. cached manifest entry), topology snapshot extraction, and unit/regression
  test generation. Use whenever asked to add a model file or URL as a test case.
---

# Adding a Model to the Test Suite

Follow this workflow whenever adding a concrete neural network model (via a local path or a remote URL) to the test suite.

## 1. Core Rules for Model Test Fixtures

1. **Never Bloat Git History with Large Binaries**
   - **Small models (256 KB or smaller):** Commit directly into `tests/<TestProject>/Fixtures/<format>/` for fast, offline CI execution.
   - **Large models (greater than 256 KB):** NEVER commit directly to Git unless explicitly instructed. Store the binary in the git-ignored `tests/<TestProject>/.model-cache/<format>/` directory and register its metadata (`sourceUrl` or `localHint`, `sha256`, `byteLength`) in `tests/<TestProject>/Fixtures/models.manifest.json`.
   - Ensure `.model-cache/` is listed in the repository's `.gitignore`.

2. **Always Verify Integrity with SHA-256**
   - Every test model—whether committed in `Fixtures/` or cached in `.model-cache/`—must have its exact `byteLength` and lowercase `sha256` digest recorded in `models.manifest.json` so corrupted downloads or silent model updates fail fast before parser assertions run.

3. **Assert Both Graph Topology and Lazy Weight Slicing**
   - Every model test must verify:
     1. **Format detection:** `IModelFormatDetector` identifies the format from the stream header and leaves `stream.Position == 0`.
     2. **Graph metadata & I/O shapes:** Expected subgraph count, total node count, and exact input/output `TensorShape` dimensions (including dynamic/symbolic dimensions).
     3. **Zero-allocation weight slices:** At least one initializer/weight tensor has a valid `TensorDataSlice` (`StreamOffset > 0`, `ByteLength > 0`, and `StreamOffset + ByteLength <= fileByteLength`) without loading the raw weight buffer onto the managed heap.

---

## 2. Step-by-Step Execution Workflow

### Step 1: Stage & Inspect the Model Binary
Run the bundled staging script `.agents/skills/add-test-model/scripts/stage-model.sh` to fetch or copy the model, compute its SHA-256 digest and byte size, inspect its magic bytes, and place it in the right tier (`Fixtures/` vs `.model-cache/`):

```bash
bash .agents/skills/add-test-model/scripts/stage-model.sh "<file-path-or-url>" "<format-slug>" "<test-project-dir>"
```

- `<file-path-or-url>`: Local path (e.g., `~/Downloads/mobilenet_v2.tflite`) or remote URL (e.g., `https://huggingface.co/.../model.onnx`).
- `<format-slug>`: Lowercase format folder name (e.g., `onnx`, `tflite`, `keras`, `pytorch`, `executorch`).
- `<test-project-dir>`: Path to the test project (defaults to `tests/DotNodeViewer.Core.Tests`).

### Step 2: Register the Model in `models.manifest.json`
1. Read `references/manifest-and-snapshot-schema.md` for the exact JSON structure.
2. Add or update the model entry in `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json` using the `id`, `tier` (`committed` or `cached`), `relativePath`, `sourceUrl`, `sha256`, and `byteLength` output by `stage-model.sh`.

### Step 3: Extract the Expected Topology Snapshot
1. Generate the golden topology snapshot at `tests/DotNodeViewer.Core.Tests/Fixtures/<format>/<model-id>.expected.json`:
   - Run the parser against the staged file via the CLI harness (`dotnet run --project tools/DotNodeViewer.Cli -c Release -- snapshot <file> --id <model-id>`) and cross-check against independent ground truth (e.g., `.truth.json` emitted by the Python generator in `tools/model-gen/` or reference Netron).
   - If the model is being added to reproduce a parser bug before the fix is complete, inspect the file header/schema directly (or compare against reference Netron) to author the ground-truth `.expected.json` first (TDD).
2. Capture:
   - `format`: Format identifier and version string.
   - `subgraphs`: Count and primary graph name.
   - `nodeCount`: Total number of operator nodes in the primary graph.
   - `operatorHistogram`: Dictionary of operator type names to counts (e.g., `{"Conv": 52, "Relu": 35, "Add": 10}`).
   - `inputs` & `outputs`: Name, `dataType`, and `shape` (e.g., `["batch", 3, 224, 224]`).
   - `sampleWeightSlice`: Name, `dataType`, `streamOffset`, and `byteLength` of a representative initializer tensor to verify lazy stream slicing.

### Step 4: Wire Up the Test Case
1. **Standard regression coverage:** Confirm the manifest-driven parameterized TUnit test (`ModelManifestTests.ParseRegisteredModel_MatchesExpectedTopology` via `[MethodDataSource]`) discovers the new entry in `models.manifest.json`.
2. **Model-specific edge-case assertions (when applicable):** If the user added this model to test a specific feature (such as per-channel int8 quantization parameters, NPU custom delegate payloads like `ethos-u`, or nested control-flow subgraphs), add a dedicated `[Test]` method in `tests/DotNodeViewer.Core.Tests/Formats/<Format>ModelTests.cs` targeting that exact node or tensor attribute.
   - For `cached` tier models without a public `sourceUrl` (local-only large files), guard the test with a helper that skips gracefully with a descriptive message when the uncached binary is absent on another machine.

### Step 5: Verify via `dotnet test`
Run the targeted TUnit test filter to confirm the model downloads/loads, passes SHA-256 verification, parses without unexpected heap allocations, and matches the snapshot:

```bash
dotnet test --project tests/DotNodeViewer.Core.Tests -c Release -- --treenode-filter "/*/*/<TestClassName>/*"
```