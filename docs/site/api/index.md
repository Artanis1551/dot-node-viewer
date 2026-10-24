# API Reference (`dot-node-viewer`)

This section contains the generated C# API documentation for the reusable libraries in `dot-node-viewer`:

## Namespaces & Core Types

### [DotNodeViewer.Core](xref:DotNodeViewer.Core)
Root namespace containing library versioning and metadata:
- [CoreLibraryInfo](xref:DotNodeViewer.Core.CoreLibraryInfo) — Assembly version and runtime information.

### [DotNodeViewer.Core.Abstractions](xref:DotNodeViewer.Core.Abstractions)
Core streaming parser, format detection, and weight loading contracts:
- [IModelFormatDetector](xref:DotNodeViewer.Core.Abstractions.IModelFormatDetector) — Header and magic-byte format detection contract.
- [IModelFormatReader](xref:DotNodeViewer.Core.Abstractions.IModelFormatReader) — Streaming model reader extracting the Unified Graph IR.
- [ModelFormatRegistry](xref:DotNodeViewer.Core.Abstractions.ModelFormatRegistry) — Central registry for format detectors and readers.
- [ModelProbe](xref:DotNodeViewer.Core.Abstractions.ModelProbe) — Zero-allocation metadata and format sniffer.
- [TensorDataLoader](xref:DotNodeViewer.Core.Abstractions.TensorDataLoader) — On-demand tensor weight payload loader.
- [GraphIndex](xref:DotNodeViewer.Core.Abstractions.GraphIndex) — Fast O(1) indexed lookup for nodes, ports, and tensor edges.
- [ModelParseException](xref:DotNodeViewer.Core.Abstractions.ModelParseException) — Structured exception with byte offset and node context.

### [DotNodeViewer.Core.Ir](xref:DotNodeViewer.Core.Ir)
Pure immutable record Unified Graph Intermediate Representation:
- [ModelGraph](xref:DotNodeViewer.Core.Ir.ModelGraph) — Root container for subgraphs and model metadata.
- [Subgraph](xref:DotNodeViewer.Core.Ir.Subgraph) — Scoped computational subgraph of nodes and ports.
- [GraphNode](xref:DotNodeViewer.Core.Ir.GraphNode) — Operator node with inputs, outputs, and attributes.
- [TensorEdge](xref:DotNodeViewer.Core.Ir.TensorEdge) — Directed edge connecting source and target ports.
- [TensorDescriptor](xref:DotNodeViewer.Core.Ir.TensorDescriptor) — Tensor metadata, shape, data type, and lazy weight slice.
- [TensorShape](xref:DotNodeViewer.Core.Ir.TensorShape) — Tensor rank and dimension descriptors.
- [QuantizationInfo](xref:DotNodeViewer.Core.Ir.QuantizationInfo) — Affine or block quantization parameters.

### [DotNodeViewer.Layout](xref:DotNodeViewer.Layout)
Pure C# Sugiyama layered graph layout engine:
- [IGraphLayoutEngine](xref:DotNodeViewer.Layout.IGraphLayoutEngine) — Primary layout engine abstraction.
- [SugiyamaLayoutEngine](xref:DotNodeViewer.Layout.SugiyamaLayoutEngine) — Layered Sugiyama directed-graph layout implementation.
- [LayoutGraph](xref:DotNodeViewer.Layout.LayoutGraph) — Unpositioned graph input for the layout engine.
- [PositionedGraph](xref:DotNodeViewer.Layout.PositionedGraph) — Fully positioned graph layout result.
- [ModelGraphLayoutAdapter](xref:DotNodeViewer.Layout.ModelGraphLayoutAdapter) — Adapter converting `ModelGraph` to `LayoutGraph`.

