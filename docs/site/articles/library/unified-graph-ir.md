# Explanation & Reference: Unified Graph IR (`DotNodeViewer.Core.Ir`)

> [!NOTE]
> This article is scaffolded in **Stage S00** and will be expanded with complete record definitions and C# usage examples in **Stage S01** (`S01-core-ir-harness-and-cli`).

---

## Architectural Overview

`DotNodeViewer.Core` separates **format-specific binary framing** from **graph topology and semantics**:

1. **Zero UI or Layout Coupling:** `DotNodeViewer.Core` and `DotNodeViewer.Layout` never reference `Avalonia` or `SkiaSharp`. An MSBuild guardrail target (`ValidateNoUiDependenciesInCoreAndLayout`) enforces this boundary at compile time.
2. **Pure Immutable C# Record IR:** Every format reader projects its internal container structures into the immutable record hierarchy under `DotNodeViewer.Core.Ir` (`ModelGraph`, `Subgraph`, `GraphNode`, `NodePort`, `GraphPort`, `TensorEdge`, `TensorDescriptor`, `TensorShape`, `TensorDimension`, `NodeAttribute`, `AttributeValue`, `QuantizationInfo`, and `TensorDataSource`).
3. **Zero-Allocation Lazy Weight Slicing:** Rather than allocating `byte[]` or `float[]` arrays for raw tensor weights during graph extraction, format readers record a lightweight `TensorDataSlice(StreamOffset, ByteLength, DataType, Encoding)` and seek past the payload. Weights are loaded on demand via `TensorDataLoader` only when inspected.
4. **NativeAOT & WebAssembly Compatibility:** All public and internal code in `DotNodeViewer.Core` and `DotNodeViewer.Layout` compiles with `IsAotCompatible=true` and `IsTrimmable=true`.

