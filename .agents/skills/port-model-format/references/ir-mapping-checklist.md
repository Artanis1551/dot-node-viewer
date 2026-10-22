# Unified Graph IR Mapping Checklist

When mapping a format into the core library's Unified Graph IR, ensure the following edge cases are handled consistently across all formats:

## 1. Tensor Shapes & Dynamic Dimensions
- Do not represent tensor shapes as plain `int[]` because many formats (ONNX `dim_param`, TFLite `shape_signature` with `-1`) mix static sizes with symbolic or dynamic dimensions.
- Always map each dimension to `TensorDimension`:
  - `Fixed(long value)` for known static dimensions (e.g., `224`).
  - `Symbolic(string name)` for named dynamic axes (e.g., `"batch_size"`, `"seq_len"`).
  - `Dynamic()` for unknown/wildcard dimensions (`-1` or `?`).
- Distinguish between a **scalar tensor** (`Rank = 0`, shape `[]`) and an **unranked tensor** (`Shape = null`).

## 2. Initializers / Constant Weights vs. Graph Inputs
- Many formats (such as ONNX IR v3 and TFLite flat tensor lists) list constant weights alongside runtime activation tensors.
- Explicitly classify tensors in the IR (`TensorKind.Activation`, `TensorKind.Initializer`, `TensorKind.Constant`) so downstream visualizers can render weights as compact node slots rather than full-sized graph input nodes.

## 3. Quantization & Hardware Accelerator Metadata
- Preserve quantization parameters on `TensorDescriptor` whenever present:
  - **Affine quantization:** `Scale` (`float[]`), `ZeroPoint` (`long[]`), and `QuantizedDimension` (`int?` for per-channel vs. per-tensor quantization).
  - **Block-wise quantization (GGUF / LLM formats):** Block size, super-block scale type, and packed bit-width (e.g., `Q4_K_M`, `INT4`).
- If a format embeds NPU/accelerator subgraph delegation nodes (e.g., TFLite `Custom` delegate nodes or Ethos-U / Vela command stream payloads), preserve the custom operator code, delegate name, and payload byte size as structured attributes.

## 4. Subgraphs & Control Flow
- Operators like `If`, `Loop`, `Scan`, or `Call` reference nested subgraphs.
- Store nested graphs as `NodeAttribute` values of type `Subgraph` (or `IReadOnlyList<Subgraph>`) rather than flattening them into the root graph, allowing UI consumers to support drill-down navigation.

## 5. External Weights
- Formats like ONNX (`external_data`) and Safetensors index files (`model.safetensors.index.json`) reference weights stored in external shard files.
- Map these to an `ExternalTensorDataReference` containing the relative file path, byte offset, and byte length without attempting to open the external file from inside the core stream parser (preventing unauthorized file-system access in sandboxed/WASM environments).