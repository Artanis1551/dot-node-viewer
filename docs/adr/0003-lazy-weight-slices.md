# ADR 0003: Zero-Allocation Topology Scanning & Lazy Weight Slices

- **Status:** Accepted
- **Date:** 2026-10-23
- **Stage:** S00

## Context

Neural network model files routinely range from a few megabytes to multiple gigabytes, where >95% of the file size consists of raw tensor weight buffers. `dot-node-viewer` targets both native desktop (`DotNodeViewer.Desktop`) and browser WebAssembly (`DotNodeViewer.Browser`), where 32-bit WASM address space and browser tab memory limits prohibit loading entire model files or weight arrays onto the managed heap during graph extraction.

## Decision

1. **Ban Eager Full-File Buffering and Weight Allocation:**
   `File.ReadAllBytes`, `Stream.ToArray()` on whole model streams, and allocating `byte[]`/`float[]` arrays for tensor weights during `IModelFormatReader.ReadAsync` are strictly prohibited.
2. **Record `TensorDataSlice` Metadata Only:**
   When a format reader encounters an inline tensor payload, it records a lightweight `TensorDataSlice(StreamOffset, ByteLength, DataType, Encoding)` attached to the `TensorEdge` (or `AttributeValue.Bytes`) and seeks past the payload bytes in the underlying `Stream`.
3. **External Weight References (`ExternalTensorDataReference`):**
   When weights live in external shard files (e.g., ONNX external data or `.pt2` external constants), the parser records `ExternalTensorDataReference(RelativePath, Offset, Length, DataType)` without automatically opening additional files from disk.
4. **On-Demand Reading via `TensorDataLoader`:**
   Raw weight bytes are read and decoded only when explicitly requested by the user (for example, in the Stage S11 tensor weight inspector) via `TensorDataLoader` into caller-supplied or pooled buffers.

## Consequences

- Initial model parsing scales with graph topology size (number of nodes, edges, and metadata attributes) rather than total weight byte size.
- Multi-gigabyte models can be inspected in microseconds/milliseconds with minimal GC heap pressure.

