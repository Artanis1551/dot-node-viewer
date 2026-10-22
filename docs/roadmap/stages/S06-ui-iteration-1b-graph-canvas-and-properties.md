# Stage S06 — UI Iteration 1b: Graph Canvas & Basic Node Properties

## Goal
Replace the S05 viewport placeholder with a high-performance custom `GraphCanvas` control rendering interactive ONNX and TFLite neural network graphs (with pan/zoom, spatial hit-testing, category-colored nodes, collapsed initializer rows, quantized edge callouts) and a basic Node/Model Properties sidebar, verified via headless tests and live `computer-use` screenshots.

## Prerequisites
- Stage **S05** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`docs/design/design-system.md`](../../design/design-system.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- **`.agents/skills/computer-use/SKILL.md`** (mandatory live UI driving & screenshots)
- **`.agents/skills/frontend-design/SKILL.md`** (visual critique pass)
- `.agents/skills/development-plugin-for-avalonia/SKILL.md` → route to:
  - `skills/avalonia-rendering-and-graphics/SKILL.md`
  - `skills/avalonia-layout-and-virtualization/SKILL.md`
  - `skills/avalonia-input-and-commands/SKILL.md`
  - `skills/avalonia-testing-diagnostics-and-performance/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`
- `.agents/skills/csharp-tunit/SKILL.md`

## Scope & Deliverables
1. **`GraphCanvas` Custom Control (`src/DotNodeViewer.App/Controls/GraphCanvas.cs`):**
   - Renders `VisualSubgraphLayout` directly via `DrawingContext` (never instantiating one XAML `Control` per graph node).
   - **Viewport Culling & Level-of-Detail (LOD):** skips nodes and edges outside the visible viewport rect; at low zoom levels, omits fine port labels and shape strings to maintain 60 fps on large models (`mobilenetv2`).
   - **Cached Text & Brushes:** caches `FormattedText` layouts and category header brushes per theme variant; implements an `AvaloniaTextMeasurer : ITextMeasurer` so node box widths match the exact rendered font metrics.
   - **Node Visuals:**
     - Rounded node card using S05 design tokens with a distinct `OperatorCategory` header band, operator title, and subtle node name subtitle.
     - Collapsed initializer/weight rows rendered inside the node body with compact shape & data type pills (e.g., `W · float32 [64, 3, 3, 3]` or `weights_quant · uint8 [32, 3, 3, 3] Q`).
     - Fused activation badge row (e.g., `ReLU`, `ReLU6`) when present.
   - **Edge Visuals:**
     - Smooth cubic Bézier curves with directional arrowheads.
     - Tensor shape label pill along the edge midpoint at medium/high zoom (e.g., `[batch, 64, 112, 112]`), plus a clear visual indicator when an activation edge is quantized (`INT8` / `UINT8`).
   - **Interactive Navigation & Hit-Testing:**
     - Smooth pan (drag empty canvas / middle-mouse / two-finger scroll) and zoom-around-cursor (`Ctrl` + wheel / pinch / toolbar `+`, `-`, `Zoom to Fit` buttons).
     - Spatial grid index over `PositionedNode` boxes for $O(1)$ pointer hit-testing (hover highlight and click-to-select).
2. **Basic Properties Sidebar (v1) (`src/DotNodeViewer.App/Views/PropertiesPanelView.axaml`):**
   - **When no node is selected (Graph Overview):** shows format, producer, opset/version, subgraph selector, graph inputs, and graph outputs with shapes & quantization summary.
   - **When a node is selected (Node Inspector v1):** shows operator name, category badge, domain/version, documentation summary, typed attributes list (`Int64`, `Float64`, `String`, `TensorShape`, etc.), input ports (distinguishing runtime activations from initializers), and output ports with full `TensorDescriptor` details (`DataType`, `TensorShape`, and `AffineQuantization` scale/zero-point summary).
   - *(Note: On-demand raw tensor weight value reading, full-graph search, minimap, and subgraph breadcrumb drill-down are scoped to Stage S11.)*

## Acceptance Criteria & Verification
1. **Automated Build & Headless Render Tests (`tests/DotNodeViewer.App.Tests`):**
   - Headless render tests verifying `GraphCanvas` renders `tiny_mlp.onnx` and `tiny_cnn_int8.tflite` to a bitmap without errors, and pointer click at a node's coordinates selects that `GraphNode` in the ViewModel.
   ```bash
   dotnet build dot-node-viewer.slnx -c Release
   dotnet test dot-node-viewer.slnx -c Release
   ```
2. **Mandatory Live UI Run & Screenshots (`computer-use`):**
   - Launch `DotNodeViewer.Desktop` in the background on `tiny_cnn_dynamic_batch.onnx`, `tiny_cnn_int8.tflite`, and cached `mobilenetv2-7.onnx` / `mobilenet_v2_1.0_224_quant.tflite`.
   - Use `computer_use` (`mode="som"` and `mode="vision"`) to:
     1. Capture the initial zoom-to-fit graph view.
     2. Click a `Conv` / `CONV_2D` node on the canvas (`capture_after=True`) and verify the right-hand Properties Sidebar displays its attributes, initializer weights, and quantization parameters.
     3. Zoom in and out and capture the LOD transition.
   - Critique the screenshots against `docs/design/design-system.md`, save them to `docs/screenshots/S06/`, and link them in `docs/roadmap/PROGRESS.md`.

## Recommended Checkpoint After S06
Recommend to the user that they can optionally run `.agents/skills/improve-codebase-architecture/SKILL.md` before proceeding to Stages S07–S10.

## Hand-off & Git Requirements
- Work on branch `stage/S06-ui-graph-canvas` (`git checkout -b stage/S06-ui-graph-canvas`).
- Commit your work incrementally in logical sub-commits prefixed with `S06:` (e.g., `S06: implement custom GraphCanvas with pan/zoom and spatial hit-testing`, `S06: add PropertiesPanelView v1`, `S06: add headless render tests and computer-use screenshots`, `S06: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S06** complete.
