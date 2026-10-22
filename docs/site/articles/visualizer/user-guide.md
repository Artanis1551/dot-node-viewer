# How-To Guide: Using the `dot-node-viewer` Visualizer

> [!NOTE]
> The Avalonia desktop and browser visualizer UI is built across **Stage S05** (Design System & Shell), **Stage S06** (Graph Canvas & Node Properties), **Stage S11** (Weight Inspector, Search & Subgraph Navigation), and **Stage S12** (WebAssembly Browser Head).

---

## Overview

`DotNodeViewer.App` provides the shared cross-platform Avalonia user interface hosted by:

- **`DotNodeViewer.Desktop`** — Native desktop application for Linux, macOS, and Windows.
- **`DotNodeViewer.Browser`** — Client-side WebAssembly (`net10.0-browser`) application that parses and renders models directly in the browser without uploading files to a server.

---

## Planned Capabilities

1. **Opening Models (Stage S05):** Open models from the command line, file picker, or drag-and-drop.
2. **Interactive Layered Graph Canvas (Stage S06):** Smooth pan, zoom-to-cursor, Fit-to-Screen, and hit-tested node/edge selection powered by `DotNodeViewer.Layout`.
3. **Node & Tensor Properties Sidebar (Stage S06):** Inspect operator category, attributes, input/output ports, tensor shapes, and quantization parameters.
4. **On-Demand Tensor Weight Inspector (Stage S11):** Slice and decode tensor weights on demand (`Min`, `Max`, `Mean`, `NaN`/`Inf` counts, and value preview) via `TensorDataLoader`.
5. **Fast Node Search & Subgraph Drill-Down (Stage S11):** Filter nodes by name or operator type and navigate nested control-flow subgraphs via breadcrumb navigation.

