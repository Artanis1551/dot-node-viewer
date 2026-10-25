# API Reference (`dot-node-viewer`)

Welcome to the technical API reference for `dot-node-viewer`. The repository provides reusable, high-performance, NativeAOT- and WebAssembly-ready .NET 10 libraries for extracting, analyzing, and laying out computational neural network graphs.

The library ecosystem is strictly decoupled into pure C# components with no dependencies on Avalonia, SkiaSharp, or platform windowing systems:

- **`DotNodeViewer.Core`**: Provides the format detection engine, streaming binary parsers, on-demand weight slicers, and the pure immutable record Unified Graph Intermediate Representation (IR).
- **`DotNodeViewer.Layout`**: Provides a pure C# layered graph layout engine based on the Sugiyama / Dagre framework, converting Unified Graph IR topologies into positioned visual geometries with spatial indexing.

```
                    ┌─────────────────────────┐
                    │   DotNodeViewer.Core    │
                    │  (IR, Parsers, Slicing) │
                    └────────────┬────────────┘
                                 │
                                 ▼
                    ┌─────────────────────────┐
                    │   DotNodeViewer.Layout  │
                    │ (Sugiyama Layered DAG)  │
                    └────────────┬────────────┘
                                 │
            ┌────────────────────┴────────────────────┐
            │                                         │
            ▼                                         ▼
┌─────────────────────────┐               ┌─────────────────────────┐
│   DotNodeViewer.App     │               │   DotNodeViewer.Cli     │
│   (Avalonia Desktop &   │               │   (Inspection, Diff,    │
│    WebAssembly Heads)   │               │    Golden Snapshots)    │
└─────────────────────────┘               └─────────────────────────┘
```

---

## Architectural Principles & Contracts

1. **Immutable Unified Graph IR:** All extracted topologies are mapped to pure immutable C# records in <xref:DotNodeViewer.Core.Ir>. Public APIs never leak third-party or format-specific Protobuf, FlatBuffers, HDF5, Pickle, or JSON serialization classes.
2. **Zero Eager Weight Allocations:** Parsing a model never allocates large `byte[]` or `float[]` weight payloads on the managed heap. Weight payloads remain on disk or within the underlying stream, indexed via lazy <xref:DotNodeViewer.Core.Ir.TensorDataSlice> structs and read on demand via <xref:DotNodeViewer.Core.Abstractions.TensorDataLoader>.
3. **Closed Discriminated Unions:** Polymorphic structures (attributes, tensor dimensions, quantization metadata, tensor data sources) are implemented as closed `abstract record` class hierarchies with `sealed` records, enabling exhaustive pattern-matching without untyped casting.
4. **NativeAOT & WebAssembly Safe:** The core and layout engines pass `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>` with zero trimming warnings. Deserialization relies on zero-allocation span readers, `ReadOnlySpan<byte>`, `BinaryPrimitives`, and source-generated JSON contexts.
5. **Thread Safety & Async Hygiene:** Streaming ingestion contracts accept `CancellationToken`, use `.ConfigureAwait(false)` throughout, and emit thread-safe immutable structures that can be read concurrently across visualizer or layout threads.

---

## 1. Model Format Detection & Ingestion Pipeline

Namespace: <xref:DotNodeViewer.Core.Abstractions>

The ingestion pipeline detects neural network formats from raw byte streams without reading entire files into memory. Detection inspects magic bytes, file headers, and container metadata via <xref:DotNodeViewer.Core.Abstractions.ModelProbe>.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Abstractions.IModelFormatDetector> | Interface | Sniffs headers and stream prefixes to detect supported formats and confidence. |
| <xref:DotNodeViewer.Core.Abstractions.IModelFormatReader> | Interface | Streaming reader extracting the <xref:DotNodeViewer.Core.Ir.ModelGraph> from a stream. |
| <xref:DotNodeViewer.Core.Abstractions.ModelFormatRegistry> | Class | Thread-safe central registry managing detectors and streaming readers. |
| <xref:DotNodeViewer.Core.Abstractions.ModelProbe> | Class | Zero-allocation byte inspector detecting formats from streams or memory buffers. |
| <xref:DotNodeViewer.Core.Abstractions.ModelReadOptions> | Class | Configuration options for reading models (diagnostic limits, delegate expansion). |
| <xref:DotNodeViewer.Core.Abstractions.ModelParseException> | Class | Structured exception containing byte offset, node context, and severity. |
| <xref:DotNodeViewer.Core.Ir.ParseDiagnostic> | Record | Non-fatal anomaly diagnostic recorded during model parsing. |
| <xref:DotNodeViewer.Core.Ir.DiagnosticSeverity> | Enum | Diagnostic severity level: `Information`, `Warning`, or `Error`. |

### Example: Probing & Reading a Model Stream

```csharp
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

// 1. Detect format from stream prefix
await using FileStream stream = File.OpenRead("model.onnx");
ModelFormatInfo? formatInfo = await ModelProbe.ProbeAsync(stream);

if (formatInfo is not null && ModelFormatRegistry.Default.TryGetReader(formatInfo.Id, out var reader))
{
    // 2. Stream topology with options
    ModelReadOptions options = new() { MaxDiagnostics = 50 };
    ModelGraph graph = await reader.ReadAsync(stream, options);

    Console.WriteLine($"Loaded {graph.Format.DisplayName} model with {graph.Subgraphs.Count} subgraphs.");
}
```

---

## 2. Unified Graph Intermediate Representation (IR)

Namespace: <xref:DotNodeViewer.Core.Ir>

The Unified Graph IR represents computational graphs across machine learning frameworks (ONNX, TensorFlow Lite, ExecuTorch, PyTorch TorchScript/ExportedProgram, Keras 3/H5) in a normalized, framework-agnostic schema.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Ir.ModelGraph> | Record | Root graph container holding subgraphs, format metadata, and telemetry. |
| <xref:DotNodeViewer.Core.Ir.Subgraph> | Record | Scoped computational graph holding nodes, ports, and tensor edges. |
| <xref:DotNodeViewer.Core.Abstractions.GraphIndex> | Class | High-speed $O(1)$ indexed lookup cache for nodes, ports, and tensor edges. |
| <xref:DotNodeViewer.Core.Ir.GraphNode> | Record | Operator node containing execution metadata, inputs, outputs, and attributes. |
| <xref:DotNodeViewer.Core.Ir.GraphPort> | Record | Subgraph-level boundary input or output port. |
| <xref:DotNodeViewer.Core.Ir.NodePort> | Record | Operator-level input or output port connecting tensor edges. |
| <xref:DotNodeViewer.Core.Ir.TensorEdge> | Record | Directed dataflow edge connecting source and destination ports. |
| <xref:DotNodeViewer.Core.Ir.TensorKind> | Enum | Classification: `Activation`, `Initializer` (weight), `Constant`, or `Custom`. |
| <xref:DotNodeViewer.Core.Ir.OperatorInfo> | Record | Operator specification metadata including name, category, and domain. |
| <xref:DotNodeViewer.Core.Ir.OperatorCategory> | Enum | Semantic category (`Layer`, `Activation`, `Normalization`, `Pooling`, etc.). |
| <xref:DotNodeViewer.Core.Ir.ModelFormatId> | Record | Unique strongly-typed identifier for registered formats (e.g., `onnx`, `tflite`). |
| <xref:DotNodeViewer.Core.Ir.ModelFormatInfo> | Record | Display metadata, primary file extensions, and descriptions for formats. |

### Example: Fast $O(1)$ Graph Navigation via `GraphIndex`

```csharp
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

// Wrap subgraph in an indexed cache for fast queries
GraphIndex index = new(subgraph);

if (index.TryGetNode("conv_0", out GraphNode? node))
{
    // Retrieve all immediate predecessor nodes
    IReadOnlyList<GraphNode> predecessors = index.GetPredecessors(node);
    
    // Retrieve incoming tensor edges feeding this operator
    IReadOnlyList<TensorEdge> incomingEdges = index.GetIncomingEdges(node);
    
    Console.WriteLine($"Node {node.Name} has {predecessors.Count} predecessors.");
}
```

---

## 3. Tensor Typing, Dimensionality, & Shapes

Namespace: <xref:DotNodeViewer.Core.Ir>

Tensor shapes and data types are represented using strongly typed records supporting static, dynamic (unbound batch size), and symbolic dimension identifiers.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Ir.TensorDescriptor> | Record | Full tensor specification including data type, shape, and weight slice. |
| <xref:DotNodeViewer.Core.Ir.DataType> | Enum | Numerical element types (`Float32`, `Float16`, `BFloat16`, `Int8`, `UInt8`, etc.). |
| <xref:DotNodeViewer.Core.Ir.TensorShape> | Record | Rank descriptor holding an immutable list of <xref:DotNodeViewer.Core.Ir.TensorDimension>s. |
| <xref:DotNodeViewer.Core.Ir.TensorDimension> | Abstract Record | Closed discriminated union base for tensor dimensions. |
| <xref:DotNodeViewer.Core.Ir.TensorDimension.Fixed> | Record | Static dimension with known integer size (e.g. `224`). |
| <xref:DotNodeViewer.Core.Ir.TensorDimension.Dynamic> | Record | Dynamic unbound dimension with optional minimum and maximum bounds. |
| <xref:DotNodeViewer.Core.Ir.TensorDimension.Symbolic> | Record | Named symbolic dimension variable (e.g. `batch`, `seq_len`). |

### Example: Pattern Matching on Tensor Dimensions

```csharp
using DotNodeViewer.Core.Ir;

string FormatDimension(TensorDimension dim) => dim switch
{
    TensorDimension.Fixed f => f.Size.ToString(),
    TensorDimension.Symbolic s => s.Symbol,
    TensorDimension.Dynamic d when d.Min.HasValue && d.Max.HasValue => $"{d.Min}..{d.Max}",
    TensorDimension.Dynamic => "?",
    _ => "unknown"
};
```

---

## 4. Dynamic Operator Attributes (`AttributeValue` Union)

Namespace: <xref:DotNodeViewer.Core.Ir>

Operators in neural network formats contain heterogeneous parameters (convolution strides, dilation, padding, epsilon, axes, activation strings, and nested subgraphs). The `AttributeValue` closed discriminated union represents these values without untyped `object` casting or boxing allocations.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Ir.NodeAttribute> | Record | Key-value attribute descriptor on a <xref:DotNodeViewer.Core.Ir.GraphNode>. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue> | Abstract Record | Closed discriminated union base class for all attribute values. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Bool> | Record | Boolean parameter flag. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Int64> | Record | Signed 64-bit integer parameter. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Float64> | Record | 64-bit floating-point parameter. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.String> | Record | UTF-8 string parameter (e.g., activation name). |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Bytes> | Record | Raw binary parameter buffer. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Int64List> | Record | List of 64-bit integers (e.g., strides `[1, 1]`, kernel shape `[3, 3]`). |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Float64List> | Record | List of 64-bit floating-point numbers. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.StringList> | Record | List of strings. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.DataTypeValue> | Record | Data type specification parameter. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.ShapeValue> | Record | Tensor shape specification parameter. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Tensor> | Record | Embedded constant tensor descriptor and weight slice. |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.Graph> | Record | Nested computational subgraph (used in `If`, `Loop`, or conditional blocks). |
| <xref:DotNodeViewer.Core.Ir.AttributeValue.GraphList> | Record | Collection of nested computational subgraphs. |

---

## 5. Lazy Weight Slicing & On-Demand Data Loader

Namespace: <xref:DotNodeViewer.Core.Ir> & <xref:DotNodeViewer.Core.Abstractions>

Neural network weights and parameters can consume hundreds of megabytes or gigabytes of memory. `dot-node-viewer` records exact byte offsets and stream lengths into <xref:DotNodeViewer.Core.Ir.TensorDataSlice> instances during format scanning. Numerical payloads are only read from the model file when requested by the visualizer or inspector.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Ir.TensorDataSource> | Abstract Record | Closed union base for weight backing stores. |
| <xref:DotNodeViewer.Core.Ir.TensorDataSlice> | Record | Embedded weight slice descriptor holding `StreamOffset`, `ByteLength`, and `DataType`. |
| <xref:DotNodeViewer.Core.Ir.ExternalTensorDataReference> | Record | Reference to an external sidecar binary shard (e.g. ONNX external data). |
| <xref:DotNodeViewer.Core.Ir.TensorDataEncoding> | Enum | Payload encoding: `RawBinary`, `ZipDeflate`, or `Custom`. |
| <xref:DotNodeViewer.Core.Abstractions.TensorDataLoader> | Class | Streaming decoder decoding floating-point values and statistics on demand. |

### Example: On-Demand Tensor Decoding

```csharp
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

if (tensorEdge.Data is TensorDataSlice slice)
{
    await using FileStream stream = File.OpenRead("model.tflite");
    
    // Allocate bounded destination buffer (e.g., first 1024 elements)
    int elementCount = Math.Min(1024, (int)(slice.ByteLength / sizeof(float)));
    float[] values = new float[elementCount];
    
    int read = TensorDataLoader.ReadFloats(stream, slice, values);
    Console.WriteLine($"Sampled {read} floating-point weights from byte offset 0x{slice.StreamOffset:X8}.");
}
```

---

## 6. Quantization Metadata Taxonomy

Namespace: <xref:DotNodeViewer.Core.Ir>

Modern edge and LLM models employ uniform affine quantization (per-tensor or per-channel) as well as block-floating-point and grouped quantization schemes.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Ir.QuantizationInfo> | Abstract Record | Closed discriminated union base for quantization schemes. |
| <xref:DotNodeViewer.Core.Ir.AffineQuantization> | Record | Uniform affine quantization with scale array, zero-point array, and quantization axis. |
| <xref:DotNodeViewer.Core.Ir.BlockQuantization> | Record | Grouped/block quantization with block dimensions, scales, and codebook metadata. |

### Example: Dequantizing INT8 Values

```csharp
using DotNodeViewer.Core.Ir;

double Dequantize(sbyte rawValue, AffineQuantization affine, int channelIndex = 0)
{
    double scale = affine.Scales[channelIndex];
    long zeroPoint = affine.ZeroPoints.Count > channelIndex ? affine.ZeroPoints[channelIndex] : 0;
    return (rawValue - zeroPoint) * scale;
}
```

---

## 7. Delegate Subgraph Expansion Pipeline

Namespace: <xref:DotNodeViewer.Core.Abstractions>

Hardware accelerator partitions (e.g., ExecuTorch XNNPACK/Vulkan delegates, Arm Ethos-U NPU partitions, or TensorFlow Lite custom kernels) often encapsulate internal subgraphs as opaque command blobs. The delegate expansion pipeline allows registered expanders to unwrap and synthesize internal topologies.

### Core Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Core.Abstractions.IDelegateSubgraphExpander> | Interface | Contract for unpacking opaque delegate payloads into interactive subgraphs. |
| <xref:DotNodeViewer.Core.Abstractions.DelegateSubgraphExpanderRegistry> | Class | Thread-safe registry mapping delegate backends to their respective expanders. |

---

## 8. Layered Graph Layout Engine

Namespace: <xref:DotNodeViewer.Layout>

`DotNodeViewer.Layout` provides a pure C# Sugiyama-style layered directed acyclic graph (DAG) layout engine. It transforms unpositioned <xref:DotNodeViewer.Layout.LayoutGraph>s into pixel-positioned <xref:DotNodeViewer.Layout.PositionedGraph>s with cubic Bézier edge routing, port alignment, and spatial viewport indexing.

### Layout Pipeline & Geometry Types

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Layout.IGraphLayoutEngine> | Interface | Engine abstraction computing layered graph layouts. |
| <xref:DotNodeViewer.Layout.SugiyamaLayoutEngine> | Class | Production layered layout engine with crossing reduction and coordinate assignment. |
| <xref:DotNodeViewer.Layout.LayoutOptions> | Class | Layout configuration (direction, node separation, rank separation, edge padding). |
| <xref:DotNodeViewer.Layout.LayoutDirection> | Enum | Direction: `TopToBottom`, `BottomToTop`, `LeftToRight`, `RightToLeft`. |
| <xref:DotNodeViewer.Layout.LayoutGraph> | Class | Unpositioned graph input for the layout engine. |
| <xref:DotNodeViewer.Layout.LayoutNode> | Class | Unpositioned node input with width, height, and port definitions. |
| <xref:DotNodeViewer.Layout.LayoutPort> | Class | Unpositioned port on a layout node. |
| <xref:DotNodeViewer.Layout.LayoutEdge> | Class | Unpositioned directed edge connecting source and target ports. |
| <xref:DotNodeViewer.Layout.PositionedGraph> | Class | Fully positioned layout result holding positioned nodes, edges, and bounds. |
| <xref:DotNodeViewer.Layout.PositionedNode> | Class | Positioned node with concrete $(X, Y)$ coordinates and dimensions. |
| <xref:DotNodeViewer.Layout.PositionedPort> | Class | Positioned port with world-space attachment coordinates. |
| <xref:DotNodeViewer.Layout.PositionedEdge> | Class | Positioned edge containing routed cubic Bézier segments and waypoints. |
| <xref:DotNodeViewer.Layout.CubicBezierSegment> | Struct | Smooth cubic Bézier spline segment $(P_0, P_1, P_2, P_3)$ for edge rendering. |
| <xref:DotNodeViewer.Layout.BoundingBox> | Struct | Axis-aligned bounding box $(X, Y, \text{Width}, \text{Height})$ with intersection tests. |
| <xref:DotNodeViewer.Layout.LayoutPoint> | Struct | 2D $(X, Y)$ coordinate point. |

### Visual Subgraph Composition & Parameter Collapsing

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Layout.VisualSubgraphLayout> | Class | High-level visual layout container managing visual nodes, edges, and fast lookups. |
| <xref:DotNodeViewer.Layout.VisualNode> | Class | Visual operator card containing title, subtitle, category styling, and parameter slots. |
| <xref:DotNodeViewer.Layout.VisualEdge> | Class | Visual dataflow curve connecting source and target nodes with state indicators. |
| <xref:DotNodeViewer.Layout.VisualNodeKind> | Enum | Classification: `Operator`, `GraphInput`, `GraphOutput`, or `Cluster`. |
| <xref:DotNodeViewer.Layout.CollapsedParameterSlot> | Record | Collapsed static parameter row rendered directly within an operator card. |
| <xref:DotNodeViewer.Layout.ModelGraphLayoutAdapter> | Class | Adapter transforming <xref:DotNodeViewer.Core.Ir.ModelGraph> into <xref:DotNodeViewer.Layout.LayoutGraph>. |

### Viewport Spatial Culling

| Type | Kind | Description |
|---|---|---|
| <xref:DotNodeViewer.Layout.SpatialGridIndex> | Class | 2D uniform grid index accelerating viewport culling for massive graphs ($10{,}000+$ nodes). |
| <xref:DotNodeViewer.Layout.VisibleLayoutElements> | Struct | Result of a spatial grid query holding visible nodes and edges for the viewport. |

### Example: Running Layout & Querying Viewport Elements

```csharp
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

// 1. Convert IR Subgraph into an unpositioned LayoutGraph
ITextMeasurer measurer = new MonospaceTextMeasurer(charWidth: 7.2, charHeight: 14.0);
LayoutGraph unpositioned = ModelGraphLayoutAdapter.CreateLayoutGraph(subgraph, measurer);

// 2. Execute Sugiyama layout
SugiyamaLayoutEngine engine = new();
LayoutOptions options = new()
{
    Direction = LayoutDirection.TopToBottom,
    NodeSeparation = 36.0,
    RankSeparation = 48.0
};
PositionedGraph positioned = engine.Layout(unpositioned, options);

// 3. Populate spatial index for 60 FPS viewport culling
SpatialGridIndex spatialIndex = new(cellSize: 256.0);
spatialIndex.Build(positioned.Nodes, positioned.Edges);

// 4. Query only elements visible in current camera viewport
BoundingBox cameraViewport = new(x: 100, y: 200, width: 1280, height: 720);
VisibleLayoutElements visible = spatialIndex.QueryVisibleElements(cameraViewport);

Console.WriteLine($"Rendering {visible.VisibleNodes.Count} nodes within visible canvas bounds.");
```

---

## Summary of Namespaces

- **<xref:DotNodeViewer.Core>** — Root assembly information and runtime version metadata.
- **<xref:DotNodeViewer.Core.Abstractions>** — Format detectors, streaming readers, registries, probes, and on-demand weight loaders.
- **<xref:DotNodeViewer.Core.Ir>** — Pure immutable records for graphs, subgraphs, nodes, ports, edges, shapes, attributes, and quantization.
- **<xref:DotNodeViewer.Layout>** — Pure C# Sugiyama layered graph layout engine, cubic Bézier edge routing, visual adapters, and spatial viewport grid index.
