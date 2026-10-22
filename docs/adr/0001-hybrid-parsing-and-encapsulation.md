# ADR 0001: Hybrid Format Parsing & Strict Schema Encapsulation

- **Status:** Accepted
- **Date:** 2026-10-23
- **Stage:** S00

## Context

`dot-node-viewer` parses heterogeneous neural network model formats (ONNX Protobuf, TFLite FlatBuffers, ExecuTorch FlatBuffers + container envelopes, PyTorch ZIP/Pickle/TorchScript, PyTorch `.pt2` `ExportedProgram`, and Keras `.keras`/HDF5). Each format uses its own serialization framing and schema definitions.

Exposing format-specific third-party libraries (`Google.Protobuf`, `Google.FlatBuffers`, `PureHDF`, or generated schema classes) on the public API surface of `DotNodeViewer.Core` would:
1. Couple downstream consumers (`DotNodeViewer.Layout`, `DotNodeViewer.App`, CLI tools, and external NuGet consumers) to internal serialization dependencies and version churn.
2. Risk leaking eager weight-buffer allocations from naive full-message deserializers.
3. Complicate NativeAOT and WebAssembly (`net10.0-browser`) trimming guarantees.

At the same time, writing every binary framing parser from scratch when mature AOT-compatible primitives exist adds unnecessary maintenance risk.

## Decision

1. **Pragmatic Hybrid Parsing Inside `DotNodeViewer.Core`:**
   Format readers under `DotNodeViewer.Core.Formats.<FormatName>` may combine AOT/trimming-compatible parsing libraries (`Google.Protobuf`, `Google.FlatBuffers`/`FlatSharp.Runtime`, `PureHDF`, `System.IO.Compression.ZipArchive`, and source-generated `System.Text.Json`) with custom zero-allocation span/stream readers (`RandomAccessSource`, `BinaryCursor`, custom tag-skipping readers) to extract graph topology while skipping raw tensor payloads.
2. **Strict Internal Encapsulation:**
   All format-specific deserialization types, generated schemas, and container readers MUST be declared `internal`. No third-party format type may appear in any `public` or `protected` signature, property, or exception in `DotNodeViewer.Core`.
3. **Unified Exception Boundary:**
   All format-level corruption, truncation, or schema mismatches MUST be caught and wrapped in `ModelParseException` (carrying `FileOffset` and node/tensor `Context`).
4. **Automated Enforcement:**
   - An MSBuild target in `Directory.Build.props` forbids `Avalonia*` and `SkiaSharp*` references in `DotNodeViewer.Core` and `DotNodeViewer.Layout`.
   - `PublicApiEncapsulationTests` (introduced in Stage S01) verifies via reflection that no public type in `DotNodeViewer.Core` exposes any `*.Formats.*`, `*.IO.*`, or third-party schema type.

## Consequences

- Consumers of `DotNodeViewer.Core` depend solely on the clean, format-agnostic Unified Graph IR (`DotNodeViewer.Core.Ir`) and abstractions (`DotNodeViewer.Core.Abstractions`).
- Internal format readers can evolve or swap underlying binary decoding strategies without breaking binary or source compatibility.

