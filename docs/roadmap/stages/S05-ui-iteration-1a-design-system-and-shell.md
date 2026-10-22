# Stage S05 — UI Iteration 1a: Design System & Avalonia App Shell

## Goal
Establish a distinctive, intentional visual design system (`docs/design/design-system.md`) following `frontend-design`, get user approval on the design plan, and build the Avalonia application shell (`DotNodeViewer.App` + `DotNodeViewer.Desktop`) with file opening, CLI model-path loading, drag-and-drop, recent files, background parsing/layout, error states, and live screenshot verification via `computer-use`.

## Prerequisites
- Stage **S04** complete.
- Read [`AGENTS.md`](../../../AGENTS.md), [`PREREQUISITES.md`](../../../PREREQUISITES.md), and [`docs/roadmap/PROGRESS.md`](../PROGRESS.md).

## Skills to Load
- **`.agents/skills/frontend-design/SKILL.md`** (mandatory two-pass design process)
- **`.agents/skills/computer-use/SKILL.md`** (mandatory live desktop capture & verification)
- `.agents/skills/development-plugin-for-avalonia/SKILL.md` → route to:
  - `skills/avalonia-bootstrap-and-lifetime/SKILL.md`
  - `skills/avalonia-design-systems/SKILL.md`
  - `skills/avalonia-styling-and-resources/SKILL.md`
  - `skills/avalonia-bindings-and-xaml/SKILL.md`
  - `skills/avalonia-platform-services/SKILL.md`
  - `skills/avalonia-accessibility-and-validation/SKILL.md`
- `.agents/skills/csharp-async/SKILL.md`
- `.agents/skills/csharp-tunit/SKILL.md`
- `.agents/skills/documentation-writer/SKILL.md`

## Scope & Deliverables

### Pass 1: Design System Plan (`docs/design/design-system.md`) — STOP FOR USER APPROVAL
Before writing UI code, author `docs/design/design-system.md` following `.agents/skills/frontend-design/SKILL.md`:
1. **Subject, Audience & Primary Job:**
   - **Audience:** Both ML engineers **and embedded / edge / firmware developers (or developers less specialized in ML)** who are converting models for embedded targets (e.g., from PyTorch `.pt2` or ONNX to TFLite for TFLite Micro, or ExecuTorch `.pte`) and want to sanity-check that the converted model still looks structurally coherent (inputs/outputs, shapes, `INT8` vs `FP32` quantization, fused/delegated blocks), or who are inspecting the architecture of a newly downloaded model.
   - **Design Implications:** Avoid intimidating wall-of-jargon chrome. Make high-level structural cues immediately legible at a glance: clear input → stage → output flow, plain-language operator category cues, obvious visual callouts when a tensor is quantized (`INT8` / per-channel) or delegated to an NPU/backend, and approachable, plain-language copywriting in the sidebar, empty screen, and diagnostic/error states.
2. **Color Token System:**
   - 4–6 named core base palette hex values (both dark and light theme variants, avoiding generic AI defaults).
   - A **colorblind-checked semantic palette** for `OperatorCategory` (`Layer`, `Activation`, `Normalization`, `Pooling`, `Quantization`, `Shape`, `Tensor`, `Constant`, `ControlFlow`, `Data`, `Custom`, `Unknown`) plus quantization badge accents (`FP32`, `FP16`/`BF16`, `INT8`/`UINT8`, `INT4`).
3. **Typography & Scale:** Intentional typeface pairing (UI display/body face + tabular/monospace face for tensor shapes like `[batch, 3, 224, 224]` and byte offsets) and explicit type scale.
4. **Layout & ASCII Wireframes:** Toolbar/header strip, central graph viewport, collapsible right inspector pane, bottom status/diagnostic bar, and inviting empty state.
5. **Self-Critique Against Generic Defaults:** Review against the `frontend-design` calibration list, revise any generic choices, and **pause for user approval** if running interactively.

### Pass 2: Avalonia Shell Implementation (`src/DotNodeViewer.App` & `src/DotNodeViewer.Desktop`)
1. **Architecture & Bindings:**
   - Use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`) and compiled bindings (`x:DataType`) everywhere.
   - Implement theme token dictionaries (`Themes/Tokens.axaml`, `Themes/NodePalette.axaml`) for Light and Dark theme variants.
2. **Shell Features (`MainWindow`, `MainWindowViewModel`):**
   - **Open Model:** via `IStorageProvider` file picker, drag-and-drop onto the window, recent files list, and **command-line argument** (`dotnet run --project src/DotNodeViewer.Desktop -- <path-to-model>`) so automated scripts and `computer-use` can open any fixture directly on launch.
   - **Background Loading Pipeline:** opens a read-only `FileStream`, runs `ModelFormatRegistry.Default.ReadAsync` and `ModelGraphLayoutAdapter.BuildLayout` on a background thread with a responsive progress indicator and `CancellationToken` support.
   - **Empty State:** inviting, plain-language empty state with quick-open actions and supported formats summary.
   - **Error State:** clear, actionable presentation of `ModelParseException` (including file offset and context) in the interface's voice.
   - **Model Overview Card (Viewport Placeholder for S05):** until `GraphCanvas` lands in S06, display a clean architectural summary card of the loaded model (format, producer, subgraph selector, input/output tensor badges with shapes & quantization, operator category breakdown, and node count).
   - **AT-SPI / Accessibility (`AutomationProperties`):** set `AutomationProperties.Name` and `AutomationProperties.AutomationId` on all interactive shell controls (`OpenModelButton`, `ThemeToggleButton`, `SubgraphSelector`, `ModelSummaryPanel`, `ErrorBanner`) so `computer-use` (`mode="som"` / `mode="ax"`) indexes them cleanly.

## Acceptance Criteria & Verification
1. **Automated Build & Headless Tests (`tests/DotNodeViewer.App.Tests`):**
   - Test opening committed ONNX (`tiny_mlp.onnx`, `tiny_cnn_dynamic_batch.onnx`) and TFLite (`tiny_cnn_int8.tflite`) fixtures via `MainWindowViewModel`.
   - Test corrupted file handling (`ModelParseException` populates the error state cleanly).
   ```bash
   dotnet build dot-node-viewer.slnx -c Release
   dotnet test dot-node-viewer.slnx -c Release
   ```
2. **Mandatory Live UI Run & Screenshots (`computer-use`):**
   - Launch `DotNodeViewer.Desktop` in the background across 4 states:
     1. Empty state (`dotnet run --project src/DotNodeViewer.Desktop -c Release`)
     2. Loaded ONNX fixture (`tiny_cnn_dynamic_batch.onnx`)
     3. Loaded quantized TFLite fixture (`tiny_cnn_int8.tflite`)
     4. Theme toggle (Light vs Dark)
   - Use `computer_use(action="capture", mode="som", app="DotNodeViewer")` (and `mode="vision"`) in the background without stealing focus to capture screenshots and verify the AT-SPI element tree.
   - Save screenshots under `docs/screenshots/S05/`, critique them against `docs/design/design-system.md`, fix any visual flaws, and link the final screenshots in `docs/roadmap/PROGRESS.md`.
3. **Documentation (`README.md` & `docs/site/`):**
   - Update root `README.md` with desktop app launch instructions and initial shell screenshots.
   - Update `docs/site/articles/visualizer/user-guide.md` covering the desktop shell, CLI arguments, file loading, and theme switching, and verify `dotnet tool run docfx build docs/site/docfx.json --warningsAsErrors`.

## Hand-off & Git Requirements
- Work on branch `stage/S05-ui-design-and-shell` (`git checkout -b stage/S05-ui-design-and-shell`).
- Commit your work incrementally in logical sub-commits prefixed with `S05:` (e.g., `S05: author design-system.md`, `S05: implement Avalonia theme tokens and MainWindow shell`, `S05: add headless tests and computer-use verification screenshots`, `S05: update PROGRESS.md`).
- Update `docs/roadmap/PROGRESS.md` marking **S05** complete (linking the saved screenshots).
