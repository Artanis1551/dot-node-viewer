---
name: port-model-format
description: >
  Workflow and architectural guardrails for adding or porting a neural network model format parser
  (such as ONNX, TFLite, Safetensors, ExecuTorch, GGUF, or CoreML) into the core .NET graph extraction
  library. Use whenever implementing a new format reader, binary schema decoder, magic-byte sniffer,
  or mapping format-specific operators and tensors into the Unified Graph IR.
---

# Porting a Neural Network Model Format (.NET Core Library)

Follow this workflow whenever adding or updating a model format parser. The primary goal is a **reusable, high-performance, AOT-compatible .NET NuGet library** with zero UI dependencies that runs equally well in desktop apps, CLI tools, and browser WebAssembly (`Avalonia.Browser`) environments.

## 1. Non-Negotiable Architectural Guardrails

1. **Zero UI or Layout Leakage**
   - The core parser library MUST NOT reference Avalonia, SkiaSharp, or any rendering/layout geometry types (no X/Y coordinates, colors, or Sugiyama layout state in the parser).
   - Output strictly maps to the **Unified Graph Intermediate Representation (IR)**: `ModelGraph`, `Subgraph`, `GraphNode`, `TensorEdge`, `NodeAttribute`, and `TensorShape`.

2. **Strict Encapsulation of Format Schemas**
   - Format-specific deserialization types (generated Protobuf classes, FlatBuffers structs, JSON header DTOs) MUST be marked `internal`.
   - Never expose format-specific types in public method signatures, properties, or exceptions. Wrap format errors in a domain-specific `ModelParseException` with the file offset or node name for context.

3. **Zero-Allocation Topology Scanning & Lazy Weights (WASM-Safe)**
   - **BANNED:** `File.ReadAllBytes`, `Stream.ToArray()` on full model streams, or eagerly allocating `byte[]`/`float[]` arrays for raw tensor weights during graph extraction.
   - Neural network files can exceed several gigabytes, and WebAssembly tabs have strict memory ceilings.
   - During initial parsing, extract **topology, shapes, data types, and attributes only**. When encountering raw weight buffers, record a lightweight `TensorDataSlice` (`StreamOffset`, `ByteLength`, `DataType`) and seek past the payload so weights are only read on demand when a user inspects a specific tensor.
   - Use `ReadOnlySpan<byte>`, `BinaryPrimitives`, and `ArrayPool<byte>.Shared` for header and magic-byte parsing.

4. **NativeAOT & WebAssembly Trimming Compatibility**
   - All parser code must be compatible with `<IsTrimmable>true</IsTrimmable>` and `<IsAotCompatible>true</IsAotCompatible>`.
   - Avoid unbounded runtime reflection (`Type.GetType`, `Activator.CreateInstance`, or untyped `JsonSerializer.Deserialize`). For JSON-based headers or metadata (e.g., Safetensors, CoreML metadata), always use a source-generated `JsonSerializerContext`.

5. **Library-Grade Async & Public API Hygiene**
   - All asynchronous stream reads must accept a `CancellationToken` and use `.ConfigureAwait(false)`.
   - All public types must be `sealed` unless explicitly designed for inheritance, use `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` for collections, and include XML documentation comments (`/// <summary>`).

---

## 2. Step-by-Step Implementation Workflow

### Step 1: Reference Analysis & Schema Inspection
1. Inspect how reference Netron (JavaScript) or the official format specification handles:
   - Container framing (magic bytes, endianness, version headers, zip/tar archive wrappers).
   - Graph hierarchy (single graph vs. multiple subgraphs/functions).
   - Operator metadata catalog (implicit inputs, activation fusions, category classification).
2. Read `references/ir-mapping-checklist.md` in this skill directory to verify how format-specific constructs map to our Unified Graph IR.

### Step 2: Format Detection (`IModelFormatDetector`)
1. Implement header sniffing using a small stack-allocated buffer (`Span<byte> header = stackalloc byte[64]`) without consuming or closing the caller's `Stream` (always reset `stream.Position` or use a non-destructive peek).
2. Prefer magic-byte signature verification over file extension checks, using file extensions only as a secondary tie-breaker when container formats overlap (e.g., Zip-based `.pt`, `.pte`, or `.mlpackage` files).

### Step 3: Streaming Parser Implementation (`IModelFormatReader`)
1. Implement the format reader in its own internal namespace (e.g., `NetronNet.Formats.<FormatName>`).
2. Decode the container structure:
   - **Binary/Header formats (Safetensors, GGUF):** Parse header length via `BinaryPrimitives`, deserialize header metadata using source-generated JSON or span readers, and calculate absolute byte offsets for tensor data.
   - **FlatBuffers (TFLite, ExecuTorch):** Read the buffer table offsets directly without copying tensor `uint8` vectors onto the managed heap.
   - **Protobuf (ONNX, TensorFlow):** Use `CodedInputStream` or custom tag-skipping readers to bypass `TensorProto.RawData` fields while capturing dimensions, `elem_type`, and external data locations.
3. Map nodes, initializers, inputs, outputs, and quantization parameters into the Unified Graph IR.

### Step 4: Operator Metadata & Category Tagging
1. Map operator types to standard semantic categories (`Layer`, `Activation`, `Normalization`, `Pooling`, `Quantization`, `Shape`, `Constant`, `ControlFlow`, `Custom`) so any downstream UI can color-code nodes automatically without hardcoding format-specific operator names.
2. Attach attribute types (`Int64`, `Float32`, `String`, `TensorShape`, `Subgraph`, `DataType`) using strongly typed discriminated unions or `NodeAttribute` records rather than raw `object` boxing.

### Step 5: Automated Unit & Snapshot Tests
1. Create a test class in the test project using **synthetic minimal binary fixtures** (constructed in memory via `MemoryStream` or compact byte arrays) rather than committing multi-megabyte model files to Git.
2. Include at least four tests for every new format:
   - **Magic-byte detection test:** Verifies `CanRead` returns `true` for valid headers and `false` for truncated or random bytes.
   - **Topology & shape extraction test:** Verifies inputs, outputs, node connections, and dynamic/symbolic dimensions (e.g., `["batch", 3, 224, 224]`).
   - **Lazy weight skipping test:** Verifies that parsing a model with a simulated weight buffer records the exact `StreamOffset` and `ByteLength` without allocating the weight payload.
   - **Corrupted input resilience test:** Verifies that truncated streams or invalid offsets throw `ModelParseException` cleanly instead of `IndexOutOfRangeException` or `OutOfMemoryException`.