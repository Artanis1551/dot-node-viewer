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

### 1. Publish the WebAssembly Bundle

Publish the client-side WebAssembly AppBundle targeting the `browser-wasm` runtime:

```bash
dotnet publish src/DotNodeViewer.Browser -c Release -r browser-wasm
```

This compiles .NET assemblies and native Skia/HarfBuzz libraries into static WebAssembly assets at:
`src/DotNodeViewer.Browser/bin/Release/net10.0/browser-wasm/AppBundle/`

### 2. Serve Locally

Serve the generated static bundle with any local HTTP server (such as Python's built-in static server):

```bash
python3 -m http.server 8080 --directory src/DotNodeViewer.Browser/bin/Release/net10.0/browser-wasm/AppBundle
```

Navigate to `http://localhost:8080` in your web browser (Chrome, Firefox, Safari, Edge). Use the **Open Model** button (`Ctrl+O` / `Cmd+O`) or drag-and-drop to open any supported neural network model (`.onnx`, `.tflite`, `.pte`, `.pt`, `.pt2`, `.keras`, `.h5`).

### Drag-and-Drop in WebAssembly

Drag-and-drop in the browser head is routed natively through Avalonia's HTML5 drag events onto `MainView`:
- **Visual Feedback:** Dropping a file triggers the same responsive dashed-border drop overlay as the desktop shell.
- **Client-Side File Streaming:** Dropped browser files have no local filesystem path (`TryGetLocalPath()` returns `null`). `MainView` reads the file via `IStorageFile.OpenReadAsync()` and feeds chunks into `BrowserStreamAdapter.CreateSeekableStreamFactoryAsync`, parsing the model completely in-memory inside the browser tab without server uploads.

---

## Production Deployment

The static `AppBundle` directory contains only standard web assets (`index.html`, `main.js`, `.wasm`, `.js`). You can deploy this directory directly to any static hosting provider, including:
* **GitHub Pages**
* **Cloudflare Pages / Workers**
* **AWS S3 + CloudFront**
* **Nginx / Caddy static file hosting**

Because `DotNodeViewer.Browser` runs standard single-threaded WASM with client-side streaming, it requires no special Cross-Origin Isolation headers (`COOP`/`COEP`) and runs on any standard web host out of the box.

