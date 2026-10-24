# Running DotNodeViewer in the Browser (WebAssembly)

DotNodeViewer provides a first-class WebAssembly target (`DotNodeViewer.Browser`) powered by Avalonia 11 for WebAssembly (`Avalonia.Browser`) and `.NET 10`. This head runs entirely client-side in the user's browser without requiring server-side compute or transferring neural network model weights over the network.

---

## Architecture & Lifetimes

The browser head leverages Avalonia's `ISingleViewApplicationLifetime`, mounting the shared `MainView` control directly onto an HTML5 canvas:

```text
Browser Window / Canvas
  └── MainView (UserControl)
        ├── SubgraphBreadcrumbBar
        ├── GraphCanvas (Layered Node/Edge Rendering)
        ├── SearchPanel (Categorized Node Finder)
        ├── TensorInspector (Lazy Weight Slicing & Histogram)
        ├── DiagnosticsDrawer (Conversion Warnings)
        └── MinimapControl (Viewport Overview)
```

Both `DotNodeViewer.Desktop` and `DotNodeViewer.Browser` share 100% of the UI layout, ViewModels, and graph rendering engine.

---

## Memory-Safe Browser Streaming (`BrowserStreamAdapter`)

Browsers place stringent memory limits on WebAssembly execution threads (typically 2 GB address space for 32-bit WASM runtimes). Loading multi-gigabyte model files entirely into contiguous managed byte arrays causes immediate out-of-memory crashes.

DotNodeViewer solves this using `BrowserStreamAdapter`:

1. **Memory Guard:** Enforces a configurable model size limit (default: 512 MB) before allocating streaming memory. Files exceeding this threshold are rejected with a clear descriptive message prompting the user to use the desktop client for ultra-large models.
2. **Chunked Memory Buffer:** Streams data asynchronously in pooled 64 KB chunks (`ArrayPool<byte>.Shared`), avoiding large object heap (LOH) fragmentation.
3. **Seekable Random-Access Stream:** Format readers (such as FlatBuffers for TFLite and ExecuTorch, or Protobuf for ONNX) require seekable streams (`CanSeek = true`) to read headers, jump across tensor tables, and lazily load weight slices. `BrowserStreamAdapter` produces a seekable `MemoryStream` snapshot while minimizing transient allocations.

```csharp
// Example: Staging an uploaded browser file safely
using var stream = await file.OpenReadAsync();
var streamFactory = await BrowserStreamAdapter.CreateSeekableStreamFactoryAsync(
    stream,
    maxByteLength: 512 * 1024 * 1024,
    cancellationToken: ct);

await viewModel.LoadModelStreamAsync(streamFactory, file.Name, ct);
```

---

## Building and Running Locally

### Prerequisites

Ensure the .NET WebAssembly workload is installed:

```bash
dotnet workload install wasm-tools
```

### Local Development Server

Run the browser head directly via `dotnet run`:

```bash
dotnet run --project src/DotNodeViewer.Browser
```

Navigate to `http://localhost:5000` (or the URL printed in the terminal). Use the **Open Model** button or drag-and-drop to open any supported neural network model (`.onnx`, `.tflite`, `.pte`, `.pt2`, `.keras`, `.h5`).

---

## Production Publishing

To publish the optimized standalone WebAssembly static bundle:

```bash
dotnet publish src/DotNodeViewer.Browser -c Release -o dist/wasm
```

The resulting `dist/wasm/wwwroot/` folder contains pure static assets (`index.html`, `.wasm` binaries, and JavaScript interop shims) that can be hosted on any static web host, CDN, or GitHub Pages.

