# ADR 0002: Pure Immutable C# Record Unified Graph IR

- **Status:** Accepted
- **Date:** 2026-10-23
- **Stage:** S00

## Context

Every supported neural network format represents computational graphs differently (e.g., ONNX `GraphProto` with `NodeProto` and `ValueInfoProto`, TFLite `SubGraph` with integer tensor indices, PyTorch `ExportedProgram` JSON or TorchScript SSA graphs, Keras functional/sequential layer configs).

The layout engine (`DotNodeViewer.Layout`), visualizer (`DotNodeViewer.App`), and CLI inspection/snapshot tools (`DotNodeViewer.Cli`) require a single canonical representation of model topology, operator semantics, tensor shapes, quantization metadata, and attributes.

## Decision

1. **Pure C# Immutable Records (`DotNodeViewer.Core.Ir`):**
   We represent parsed models exclusively using `public sealed record` types and closed discriminated-union hierarchies (`public abstract record` with `public sealed record` cases) in `DotNodeViewer.Core.Ir`:
   - `ModelGraph`, `ModelFormatInfo`, `ModelFormatId`, `Subgraph`, `GraphNode`, `OperatorInfo`, `OperatorCategory`
   - `NodePort`, `GraphPort`, `TensorEdge`, `TensorKind`, `TensorDescriptor`, `TensorShape`, `TensorDimension`
   - `TensorDataSource` (`TensorDataSlice`, `ExternalTensorDataReference`)
   - `QuantizationInfo` (`AffineQuantization`, `BlockQuantization`)
   - `NodeAttribute`, `AttributeValue`, `DataType`, `ParseDiagnostic`
2. **No Secondary Serialization Schema for the IR:**
   We do **not** define a FlatBuffers or Protobuf schema for our own IR. The IR lives as pure, thread-safe, immutable .NET records in memory, with source-generated JSON used only by the CLI/test harness for human-readable golden topology snapshots (`*.expected.json`).
3. **Zero Layout or UI State in the IR:**
   `DotNodeViewer.Core.Ir` contains no X/Y coordinates, node dimensions, colors, or UI view state. Semantic classification is expressed via `OperatorCategory` and `TensorKind` so downstream layout and rendering layers can style and arrange graphs without format-specific coupling.

## Consequences

- Immutable records are safe to share across background parsing tasks, layout threads, and UI viewmodels without defensive copying.
- Closed record hierarchies enable exhaustive C# pattern matching (`switch` expressions) for tensor dimensions, attribute values, quantization schemes, and tensor data sources.

