# Domain Glossary (`dot-node-viewer`)

> [!NOTE]
> This glossary is published on the documentation website under [**Reference: Domain Glossary**](docs/site/articles/glossary.md).

This glossary defines the ubiquitous language across `DotNodeViewer.Core`, `DotNodeViewer.Layout`, and `DotNodeViewer.App`. Use these exact terms in code, XML documentation, ADRs, and UI viewmodels.

| Term | Definition | Core Type / Location |
|---|---|---|
| **Model Graph** | The top-level immutable representation of a parsed neural network file, containing format info, producer metadata, one or more subgraphs, and any non-fatal parse diagnostics. | `ModelGraph` (`DotNodeViewer.Core.Ir`) |
| **Subgraph** | A directed computational graph (or module hierarchy fallback) consisting of operator nodes, tensor edges, and graph-level input/output ports. `Subgraphs[0]` is always the primary subgraph. | `Subgraph` (`DotNodeViewer.Core.Ir`) |
| **Graph Node** | A single operator, layer, or custom delegate invocation in a subgraph, with named input/output ports and typed attributes. | `GraphNode` (`DotNodeViewer.Core.Ir`) |
| **Operator Info** | Metadata describing a node's operation: operator name (e.g., `Conv`, `CONV_2D`, `aten::conv2d.out`), domain/namespace, version, and semantic `OperatorCategory`. | `OperatorInfo`, `OperatorCategory` (`DotNodeViewer.Core.Ir`) |
| **Operator Category** | Format-agnostic semantic classification (`Layer`, `Activation`, `Normalization`, `Pooling`, `Quantization`, `Shape`, `Tensor`, `Constant`, `ControlFlow`, `Data`, `Custom`, `Unknown`) used by the visualizer for consistent color coding. | `OperatorCategory` (`DotNodeViewer.Core.Ir`) |
| **Node Port** | A named input or output slot on a `GraphNode` (e.g., `X`, `W`, `B`, `Y`) holding one or more `TensorEdge` connections (supporting variadic ports). | `NodePort` (`DotNodeViewer.Core.Ir`) |
| **Graph Port** | A named external input or output of a `Subgraph`. | `GraphPort` (`DotNodeViewer.Core.Ir`) |
| **Tensor Edge** | A named tensor flowing between nodes or attached as a constant/initializer weight, carrying a `TensorDescriptor`, `TensorKind`, and optional `TensorDataSource`. | `TensorEdge` (`DotNodeViewer.Core.Ir`) |
| **Tensor Kind** | Distinguishes runtime activations (`Activation`, `GraphInput`, `GraphOutput`) from static weights (`Initializer`, `Constant`) so layout can collapse initializers into compact node rows instead of full graph nodes. | `TensorKind` (`DotNodeViewer.Core.Ir`) |
| **Tensor Shape & Dimension** | An ordered list of `TensorDimension`s (`Fixed(long)`, `Symbolic(string)`, or `Dynamic`). A scalar has `Rank == 0` (`Dimensions` empty); an unranked tensor has `Shape == null`. | `TensorShape`, `TensorDimension` (`DotNodeViewer.Core.Ir`) |
| **Tensor Data Slice** | A lazy, zero-allocation byte range reference (`StreamOffset`, `ByteLength`, `DataType`, `Encoding`) into the underlying model stream. Never holds a `byte[]` payload on the heap. | `TensorDataSlice : TensorDataSource` (`DotNodeViewer.Core.Ir`) |
| **External Tensor Data Reference** | Metadata pointing to weights stored in an external shard file (`RelativePath`, `Offset`, `Length`, `DataType`), never opened automatically by the stream parser. | `ExternalTensorDataReference : TensorDataSource` (`DotNodeViewer.Core.Ir`) |
| **Quantization Info** | Per-tensor, per-channel (`AffineQuantization`), or block-wise (`BlockQuantization`) quantization parameters attached to a `TensorDescriptor`. | `QuantizationInfo` (`DotNodeViewer.Core.Ir`) |
| **Model Probe** | A non-destructive inspection handle over a seekable `Stream` exposing the first 64 header bytes, optional file name, and lazy central-directory ZIP entries while guaranteeing `stream.Position` is restored. | `ModelProbe` (`DotNodeViewer.Core.Abstractions`) |
| **Parse Diagnostic** | A non-fatal warning or informational note emitted during parsing (e.g., when TorchScript falls back from `code/*.py` to the module hierarchy, or when an HDF5 dataset is chunked/compressed). | `ParseDiagnostic` (`DotNodeViewer.Core.Ir`) |
| **Golden Topology Snapshot** | A committed `<model-id>.expected.json` file recording a model's format string, subgraph count, node count, operator histogram, I/O shapes, and a representative `sampleWeightSlice` for regression testing. | `tests/DotNodeViewer.Core.Tests/Fixtures/<format>/*.expected.json` |
