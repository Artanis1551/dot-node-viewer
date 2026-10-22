# Stage S04 — Layout Engine (`DotNodeViewer.Layout`)

## Goal
Build `DotNodeViewer.Layout`, a fast, deterministic, pure C# Sugiyama/dagre-style layered graph layout engine and its `ModelGraphLayoutAdapter` (converting a `Subgraph` from `DotNodeViewer.Core` into positioned node boxes, collapsed initializer slots, and routed edge polylines) so the first UI iteration in Stages S05–S06 can render ONNX and TFLite graphs immediately.

## Prerequisites
- Stage **S03** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`GLOSSARY.md`](../../../GLOSSARY.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- `.agents/skills/csharp-docs/SKILL.md`
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/analyzing-dotnet-performance/SKILL.md`

## Scope & Deliverables
1. **Pure C# Layout Engine (`src/DotNodeViewer.Layout/`, zero UI dependencies):**
   - Input model: `LayoutGraph` (`LayoutNode` with width/height/ports, `LayoutEdge` with source/target node & port, `LayoutOptions` for direction `TopToBottom`/`LeftToRight`, layer separation, node separation).
   - Output model: `PositionedGraph` (`PositionedNode` with `X`, `Y`, `Width`, `Height`, port coordinates; `PositionedEdge` with control points / cubic Bézier segments and optional label anchor point; overall `BoundingBox`).
   - Sugiyama / dagre pipeline:
     1. **Aciclicity (Cycle Removal):** DFS / greedy feedback arc set reversal for cyclic graphs, restoring edge direction after coordinate assignment.
     2. **Layer Assignment (Ranking):** Longest-path initial ranking followed by iteration-capped network-simplex or tight-tree refinement.
     3. **Normalization:** Insert dummy nodes along edges spanning multiple ranks (`rankDelta > 1`).
     4. **Crossing Minimization:** Layer-by-layer sweeps using weighted median / barycenter ordering with adjacent-exchange transpose refinement.
     5. **Coordinate Assignment:** Brandes–Köpf horizontal alignment (or fast median alignment) producing balanced, straight vertical runs.
     6. **Edge Routing:** Extract polyline / cubic Bézier control points through dummy node chains and port anchors, avoiding node box intersections.
2. **IR-to-Layout Adapter (`ModelGraphLayoutAdapter`):**
   - Maps a `Subgraph` into a `LayoutGraph` and produces a `VisualSubgraphLayout`:
     - **Initializer & Constant Collapsing (Netron-style):** Tensors with `TensorKind.Initializer` or `TensorKind.Constant` that feed a single operator node are collapsed into compact parameter rows inside that node's visual box rather than spawning standalone graph nodes.
     - **Graph Inputs / Outputs:** Represented as compact terminal pills at the top/bottom of the graph.
     - **Fused Activation Badges:** Surfaces fused activation attributes (e.g., TFLite `fused_activation_function = RELU`) as an inline badge row on the node box.
     - **Text Measurement Seam:** Uses an `ITextMeasurer` abstraction (with a deterministic character-width fallback `MonospaceTextMeasurer` for headless/layout tests) so `DotNodeViewer.Layout` has zero dependency on Avalonia or Skia.
3. **Unit & Performance Tests (`tests/DotNodeViewer.Layout.Tests/`):**
   - Determinism test: laying out the same graph twice produces identical coordinates.
   - Non-overlap test: no two `PositionedNode` bounding boxes within the same layer or across layers intersect.
   - DAG flow test: for every non-reversed edge in a DAG, `target.Y > source.Y` in `TopToBottom` mode.
   - Cycle resilience test: graphs with 2-node and 3-node cycles complete without infinite loops or exceptions.
   - Real-fixture integration tests: lay out every committed ONNX (`tiny_mlp.onnx`, `tiny_cnn_dynamic_batch.onnx`, `if_subgraph.onnx`) and TFLite (`tiny_cnn_float.tflite`, `tiny_cnn_int8.tflite`, `tiny_while.tflite`) model.
   - **Performance budget test:** lay out a synthetic 5,000-node layered graph in `< 1,000 ms` in Release configuration.

## Acceptance Criteria & Verification
```bash
dotnet build dot-node-viewer.slnx -c Release
dotnet test dot-node-viewer.slnx -c Release
```

## Hand-off & Git Requirements
- Work on branch `stage/S04-layout-engine` (`git checkout -b stage/S04-layout-engine`).
- Commit your work incrementally in logical sub-commits prefixed with `S04:` (e.g., `S04: implement Sugiyama ranking and crossing minimization`, `S04: implement Brandes-Kopf coordinate assignment and edge routing`, `S04: add ModelGraphLayoutAdapter and layout tests`, `S04: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S04** complete.
