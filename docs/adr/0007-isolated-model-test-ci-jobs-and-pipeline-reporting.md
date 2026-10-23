# ADR 0007: Isolated Model Test CI Jobs and Pipeline Inspection Reporting

- **Status:** Accepted
- **Date:** 2026-10-23
- **Stage:** S04 / CI Maintenance

## Context

`dot-node-viewer` verifies neural network format extraction against real model files using two complementary layers:
1. **Format Integration Tests (`*ModelTests.cs`):** Format-specific assertions verifying operator attributes, tensor encodings, fused activations, control-flow subgraphs, and Python independent ground-truth (`*.truth.json`).
2. **Manifest Golden Topology Snapshots (`models.manifest.json` + `*.expected.json`):** End-to-end regression tests comparing the parsed `ModelGraph` against golden JSON topology snapshots.

Originally, all tests across the solution (unit tests, synthetic in-memory wire tests, layout tests, Avalonia UI tests, and explicit model tests) were executed in a single monolithic CI job via `dotnet test --solution dot-node-viewer.slnx`.

This created several operational deficiencies:
1. **Obscured Pipeline Inspection:** In the GitHub Actions UI, a monolithic test step provided no at-a-glance visibility into which specific models and formats were exercised or whether failure was format-specific.
2. **Missing Format Isolation:** A failure in an ONNX model test blocked or obscured TFLite model verification, and developers could not easily run only the model tests relevant to their format in CI.
3. **Inefficient Fixture Handling:** Large cached models (> 256 KB) stored in `.model-cache/` were not cached across GitHub Actions workflow runs, causing redundant downloads.

## Decision

1. **Separate CI Jobs by Test Category and Format:**
   `.github/workflows/ci.yml` isolates testing into dedicated, parallel-capable jobs:
   - `build-and-unit-tests`: Builds the solution with zero warnings, validates DocFX documentation with `--warningsAsErrors`, and runs all non-model tests (`DotNodeViewer.Layout.Tests`, `DotNodeViewer.App.Tests`, and `DotNodeViewer.Core.Tests` unit, encapsulation, IR, cursor, probe, and in-memory synthetic tests).
   - `model-tests-<format>`: Dedicated CI jobs for explicit model verification (currently `model-tests-onnx` and `model-tests-tflite`, extending to `executorch`, `pytorch`, and `keras` as those stages are implemented).
2. **Format-Partitioned Manifest Test Methods:**
   Manifest-driven golden topology tests in `ModelManifestTests` are partitioned into format-specific parameterized test methods (e.g., `ParseRegisteredOnnxModel_MatchesExpectedTopology`, `ParseRegisteredTfliteModel_MatchesExpectedTopology`) discovered via `[MethodDataSource]`. This allows Microsoft.Testing.Platform tree-node filters (`--treenode-filter`) to execute format-specific fixtures without redundant cross-format execution.
3. **Pipeline Inspection Visibility (Terminal Logs & Step Summary):**
   Every format model job:
   - Executes `dotnet test` with `--output Detailed` so every individual fixture name, parameter, and execution duration is visible in step logs.
   - Executes `tools/ci/report-tested-models.py <format>`, which prints a structured table to console stdout and appends a rich GitHub Flavored Markdown table to `$GITHUB_STEP_SUMMARY`.
4. **Fixture Caching in CI:**
   Every model test job restores and caches `tests/DotNodeViewer.Core.Tests/.model-cache/` using `actions/cache@v4` keyed by the SHA-256 hash of `tests/DotNodeViewer.Core.Tests/Fixtures/models.manifest.json`.

## Consequences

- **Inspectability:** GitHub Actions workflow runs display immediate, format-specific status badges and rendered tables showing Model ID, file name, tier, size, SHA-256 digest, and expected snapshot path directly on the GitHub Workflow Run Summary page.
- **Extensibility:** Future format stages (`S07` ExecuTorch, `S08` PyTorch, `S09` PT2, `S10` Keras) must add a corresponding `model-tests-<format>` job in `.github/workflows/ci.yml`, a corresponding manifest test method in `ModelManifestTests`, and register the format in `tools/ci/report-tested-models.py`.
- **Local Parity:** Developers can run `./tools/ci/run-model-tests.sh <format>` locally to execute the exact same detailed tests and inspection report produced in CI.
