# Domain Glossary (`dot-node-viewer`)

This glossary defines the ubiquitous language across `DotNodeViewer.Core`, `DotNodeViewer.Layout`, and `DotNodeViewer.App`. Use these exact terms in code, XML documentation, ADRs, and UI viewmodels.

---

## Core Domain Terms

| Term | Definition | Core Type / Location |
|---|---|---|
| **Model Graph** | The top-level immutable representation of a parsed neural network file, containing format metadata (`ModelFormatInfo`), producer information, one or more subgraphs, and non-fatal parse diagnostics. | `ModelGraph` (`DotNodeViewer.Core.Ir`) |
| **Subgraph** | A directed computational graph (or module hierarchy) consisting of operator nodes, tensor edges, and graph-level boundary ports. `Subgraphs[0]` (also accessible via `PrimarySubgraph`) is always the model's primary entry graph. | `Subgraph` (`DotNodeViewer.Core.Ir`) |
| **Graph Node** | A single operator, layer, or lowered hardware delegate invocation in a subgraph, with named input/output ports and strongly typed hyperparameter attributes. | `GraphNode` (`DotNodeViewer.Core.Ir`) |
| **Operator Info** | Semantic metadata describing a node's operation: operator name (e.g., `Conv`, `CONV_2D`, `aten::conv2d.out`), optional domain/namespace, opset version, and semantic `OperatorCategory`. | `OperatorInfo` (`DotNodeViewer.Core.Ir`) |
| **Operator Category** | Format-agnostic semantic classification (`Layer`, `Activation`, `Normalization`, `Pooling`, `Quantization`, `Shape`, `Tensor`, `Constant`, `ControlFlow`, `Data`, `Custom`, `Unknown`) used by the visualizer and layout engine for consistent categorization and color-coding. | `OperatorCategory` (`DotNodeViewer.Core.Ir`) |
| **Node Port** | A named input or output slot on a `GraphNode` (e.g., `X`, `W`, `B`, `Y`) referencing one or more connected `TensorEdge`s (supporting variadic arguments). | `NodePort` (`DotNodeViewer.Core.Ir`) |
| **Graph Port** | A named external input or output boundary port of a `Subgraph`. | `GraphPort` (`DotNodeViewer.Core.Ir`) |
| **Tensor Edge** | A named tensor flowing between nodes or attached as a constant weight, carrying a `TensorDescriptor`, `TensorKind`, and optional `TensorDataSource`. | `TensorEdge` (`DotNodeViewer.Core.Ir`) |
| **Tensor Kind** | Categorization distinguishing runtime activations (`Activation`, `GraphInput`, `GraphOutput`) from static learned parameters (`Initializer`, `Constant`), allowing visualizers to collapse static weights into compact node rows. | `TensorKind` (`DotNodeViewer.Core.Ir`) |
| **Tensor Descriptor** | Precision and dimension definition for a tensor, including primitive element `DataType`, optional `TensorShape`, optional `QuantizationInfo`, and optional memory layout tag. | `TensorDescriptor` (`DotNodeViewer.Core.Ir`) |
| **Tensor Shape & Dimension** | An ordered list of `TensorDimension` instances (`Fixed(long)`, `Symbolic(string)`, or `Dynamic`). A scalar has `Rank == 0` (empty dimensions); an unranked tensor has `Shape == null`. | `TensorShape`, `TensorDimension` (`DotNodeViewer.Core.Ir`) |
| **Tensor Data Slice** | A lightweight, zero-allocation reference (`StreamOffset`, `ByteLength`, `DataType`, `Encoding`) into the model stream. Raw weights are never loaded onto the managed heap during initial graph extraction. | `TensorDataSlice : TensorDataSource` (`DotNodeViewer.Core.Ir`) |
| **External Tensor Data Reference** | Location metadata pointing to weights stored in an external shard file (`RelativePath`, `Offset`, `Length`, `DataType`), never opened automatically during stream parsing. | `ExternalTensorDataReference : TensorDataSource` (`DotNodeViewer.Core.Ir`) |
| **Quantization Info** | Linear affine (`AffineQuantization`) or block-wise (`BlockQuantization`) quantization parameters attached to a `TensorDescriptor`. | `QuantizationInfo` (`DotNodeViewer.Core.Ir`) |
| **Model Probe** | A non-destructive inspection handle over a seekable `Stream` exposing the first 64 header bytes, optional file name, and lazy central-directory ZIP entries while guaranteeing `stream.Position` is restored. | `ModelProbe` (`DotNodeViewer.Core.Abstractions`) |
| **Parse Diagnostic** | A non-fatal warning or informational note emitted during parsing (e.g., when a format reader encounters unsupported metadata or recovers from a minor non-fatal framing inconsistency). | `ParseDiagnostic` (`DotNodeViewer.Core.Ir`) |
| **Golden Topology Snapshot** | A committed deterministic JSON baseline file (`<model-id>.expected.json`) capturing format info, subgraph counts, node counts, operator histograms, and input/output tensor shapes for regression verification. | `tests/DotNodeViewer.Core.Tests/Fixtures/<format>/*.expected.json` |

---

## Layout & Visualization Terms

| Term | Definition | Layout / App Location |
|---|---|---|
| **Sugiyama Layout Engine** | The four-phase layered graph layout implementation in `DotNodeViewer.Layout` executing cycle breaking (iterative DFS), layer assignment (Kahn's longest-path + tight-tree slack), crossing minimization (barycenter sweeps), and coordinate assignment (Brandes–Köpf alignment). | `SugiyamaLayoutEngine` (`DotNodeViewer.Layout`) |
| **Layout Graph** | Abstract graph representation (`LayoutGraph`, `LayoutNode`, `LayoutEdge`, `LayoutPort`) defining bounding boxes and connectivity before geometric positioning. | `DotNodeViewer.Layout` |
| **Positioned Graph** | The immutable geometric output of the layout engine (`PositionedGraph`, `PositionedNode`, `PositionedEdge`, `PositionedPort`) containing final X/Y coordinates, port anchor offsets, and cubic Bézier / polyline routing points. | `DotNodeViewer.Layout` |
| **Collapsed Parameter Slot** | A compact row inside an operator node card representing a static initializer or constant tensor (e.g., convolution weights or bias), avoiding excessive graph clutter. | `ModelGraphLayoutAdapter` (`DotNodeViewer.Layout`) |
| **Fused Activation Badge** | An inline badge row rendered within an operator node card indicating a post-operation fused activation (such as `RELU` or `RELU6` on a `CONV_2D` layer). | `VisualNode.FusedActivationBadge` (`DotNodeViewer.Layout`) |
| **Text Measurer Seam** | A UI-independent interface (`ITextMeasurer`, `MonospaceTextMeasurer`) enabling accurate node card and label width calculations in `DotNodeViewer.Layout` without coupling to UI graphics packages. | `ITextMeasurer` (`DotNodeViewer.Layout`) |
