# Design System & Application Shell Specification (`dot-node-viewer`)

This specification defines the visual identity, token architecture, accessibility guarantees, typography, and application shell layout for `dot-node-viewer` (`DotNodeViewer.App` and `DotNodeViewer.Desktop`), designed in accordance with [`.agents/skills/frontend-design/SKILL.md`](../../.agents/skills/frontend-design/SKILL.md) and [`.agents/skills/development-plugin-for-avalonia/SKILL.md`](../../.agents/skills/development-plugin-for-avalonia/SKILL.md).

---

## 1. Subject, Audience & Primary Job

### 1.1 Product Subject
`dot-node-viewer` is a high-performance, NativeAOT-ready cross-platform visualizer and diagnostic inspector for machine learning and edge AI computational graphs.

### 1.2 Target Audience
1. **Edge & Firmware Developers:** Engineers porting models to constrained microcontrollers and micro-NPUs (e.g., Arm Ethos-U, ESP32, STM32, TFLite Micro, ExecuTorch). They are often not ML specialists; they need to quickly verify input/output shapes, tensor memory footprints, operator fusions, and confirm whether quantization (`INT8`, per-channel scales) was correctly preserved during export.
2. **ML Platform & Applied AI Engineers:** Developers verifying newly trained or converted architectures (`.onnx`, `.tflite`, and upcoming `.pte`, `.pt2`, `.keras`) to confirm graph connectivity, opset compatibility, and subgraphs before deployment.

### 1.3 Primary Job-to-be-Done
"When I export or receive a neural network binary, I want to open it instantly without launching heavyweight Python notebooks or web servers, visually inspect the model topology and tensor dimensions, and immediately spot quantization types, broken dimensions, or unsupported custom operators."

### 1.4 Design Principles
1. **Instrument, Not Toy:** The visual design draws from modern precision measurement instruments (digital storage oscilloscopes, logic analyzers, and hardware telemetry displays) rather than generic SaaS dashboards.
2. **Sensible Polish & Tactile Refinement:** While avoiding gratuitous floating shadows or garish decorative rainbows, sensible visual "nice things" are intentionally incorporated: refined corner radiuses (6–8px), subtle elevation transitions, crisp category badge pills with soft translucent backdrops, glowing selection outlines, and polished empty-state drop targets.
3. **Immediate Structural Legibility:** High-level graph hierarchy and dataflow (Input → Feature Extraction → Heads → Output) must be clear at a glance. Visual weight is reserved for dataflow boundaries and quantization changes.
4. **Approachable Terminology:** Avoid impenetrable ML compiler jargon where standard descriptive words suffice. Use plain, active terms: *Model Inputs*, *Model Outputs*, *Quantized (INT8)*, *Custom Hardware Delegate*.
5. **Quiet, Purposeful Motion & Feedback:** Button hover states, theme transitions, and drag-and-drop target highlights give immediate, reassuring tactile feedback without distracting from graph inspection.

---

## 2. Color Token System

### 2.1 Core Palette (Dark & Light Variants)
The base palette is anchored in **Deep Charcoal / Slate** for dark mode and **Porcelain / Steel** for light mode, providing high contrast and low visual fatigue during prolonged diagnostic sessions.

| Token | Dark Theme Hex | Light Theme Hex | Role & Usage |
|---|---|---|---|
| `ShellCanvasBackground` | `#12151B` | `#F8FAFC` | Main canvas / graph background surface |
| `ShellSurfaceBase` | `#1A1F29` | `#FFFFFF` | Primary content panels, sidebar, and cards |
| `ShellSurfaceElevated` | `#232A37` | `#F1F5F9` | Toolbar, popups, card headers, active item hover |
| `ShellBorderMuted` | `#333C4D` | `#CBD5E1` | 1px structural dividers, panel boundaries, card outlines |
| `ShellTextPrimary` | `#F1F5F9` | `#0F172A` | Primary headings, node names, tensor dimensions |
| `ShellTextSecondary` | `#94A3B8` | `#475569` | Secondary metadata, attribute keys, status hints |
| `ShellAccentPrimary` | `#38BDF8` | `#0284C7` | Interactive focus, selected node highlight, primary actions |

### 2.2 Colorblind-Checked Semantic Operator Palette
Operator categories are color-coded using an Okabe-Ito / Tol-inspired palette, tested for distinct luminance and hue separation under Protanopia, Deuteranopia, and Tritanopia.

| `OperatorCategory` | Dark Hex | Light Hex | Semantic Meaning / Common Operators |
|---|---|---|---|
| `Layer` | `#6366F1` | `#4F46E5` | Structural computation: `Conv2D`, `Gemm`, `MatMul`, `Dense` |
| `Activation` | `#F59E0B` | `#D97706` | Non-linearities: `Relu`, `Gelu`, `Sigmoid`, `HardSwish` |
| `Normalization` | `#A855F7` | `#9333EA` | Statistics & scaling: `BatchNorm`, `LayerNorm`, `RMSNorm` |
| `Pooling` | `#14B8A6` | `#0D9488` | Downsampling: `MaxPool`, `AveragePool`, `GlobalAveragePool` |
| `Quantization` | `#F43F5E` | `#E11D48` | Numerical conversion: `Quantize`, `Dequantize`, `Requantize` |
| `Shape` | `#0EA5E9` | `#0284C7` | Layout & indexing: `Reshape`, `Transpose`, `Squeeze`, `Flatten` |
| `Tensor` | `#10B981` | `#059669` | Array manipulation: `Concat`, `Split`, `Slice`, `Gather` |
| `Constant` | `#64748B` | `#475569` | Weights / biases: `Constant`, `Identity`, `Initializer` |
| `ControlFlow` | `#EC4899` | `#DB2777` | Conditionals & loops: `If`, `Loop`, `Scan`, `While` |
| `Data` | `#3B82F6` | `#2563EB` | Type conversion: `Cast`, `OneHot` |
| `Custom` | `#EAB308` | `#CA8A04` | Delegate & vendor ops: `TFLite_Custom`, `NpuDelegate` |
| `Unknown` | `#71717A` | `#52525B` | Unmapped / unrecognized ops |

### 2.3 Quantization Scheme Badges
Embedded engineers care deeply about whether a tensor is full-precision or converted to integer formats. These badges use dedicated pill badges with tabular digits:

| Badge Type | Dark Pill Background | Text Color | Semantic Meaning |
|---|---|---|---|
| `FP32` | `#334155` | `#E2E8F0` | Full precision floating point |
| `FP16` / `BF16` | `#1E3A5F` | `#7DD3FC` | Half precision / Brain floating point |
| `INT8` / `UINT8` | `#14422A` | `#86EFAC` | **Quantized Integer** (Green highlights hardware acceleration) |
| `INT4` | `#3B1B54` | `#D8B4FE` | Sub-byte quantized weights (LLM / Edge weight-only) |

---

## 3. Typography & Scale

### 3.1 Typeface Families
- **UI Proportional Face:** System sans-serif stack prioritizing clean legibility and balanced x-height:
  `"Inter", "Segoe UI", "-apple-system", "BlinkMacSystemFont", "Ubuntu", "Cantarell", "sans-serif"`
- **Tabular / Monospace Face:** Monospace font for tensor shapes (e.g. `[1, 3, 224, 224]`), memory sizes, file offsets, and SHA-256 digests:
  `"JetBrains Mono", "Cascadia Code", "Fira Code", "DejaVu Sans Mono", "Consolas", "monospace"`

### 3.2 Type Scale

| Level | Size (pt / px) | Weight | Line Height | Usage |
|---|---|---|---|---|
| `Headline` | `20pt` (`26px`) | SemiBold (`600`) | `32px` | Empty state title, major dialog header |
| `Title` | `15pt` (`20px`) | SemiBold (`600`) | `26px` | Model name, panel header, subgraph title |
| `Subtitle` | `13pt` (`17px`) | Medium (`500`) | `22px` | Section titles, node card header |
| `Body` | `11pt` (`15px`) | Regular (`400`) | `20px` | General UI labels, buttons, descriptions |
| `Caption` | `9.5pt` (`13px`) | Regular (`400`) | `16px` | Node attribute labels, status bar text |
| `Badge` | `8.5pt` (`11.5px`)| SemiBold (`600`) | `14px` | Quantization tags, category badges (tabular mono) |

---

## 4. Application Shell Layout & Wireframes

### 4.1 Shell Wireframe Overview

```
+-----------------------------------------------------------------------------------------+
| [DNV] Open Model [v]  | Subgraph: [ main (Default) v ] | Mode: [Dark/Light] [?]         |
+-----------------------------------------------------------------------------------------+
|                                                           | Model Properties            |
|                                                           |-----------------------------|
|                  CENTRAL GRAPH VIEWPORT                   | Format: ONNX 1.17           |
|                                                           | Producer: pytorch 2.6.0     |
|   (In Stage S05: Architectural Overview Card)             | Nodes: 14                   |
|   (In Stage S06: Interactive Pan/Zoom GraphCanvas)        | Tensors: 28                 |
|                                                           |                             |
|                                                           | Inputs:                     |
|                                                           |  - input_1: [1, 3, 32, 32]  |
|                                                           | Outputs:                    |
|                                                           |  - logits:  [1, 10]         |
+-----------------------------------------------------------------------------------------+
| Ready | Fixture: tiny_cnn_dynamic_batch.onnx | 14 nodes, 13 edges | Layout: 6.2ms       |
+-----------------------------------------------------------------------------------------+
```

### 4.2 Empty State Wireframe

```
+-----------------------------------------------------------------------------------------+
|                                                                                         |
|                                  [ + ]                                                  |
|                                                                                         |
|                             Open a Neural Network                                       |
|               Drop an ONNX or TensorFlow Lite model file here,                          |
|                     or select a sample below to inspect.                                |
|                                                                                         |
|                              [ Open File... ]                                           |
|                                                                                         |
|       Recent Models:                                                                    |
|       * tiny_cnn_dynamic_batch.onnx    (ONNX v1.17, 14 nodes)                           |
|       * tiny_cnn_int8.tflite           (TFLite v3, INT8 quantized)                      |
|                                                                                         |
|       Supported Formats: [ ONNX .onnx ]  [ TensorFlow Lite .tflite ]                    |
|                                                                                         |
+-----------------------------------------------------------------------------------------+
```

### 4.3 Error State Wireframe

```
+-----------------------------------------------------------------------------------------+
|                                                                                         |
|     /!\  Could Not Parse Model File                                                     |
|                                                                                         |
|     File: corrupt_header.onnx                                                           |
|     Error: Invalid magic byte sequence at byte offset 0x00000004.                       |
|     Context: Expected valid Protobuf tag or TFLite identifier 'TFL3'.                   |
|                                                                                         |
|     Action: Verify the model was exported successfully from PyTorch or TensorFlow,      |
|             or try opening a different fixture.                                         |
|                                                                                         |
|     [ Try Another File ]                                                                |
|                                                                                         |
+-----------------------------------------------------------------------------------------+
```

---

## 5. Self-Critique Against Generic AI Defaults

Before starting implementation, the design plan is calibrated against the common failure patterns documented in `.agents/skills/frontend-design/SKILL.md`:

1. **Background:** Avoided the warm cream (`#F4F1EA`) and clay/terracotta (`#D97757`) defaults, as well as the pitch-black hacker look (`#050505` + acid green). Selected deep graphite (`#12151B`) and slate porcelain (`#F8FAFC`).
2. **Structural Rules vs. Floating Cards:** Removed soft generic box shadows (`rgba(0,0,0,0.1)`) and rounded floating card kits. Replaced with clean 1px structural borders (`#333C4D` / `#CBD5E1`) that cleanly delineate telemetry panels.
3. **Typography Discipline:**
   - No tracked-out all-caps eyebrow labels (`OVERVIEW`, `STATUS`).
   - No fake middle-dot meta strings (`A · B · C`).
   - Standard sentence case for labels and controls.
   - Distinct tabular monospace face reserved specifically for numbers, byte counts, and tensor dimensions.
4. **Copywriting Tone:**
   - Clear, functional verbs: "Open File...", "Copy Tensor Name", "Export Layout".
   - Errors explain *what* happened, *where* (exact byte offset), and *what to do next*, without apologetic filler.

---

## 6. Avalonia Theme Implementation Structure

Tokens are implemented in two complementary Avalonia XAML resource dictionaries:
1. `src/DotNodeViewer.App/Themes/Tokens.axaml`: Application background, surface, border, and text brushes supporting `ThemeDictionaries` with `Default` (Dark) and `Light` keys.
2. `src/DotNodeViewer.App/Themes/NodePalette.axaml`: Category brushes for all 12 `OperatorCategory` values and quantization schemes.
3. Accessibility: All interactive controls carry `AutomationProperties.Name` and `AutomationProperties.AutomationId` for AT-SPI / `cua-driver` accessibility indexing.
