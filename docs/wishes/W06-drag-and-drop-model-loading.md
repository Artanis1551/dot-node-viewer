# Wish W06 — Cross-Platform Drag-and-Drop Model Loading & Visual Drop Target UX

**Status:** Proposed  
**Depends on:** Stage **S05** (Shell & Window), Stage **S06** (GraphCanvas), Stage **S11** (UI Polish & Inspector), Stage **S12** (WebAssembly Head)  
**Suggested branch when promoted:** `feature/drag-and-drop-model-loading`  

## Goal

Provide a smooth, cross-platform **drag-and-drop model loading experience** that works consistently across both Desktop (Linux, macOS, Windows) and WebAssembly (browser) heads. When a user drags a supported neural network model file (`.onnx`, `.tflite`, `.pte`, `.pt`, `.pt2`, `.keras`, `.h5`) over the application window, the viewer should display a clear, accessible **visual drop target overlay**, validate the candidate file format before drop, support multi-file sidecars (such as ONNX external data `.onnx` + `.onnx.data`), and load the model directly into `MainWindowViewModel` without relying on desktop-specific code-behind.

## Background & Friction

In the current codebase:
1. **Desktop-Only Code-Behind Coupling:** Drag-and-drop is only wired in `MainWindow.axaml.cs` using raw `AddHandler(DragDrop.DragOverEvent, ...)` and `AddHandler(DragDrop.DropEvent, ...)`. It directly extracts `files[0].TryGetLocalPath()` and passes the path to `vm.LoadModelAsync(path)`.
2. **WebAssembly Head Completely Inactive:** In `DotNodeViewer.Browser`, Avalonia uses `ISingleViewApplicationLifetime`, mounting `MainView` directly into the browser canvas. While `MainView.axaml` sets `DragDrop.AllowDrop="True"`, no drag-and-drop handlers are attached. Even if handlers were attached, browser file drops have no local filesystem path (`TryGetLocalPath()` returns `null`). Browser drops must be read asynchronously via `IStorageFile.OpenReadAsync()` and wrapped with `BrowserStreamAdapter.CreateSeekableStreamAsync` into `vm.LoadModelStreamAsync(...)`.
3. **Zero Visual Affordance / Drop Adorner:** When a file is dragged over the window, there is no visual indicator or drop-zone overlay. The user receives no visual feedback indicating that the application is ready to receive the model, whether over the initial empty welcome state or over an active graph canvas.
4. **Indiscriminate Acceptance & Poor Error Handling:** `OnDragOver` unconditionally sets `e.DragEffects = DragDropEffects.Copy` for any dropped file regardless of file extension or MIME type. Dropping an unsupported file (e.g., an `.mp4`, `.pdf`, or `.zip`) triggers an unhandled parse failure or leaves the user with an unclear error toast instead of rejecting the drop effect or showing friendly feedback.
5. **No Multi-File or External Weights Resolution:** Models with detached external tensor files (such as large ONNX models split into `model.onnx` and `model.onnx.data`, or PyTorch multi-part checkpoints) cannot be opened by dragging both files simultaneously or dropping an enclosing directory. The current desktop handler naively selects `files[0]`, discarding companion files.
6. **Outdated Welcome Screen Copy:** The empty-state text in `MainView.axaml` specifically reads: `"Drop an ONNX or TensorFlow Lite model file here to inspect its architecture."`, omitting ExecuTorch, PyTorch, and Keras formats and reinforcing the need for a unified drop experience.

```mermaid
flowchart TD
    subgraph Current["Current: Fragmented Desktop-Only Handler"]
        OS[OS File Drag] --> MW[MainWindow.axaml.cs]
        MW --> DOver[OnDragOver: Unconditional Copy Effect]
        MW --> Drop[OnDrop: files[0].TryGetLocalPath]
        Drop --> PathLoad[vm.LoadModelAsync: Path Only]
        MW -.No visual overlay.-> Canvas[GraphCanvas / Empty State]
        WASM[Browser WASM Head] -.MainView has no handlers.-> DeadEnd[Dropped files ignored / browser navigates away]
    end

    subgraph Deepened["Deepened: Unified Cross-Platform Drop Architecture"]
        DragEvent[Drag Event: Desktop or Browser WASM] --> Behavior[DropTargetBehavior / MainView]
        Behavior --> Validator[Format Sniffer & Extension Whitelist]
        Validator --> VisualOverlay["Visual Drop Overlay (Scrim · Dashed Neon Border · Format Chips)"]
        Validator --> Resolver[Storage Item Resolver]
        Resolver -->|Desktop local path| VMPath[vm.LoadModelAsync]
        Resolver -->|WASM stream / memory| StreamAdapter[BrowserStreamAdapter]
        StreamAdapter --> VMStream[vm.LoadModelStreamAsync]
        Resolver -->|Multi-file ONNX + external data| Companion[Bind Companion External Data Path]
        Companion --> VMPath
    end
```

## Proposed Architecture

### 1. Unified Drop Handling in `MainView`

- Migrate drag-and-drop event management from `MainWindow.axaml.cs` to a reusable control behavior or code-behind handler on `MainView`:
  - Attach `DragDrop.DragEnterEvent`, `DragDrop.DragOverEvent`, `DragDrop.DragLeaveEvent`, and `DragDrop.DropEvent`.
  - Handle both `IClassicDesktopStyleApplicationLifetime` and `ISingleViewApplicationLifetime` uniformly.
- When `IStorageItem.TryGetLocalPath()` returns a valid path (Desktop), delegate to path-based model loading.
- When `TryGetLocalPath()` returns `null` or empty (Browser / WebAssembly), read the `IStorageFile` asynchronously via `OpenReadAsync()`, convert to a seekable stream via `BrowserStreamAdapter.CreateSeekableStreamAsync`, and call `vm.LoadModelStreamAsync`.

### 2. Interactive Visual Drop Target Overlay

- Add a dedicated `DropOverlayControl` (or embedded overlay grid) in `MainView.axaml` positioned above both the empty-state card and the active `GraphCanvas`:
  - **Visual Elements:**
    - Semi-transparent dark backdrop scrim (`ShellCanvasBackgroundBrush` with high opacity or blur).
    - Rounded border with dashed stroke (`ShellAccentPrimaryBrush`, 2px dashed border, `RadiusLarge`).
    - Prominent drop icon (e.g., arrow-down-to-bracket / document import symbol).
    - Clear headline text: `"Drop model file to visualize"`.
    - Supported format badges: `ONNX (.onnx)`, `TFLite (.tflite)`, `ExecuTorch (.pte)`, `PyTorch (.pt, .pt2)`, `Keras (.keras, .h5)`.
  - **State Management:**
    - Bind visibility to a reactive property on `MainWindowViewModel`: `IsDragOver` / `IsDropTargetActive`.
    - Fade in / fade out with smooth opacity transitions (`Transitions` block in XAML).
    - Carefully track drag bounds during `DragLeave` to avoid flickering when hovering over nested child controls.

### 3. File Validation & Drag Effects

- Inspect `e.DataTransfer.TryGetFiles()` during `DragOver`:
  - Whitelist recognized extensions: `.onnx`, `.tflite`, `.pte`, `.pt`, `.pt2`, `.keras`, `.h5`.
  - If a valid candidate is found:
    - Set `e.DragEffects = DragDropEffects.Copy`.
    - Keep visual overlay active in valid state.
  - If all files have invalid/unsupported extensions:
    - Set `e.DragEffects = DragDropEffects.None`.
    - Display warning cue on the overlay (e.g., red accent border and `"Unsupported format"` message) or suppress the overlay.
  - If multiple files are dropped, scan for primary model files vs. companion data files.

### 4. Multi-File & Companion Weight Sidecar Handling

- Support dragging multi-file models:
  - **ONNX with External Data:** When both `model.onnx` and `model.onnx.data` (or multiple `.data` shards) are dropped together, identify the `.onnx` file as the primary model and configure the external tensor data resolver to look alongside it.
  - **Directory Drops:** When an entire directory containing a supported model is dropped, locate the entry model file or provide a clear selection prompt.

### 5. View-Model Seam & Headless Testability

- Expose clean, testable methods and commands on `MainWindowViewModel`:
  - `bool CanAcceptFiles(IReadOnlyList<string> fileNames)`
  - `Task HandleDroppedFilesAsync(IReadOnlyList<IStorageItem> items, CancellationToken cancellationToken = default)`
  - Reactive properties: `IsDropTargetActive`, `DropTargetFeedbackMessage`.
- Enable headless integration testing in `DotNodeViewer.App.Tests` via `AvaloniaHeadless`:
  - Test dragging valid files triggers `IsDropTargetActive = true` and `DragDropEffects.Copy`.
  - Test dragging unsupported files sets `DragDropEffects.None`.
  - Test dropping a valid model loads the graph and sets `HasModel = true`.
  - Test dragging outside the bounds resets `IsDropTargetActive = false`.

## User Experience & Visual Design

| State | Visual Treatment | Cursor |
|---|---|---|
| **Idle (No Drag)** | Normal canvas or empty welcome card. Overlay opacity 0, hit-test invisible. | Normal |
| **Valid File Drag Over** | Full-window scrim fades in; 2px dashed accent border (`#6366F1` or `#38BDF8`); glowing icon and `"Drop model file to visualize"` banner; accepted format pills highlighted. | Copy (+) |
| **Unsupported File Drag Over** | Muted warning scrim; dashed red/amber border; `"Unsupported format. Supported: .onnx, .tflite, .pte, .pt, .pt2, .keras, .h5"`. | None (🚫) |
| **Drop Released** | Overlay smoothly fades out; loading indicator / status bar updates to `"Parsing <file>..."`; layout engine renders model graph. | Wait / Normal |
| **Drag Leave / Esc** | Overlay immediately dismisses; previous canvas or welcome view restored without state disruption. | Normal |

## Acceptance Criteria

1. **Desktop Parity:** Dragging a supported model onto `MainWindow` on Linux, macOS, and Windows displays the visual drop overlay and loads the model upon release.
2. **WebAssembly Parity:** Dragging a supported model into the browser viewport in `DotNodeViewer.Browser` displays the visual drop overlay, streams the file via `BrowserStreamAdapter`, and loads the model without local file path dependencies.
3. **Visual Drop Overlay:** Features a high-contrast dashed accent border, format pills, smooth opacity transitions, and responsive drag-leave dismissal.
4. **Format Filtering:** Restricts accepted drop effects to supported formats (`.onnx`, `.tflite`, `.pte`, `.pt`, `.pt2`, `.keras`, `.h5`) and provides clear error feedback for unrecognized extensions.
5. **Multi-File Detection:** Intelligently identifies the primary model when companion files (such as `.onnx.data`) are dropped concurrently.
6. **Empty-State Text Refresh:** Update `MainView.axaml` empty-state typography to mention all supported formats rather than only ONNX and TensorFlow Lite.
7. **Headless Tests:** Headless TUnit tests in `DotNodeViewer.App.Tests` verify drop validation, state transitions, and model loading under both local path and stream modalities.
8. **Live UI Verification:** Verified on Linux desktop with `computer-use` taking before/during/after screenshots saved to `docs/screenshots/W06/`.

