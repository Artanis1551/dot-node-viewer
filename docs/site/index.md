# `dot-node-viewer` Documentation

Welcome to the documentation for **`dot-node-viewer`**, a high-performance, NativeAOT- and WebAssembly-compatible .NET 10 neural network model parser, layered layout engine, and cross-platform Avalonia visualizer inspired by Netron.

---

## Documentation Structure (Diátaxis)

Our documentation is organized around the four quadrants of the **Diátaxis** framework so you can jump directly to what you need:

### 1. Tutorials *(Learning-oriented)*
- [**Getting Started with `dot-node-viewer`**](articles/getting-started.md) — Verify your .NET 10 environment, build the solution, run the TUnit test suite, and build the documentation site.

### 2. How-To Guides *(Task-oriented)*
- [**Visualizer User Guide (`DotNodeViewer.Desktop` & `DotNodeViewer.Browser`)**](articles/visualizer/user-guide.md) — Open neural network models, navigate layered graphs, inspect node attributes, and examine lazy tensor weight slices.

### 3. Reference *(Information-oriented)*
- [**Supported Model Formats Overview**](articles/formats/overview.md) — Format matrix, magic-byte detection rules, and operator/tensor mapping details across ONNX, TFLite, ExecuTorch, PyTorch, and Keras.
- [**CLI Reference (`DotNodeViewer.Cli`)**](articles/cli/reference.md) — Command-line syntax and flags for `detect`, `inspect`, and `snapshot`.
- [**C# API Reference**](api/index.md) — Generated XML API reference for `DotNodeViewer.Core` and `DotNodeViewer.Layout`.

### 4. Explanation *(Understanding-oriented)*
- [**Unified Graph IR & Zero-Allocation Streaming Architecture**](articles/library/unified-graph-ir.md) — How `DotNodeViewer.Core` decouples format schemas from graph topology and skips multi-gigabyte weight buffers using `TensorDataSlice`.

