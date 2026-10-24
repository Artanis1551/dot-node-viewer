# How-To Guide: Inspecting & Validating Edge Conversions (`.pt2` to `.tflite` / `.pte`)

When preparing neural network models for edge deployment on mobile, embedded, or IoT devices, machine learning engineers typically export models from PyTorch (`ExportedProgram` / `.pt2`) and convert them to TensorFlow Lite (`.tflite`) or ExecuTorch (`.pte`).

This guide walks through using `dot-node-viewer` to inspect and cross-verify models across these conversion steps to ensure topological fidelity, correct operator fusions, valid quantization schemes, and proper hardware delegate partitioning.

---

## 1. Step 1: Inspecting the PyTorch Export Baseline (`.pt2`)

Before conversion, open the source `.pt2` model to establish the ground-truth topology and parameter footprint:

```bash
dotnet run --project src/DotNodeViewer.Desktop -c Release -- models/export/tiny_cnn_export.pt2
```

![PyTorch Model Search Panel](../../images/s11/04-pytorch-search.png)

### 1.1 Checklist for the PyTorch Export
1. **Model Inputs & Outputs:** Verify that dynamic batch dimensions (e.g., `batch` or `s0`) and channel order match your inference contract in the Model Overview card.
2. **Total Parameters:** Note the total parameter count in the top Architecture Bar (e.g., `12.5K params`).
3. **Control-Flow & Subgraphs:** If the model includes conditioned branches (`torch.ops.higher_order.cond`), verify that submodules appear in the breadcrumb trail.

---

## 2. Step 2: Verifying Quantized TensorFlow Lite Models (`.tflite`)

During quantization (`torch.ao.quantization` or TFLite Converter post-training quantization), FP32 weights and activations are converted to integer scales (`INT8` or `UINT8`).

Open the resulting `.tflite` model:

```bash
dotnet run --project src/DotNodeViewer.Desktop -c Release -- models/tflite/tiny_cnn_int8.tflite
```

![TFLite Weight Inspector](../../images/s11/02-tflite-weight-inspector.png)

### 2.1 Quantization Scheme Verification
1. **Precision Mix:** The Architecture Bar displays the integer percentage (e.g., `INT8: 100%`).
2. **Quantization Parameters:** Select the first convolution node (`CONV_2D`). In the **Properties Panel**:
   - Verify that input, filter, and output tensors carry the `INT8` badge.
   - Inspect the **Scale** and **Zero Point** parameters. For symmetric weight quantization, zero point should typically be `0`.
3. **Weight Inspector Verification:**
   - Click **Inspect Weights** on the filter tensor.
   - Inspect the **Raw vs. Dequantized** view: raw values show stored integer bytes (`-128..127`), while dequantized values reflect real floating-point numbers computed via $q_{real} = (q_{int} - z) \times s$.
   - Check the **Mean** and **Zero %** statistics to ensure the weights were not corrupted by numerical clipping or overflow.

---

## 3. Step 3: Verifying ExecuTorch Delegate Partitioning (`.pte`)

When targeting microcontrollers, mobile NPUs, or CPU backends with ExecuTorch, operators are partitioned into delegated subgraphs (such as XNNPACK, CoreML, or QNN).

Open the partitioned `.pte` model:

```bash
dotnet run --project src/DotNodeViewer.Desktop -c Release -- models/executorch/tiny_cnn_xnnpack.pte
```

![ExecuTorch Neighborhood Highlighting](../../images/s11/03-executorch-xnnpack-neighborhood.png)

### 3.1 Delegate Verification
1. **Delegate Block Count:** Check the Architecture Bar for the delegate badge (e.g., `XNNPACK (1 block)`).
2. **Delegate Node Boundaries:**
   - Click the delegated operator node on the canvas.
   - Observe the **1-Hop Neighborhood Highlighting**: the predecessor activations leading into the delegate and successor activations exiting it are highlighted with emerald and amber accent pens, while non-delegate nodes are dimmed to 25% opacity.
3. **Delegate Attributes:** In the Properties Panel, review the `custom_code` or delegate identifier and verify that no unintended operators fell back to un-accelerated CPU kernels.

---

## 4. Summary Conversion Validation Checklist

| Check | Source `.pt2` | Converted `.tflite` | Compiled `.pte` |
|---|---|---|---|
| **Input Shape** | `[batch, 1, 28, 28]` | `[batch, 28, 28, 1]` *(NHWC)* | Matches target layout |
| **Output Classes** | `[batch, 10]` | `[batch, 10]` | `[batch, 10]` |
| **Precision** | `FP32` | `INT8` (affine scale/zero-point) | `INT8` / `FP32` delegated |
| **Parameter Size** | ~50 KB (float) | ~13 KB (int8) | ~14 KB (flatbuffers) |
| **Execution** | Eager / Inductor | CPU / GPU delegate | XNNPACK delegate |
