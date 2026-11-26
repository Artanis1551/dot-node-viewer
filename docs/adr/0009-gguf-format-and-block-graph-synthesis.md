# ADR 0009: GGUF Format Support, Block-Quantized Tensor Encoding, and Block-Level Graph Synthesis

- **Status:** Accepted
- **Date:** 2026-11-26
- **Stage:** W12

---

## Context

GGUF (GGML Universal File Format) is the standard container for quantized local large language models (LLMs), multimodal vision-language models, and speech/audio models across `llama.cpp`, Ollama, LM Studio, and `whisper.cpp`. Unlike ONNX, TFLite, or ExecuTorch, a `.gguf` file does not store an explicit computation graph; instead it consists of:

1. A compact binary header (`GGUF` magic, version `1..=3`, tensor count, metadata KV count).
2. A metadata key-value section containing architecture hyperparameters (such as `general.architecture`, `<arch>.block_count`, attention head counts, MoE expert counts) alongside massive tokenizer arrays (`tokenizer.ggml.tokens`, `tokenizer.ggml.merges`, `tokenizer.ggml.scores` with 128k–256k entries).
3. A flat tensor info table (`name`, `n_dims`, `dims[]`, `ggml_type`, relative data offset).
4. Alignment padding (`general.alignment`, default 32 bytes) followed by multi-gigabyte raw and block-quantized weight payloads.

Integrating GGUF into `DotNodeViewer.Core` under the frozen v1 IR contract (**ADR 0002**, **ADR 0003**, **ADR 0006**) requires resolving three design questions:
1. How to extend `ModelFormatId` and represent GGML block-quantized super-blocks (`Q4_0`, `Q4_K`–`Q8_K`, `IQ*`, `TQ*`, `MXFP4`) safely without corrupting raw byte inspection.
2. How to synthesize an intelligible hierarchical graph from a flat tensor table across both classic LLMs and modern architectures (`deepseek41` with Engram, `mistral-large4`, `lfm3`, `kimi-k31`, `kokoro`, `moshi`, `limite`, `whisper`) as well as unseen future architectures.
3. How to bound startup latency and managed heap usage when parsing massive tokenizer metadata arrays and multi-gigabyte tensor payloads.

---

## Decision

### 1. Frozen IR Contract Additions (`DotNodeViewer.Core.Ir`)
- **`ModelFormatId.Gguf`:** Add `public static ModelFormatId Gguf => new("gguf");` on `ModelFormatId`.
- **`TensorDataEncoding.GgmlQuantizedBlock`:** Add `GgmlQuantizedBlock = 3` to `TensorDataEncoding` to identify GGML block-quantized payloads whose super-blocks interleave scale/zero headers with packed nibbles or sub-block codes.
- **Type & Quantization Mapping:**
  - Unquantized GGML element types (`F32`, `F16`, `BF16`, `F64`, `I8`, `I16`, `I32`, `I64`) map directly to their matching `DataType` values with `Quantization = null` and `TensorDataEncoding.Raw`.
  - Block-quantized GGML types (`Q4_0`, `Q4_1`, `Q5_0`, `Q5_1`, `Q8_0`, `Q8_1`, `Q2_K`–`Q6_K`, `Q8_K`, `IQ1_S`, `IQ1_M`, `IQ2_XXS`, `IQ2_XS`, `IQ2_S`, `IQ3_XXS`, `IQ3_S`, `IQ4_NL`, `IQ4_XS`, `TQ1_0`, `TQ2_0`, `MXFP4`) map to `DataType.UInt8`, `TensorDescriptor.Quantization = new BlockQuantization(Scheme, BlockSize, Bits)`, and `TensorDataEncoding.GgmlQuantizedBlock`.
  - `TensorInspectorViewModel` treats `TensorDataEncoding.GgmlQuantizedBlock` as raw hex bytes (`0xXX`) while displaying the scheme, block size, and bit width from `BlockQuantization`, preventing super-block scale headers from being misinterpreted as dense numeric elements.
- **Dimension Order & Checked Byte Sizing:**
  - GGML stores tensor dimensions innermost-first (`ne[0], ne[1], ...`). `GgmlTypeCatalog` validates `ne[0]` divisibility by `BlockSize` and computes byte lengths using checked `UInt128` arithmetic, while `GgufGraphSynthesizer` reverses dimensions into standard row-major order (`[ne[n-1], ..., ne[0]]`) in `TensorShape`.

### 2. Block-Level Graph Synthesis & Drill-Down Subgraphs
- `ModelGraph.Subgraphs` exposes a single primary entry subgraph (`Subgraphs[0]`, `SubgraphCount == 1`) wired sequentially from synthesized graph inputs through pre-block embeddings, repeated indexed blocks / named architecture stages, and output normalization / LM head to synthesized graph outputs.
- Indexed blocks (`blk.{i}.*`, `encoder.blk.{i}.*`, `decoder.blk.{i}.*`, `dep.{i}.*`, `v.blk.{i}.*`, `layers.{i}.*`) appear as grouped operator nodes in the primary subgraph (`TransformerBlock`, `MoETransformerBlock`, `EngramTransformerBlock`, `SSMBlock`, `HybridSSMBlock`, `EncoderBlock`, `DecoderBlock`, `DepthTransformerBlock`, `VisionTransformerBlock`) based on data-driven inspection of each block's member tensor suffixes.
- Each grouped block node attaches a child `Subgraph` via `NodeAttribute("subgraph", new AttributeValue.Graph(blockSubgraph))` containing internal submodules (`RMSNorm`, `Attention`, `MultiHeadLatentAttention`, `StateSpace`, `EngramMemory`, `FeedForward`, `MoEFeedForward`) so users can inspect high-level layer topology immediately and drill down via breadcrumb navigation when desired.
- Every GGUF tensor is registered in `primarySubgraph.Tensors` as `TensorKind.Initializer` with its lazy `TensorDataSlice(dataBaseOffset + relativeOffset, byteLength, dataType, encoding)`.
- Unseen `general.architecture` values fall back to data-driven grouping by `blk.{i}.*` and longest common dot-prefix modules without requiring hardcoded architecture lists.

### 3. Streaming Metadata Caps for Tokenizer Arrays
- Arrays with `<= 64` elements are formatted into `ModelGraph.Metadata`.
- Fixed-width numeric/bool arrays exceeding `64` elements are skipped in $O(1)$ using checked byte-length arithmetic and recorded as a summary string (`"[N Type values]"`).
- String arrays exceeding `64` elements sample only the first 4 entries and advance past the remaining length prefixes without UTF-8 decoding or managed `string` allocation, recording `"[N strings: s0, s1, ...]"`.

---

## Consequences

### Positive
- **Instant Multi-Gigabyte Opening:** Parsing scales strictly with metadata and tensor header counts (`< 1%` of payload size allocated), enabling multi-gigabyte LLMs to open cleanly on Desktop, CLI, and 32-bit Browser WebAssembly.
- **Readable LLM & Audio Topologies:** Grouping thousands of weights into block nodes with on-demand drill-down subgraphs keeps Sugiyama layout fast and readable while preserving every weight tensor for inspection and telemetry.
- **Resilient Future-Proofing:** Data-driven suffix classification handles both emerging architectures (`deepseek41`, `mistral-large4`, `lfm3`, `kimi-k31`, `kokoro`, `moshi`, `limite`) and arbitrary custom GGUF exports.

### Tradeoffs & Mitigations
- **Synthesized Topology:** Because GGUF has no explicit edge graph, sequential dataflow edges between blocks represent canonical architectural execution order rather than traced IR edges. Detailed tensor shapes, block quantization schemes, and architecture hyperparameters remain explicitly attached on every node and tensor.
- **No Super-Block Dequantization in v1:** `TensorInspectorViewModel` displays raw hex bytes and `BlockQuantization` metadata for `GgmlQuantizedBlock` slices; optional per-scheme dequantization (`Q4_0`/`Q8_0`) can be added independently without altering the IR.
