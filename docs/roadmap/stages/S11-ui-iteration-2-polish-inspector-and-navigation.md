# Stage S11 — UI Iteration 2: Polish, On-Demand Weight Inspector, Search & Subgraph Navigation

## Goal
Deliver the complete, polished desktop visualizer experience across all 5 model ecosystems (ONNX, TFLite, ExecuTorch, PyTorch, Keras)—adding the on-demand `TensorDataSlice` weight inspector, full-graph search & filter, subgraph breadcrumb drill-down, interactive minimap, 1-hop neighborhood highlighting, diagnostic banner, keyboard/AT-SPI accessibility polish, and a comprehensive live UI critique pass using `computer-use` and `frontend-design`.

## Prerequisites
- Stage **S10** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`docs/design/design-system.md`](../../design/design-system.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- **`.agents/skills/computer-use/SKILL.md`** (mandatory live desktop driving & screenshot verification across all 5 formats)
- **`.agents/skills/frontend-design/SKILL.md`** (writing guidance + visual polish critique pass)
- `.agents/skills/development-plugin-for-avalonia/SKILL.md` → route to:
  - `skills/avalonia-views-and-templating/SKILL.md`
  - `skills/avalonia-controls-and-windowing/SKILL.md`
  - `skills/avalonia-accessibility-and-validation/SKILL.md`
  - `skills/avalonia-input-and-commands/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-tunit/SKILL.md`

## Scope & Deliverables
1. **On-Demand Tensor Weight Inspector (`TensorInspectorViewModel`):**
   - When a user clicks/expands an initializer or constant tensor in the Properties Sidebar, calls `TensorDataLoader` on the open model `Stream` to read **only** that tensor's `TensorDataSlice` (capped to a preview window, e.g., up to 64 KB for statistics + first 64 values).
   - Decodes `Raw` and `VarintPacked` slices across `Float32`, `Float16`, `BFloat16`, `Float64`, `Int8`, `UInt8`, `Int16`, `Int32`, `Int64`, and `Bool` (and displays dequantized preview values alongside raw quantized integers when `AffineQuantization` is present).
   - Displays summary statistics (`Min`, `Max`, `Mean`, `Zero %`, `NaN/Inf` check), a compact value preview table, and file slice metadata (`StreamOffset`, `ByteLength`).
   - For `ExternalTensorDataReference`, displays the relative shard path, offset, and length without opening external files automatically.
2. **Fast Search & Filter (`SearchPanelViewModel`):**
   - Triggered via toolbar search box or `Ctrl+F`.
   - Searches across the active subgraph's node names, operator types, category names, and tensor names with instant categorized results.
   - Selecting a search result smoothly pans/zooms `GraphCanvas` to center and select the target node.
3. **Subgraph Selector & Breadcrumb Drill-Down:**
   - Top breadcrumb bar (`Root: main > If_0: then_branch`).
   - Allows switching between multiple root subgraphs (e.g., ExecuTorch methods, ONNX functions) and clicking into `AttributeValue.Graph` / `GraphList` attributes (`If`, `Loop`, `WHILE`, nested Keras models) to inspect their subgraph on `GraphCanvas`, with `Back` / `Escape` or breadcrumb click to return.
4. **Canvas & UX Polish for Embedded/ML Conversion Workflows:**
   - **Minimap Overlay:** collapsible bottom-left minimap showing the full graph silhouette and draggable viewport rectangle.
   - **1-Hop Neighborhood Highlighting:** selecting a node highlights its immediate upstream producer nodes/edges and downstream consumer nodes/edges while subtly dimming unrelated branches.
   - **Architecture & Conversion Quick-Summary Bar:** at-a-glance badges in the header showing total parameters/weight bytes, precision mix (e.g., `INT8 Quantized` vs `FP32`), and any custom delegate blocks (`XnnpackBackend`, etc.) so users checking a converted `.pt2` → `.tflite` / `.pte` model can immediately confirm precision and delegation.
   - **Diagnostics Banner / Drawer:** clean, non-intrusive presentation of `ModelGraph.Diagnostics` (e.g., TorchScript hierarchy fallback or weights-only `state_dict` explanation) written in plain, helpful language per `frontend-design`.
   - **Keyboard & AT-SPI Accessibility:** `Tab` / arrow-key navigation between connected nodes on the canvas, `F` for zoom-to-fit, `Escape` to clear selection/search, and complete `AutomationProperties` on all new controls.
5. **Visual Critique Pass (`frontend-design` + `computer-use`):**
   - Apply Chanel's rule from `frontend-design` ("take a look in the mirror and remove one accessory"): tighten padding, border hierarchy, label legibility, and Light/Dark contrast based on live screenshots.

## Acceptance Criteria & Verification
1. **Automated Build & Headless Tests (`tests/DotNodeViewer.App.Tests`):**
   - Unit/headless tests for `TensorInspectorViewModel` (using a byte-counting `Stream` wrapper to prove only `TensorDataSlice.ByteLength` bytes are read), `SearchPanelViewModel`, and subgraph breadcrumb navigation.
   ```bash
   dotnet build dot-node-viewer.slnx -c Release
   dotnet test dot-node-viewer.slnx -c Release
   ```
2. **Mandatory Live UI Run & Screenshots Across All 5 Formats (`computer-use`):**
   - Launch `DotNodeViewer.Desktop` and use `computer_use` (`mode="som"` and `mode="vision"`, `capture_after=True`) in the background without stealing focus to drive and screenshot:
     1. **ONNX** (`if_subgraph.onnx` — drilling down into `then_branch` and verifying the breadcrumb bar).
     2. **TFLite** (`tiny_cnn_int8.tflite` — selecting a quantized `CONV_2D` node and expanding the On-Demand Tensor Weight Inspector to show raw `INT8` + dequantized preview values).
     3. **ExecuTorch** (`tiny_cnn_xnnpack.pte` — inspecting the `XnnpackBackend` delegate node).
     4. **PyTorch** (`tiny_cnn_export.pt2` and `tiny_cnn_traced.pt` — using Search `Ctrl+F` to jump to a node).
     5. **Keras** (`tiny_functional_residual.keras` — verifying skip-connection layout and minimap in both Light and Dark themes).
   - Save all screenshots under `docs/screenshots/S11/` and link them with critique notes in `docs/roadmap/PROGRESS.md`.

## Hand-off & Git Requirements
- Work on branch `stage/S11-ui-polish-and-inspector` (`git checkout -b stage/S11-ui-polish-and-inspector`).
- Commit your work incrementally in logical sub-commits prefixed with `S11:` (e.g., `S11: add on-demand TensorInspectorViewModel`, `S11: add graph search and subgraph breadcrumb navigation`, `S11: add minimap, neighborhood highlighting, and computer-use critique pass`, `S11: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S11** complete.
