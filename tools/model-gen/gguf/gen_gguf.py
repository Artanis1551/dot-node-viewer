#!/usr/bin/env python3
"""Deterministic GGUF v3 test model and ground-truth generator for DotNodeViewer (Wish W12)."""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import struct
from pathlib import Path
from typing import Any

# GGUF metadata value types
GGUF_TYPE_UINT8 = 0
GGUF_TYPE_INT8 = 1
GGUF_TYPE_UINT16 = 2
GGUF_TYPE_INT16 = 3
GGUF_TYPE_UINT32 = 4
GGUF_TYPE_INT32 = 5
GGUF_TYPE_FLOAT32 = 6
GGUF_TYPE_BOOL = 7
GGUF_TYPE_STRING = 8
GGUF_TYPE_ARRAY = 9
GGUF_TYPE_UINT64 = 10
GGUF_TYPE_INT64 = 11
GGUF_TYPE_FLOAT64 = 12

# GGML tensor types: (code, name, block_size, type_size_bytes, ir_data_type, is_quantized)
GGML_TYPES: dict[str, tuple[int, str, int, int, str, bool]] = {
    "F32": (0, "F32", 1, 4, "Float32", False),
    "F16": (1, "F16", 1, 2, "Float16", False),
    "Q4_0": (2, "Q4_0", 32, 18, "UInt8", True),
    "Q4_1": (3, "Q4_1", 32, 20, "UInt8", True),
    "Q5_0": (6, "Q5_0", 32, 22, "UInt8", True),
    "Q5_1": (7, "Q5_1", 32, 24, "UInt8", True),
    "Q8_0": (8, "Q8_0", 32, 34, "UInt8", True),
    "Q8_1": (9, "Q8_1", 32, 36, "UInt8", True),
    "Q2_K": (10, "Q2_K", 256, 84, "UInt8", True),
    "Q3_K": (11, "Q3_K", 256, 110, "UInt8", True),
    "Q4_K": (12, "Q4_K", 256, 144, "UInt8", True),
    "Q5_K": (13, "Q5_K", 256, 176, "UInt8", True),
    "Q6_K": (14, "Q6_K", 256, 210, "UInt8", True),
    "Q8_K": (15, "Q8_K", 256, 292, "UInt8", True),
    "I8": (24, "I8", 1, 1, "Int8", False),
    "I16": (25, "I16", 1, 2, "Int16", False),
    "I32": (26, "I32", 1, 4, "Int32", False),
    "I64": (27, "I64", 1, 8, "Int64", False),
    "F64": (28, "F64", 1, 8, "Float64", False),
    "BF16": (30, "BF16", 1, 2, "BFloat16", False),
}


def align_up(value: int, alignment: int) -> int:
    rem = value % alignment
    return value if rem == 0 else value + (alignment - rem)


def encode_string(text: str) -> bytes:
    raw = text.encode("utf-8")
    return struct.pack("<Q", len(raw)) + raw


def encode_kv(key: str, val_type: int, value: Any) -> bytes:
    out = bytearray(encode_string(key))
    out += struct.pack("<I", val_type)
    if val_type == GGUF_TYPE_UINT32:
        out += struct.pack("<I", int(value))
    elif val_type == GGUF_TYPE_INT32:
        out += struct.pack("<i", int(value))
    elif val_type == GGUF_TYPE_FLOAT32:
        out += struct.pack("<f", float(value))
    elif val_type == GGUF_TYPE_BOOL:
        out += struct.pack("<B", 1 if value else 0)
    elif val_type == GGUF_TYPE_STRING:
        out += encode_string(str(value))
    elif val_type == GGUF_TYPE_UINT64:
        out += struct.pack("<Q", int(value))
    elif val_type == GGUF_TYPE_ARRAY:
        elem_type, items = value
        out += struct.pack("<IQ", elem_type, len(items))
        for item in items:
            if elem_type == GGUF_TYPE_STRING:
                out += encode_string(str(item))
            elif elem_type == GGUF_TYPE_FLOAT32:
                out += struct.pack("<f", float(item))
            elif elem_type == GGUF_TYPE_INT32:
                out += struct.pack("<i", int(item))
            elif elem_type == GGUF_TYPE_UINT32:
                out += struct.pack("<I", int(item))
            else:
                raise ValueError(f"Unsupported array elem type: {elem_type}")
    else:
        raise ValueError(f"Unsupported KV type: {val_type}")
    return bytes(out)


def compute_tensor_byte_length(ggml_type: str, ne_dims: list[int]) -> int:
    _, _, block_size, type_size, _, _ = GGML_TYPES[ggml_type]
    if not ne_dims:
        return type_size
    if ne_dims[0] % block_size != 0:
        raise ValueError(f"ne[0]={ne_dims[0]} not divisible by block_size={block_size} for {ggml_type}")
    total = (ne_dims[0] // block_size) * type_size
    for d in ne_dims[1:]:
        total *= d
    return total


def make_deterministic_payload(byte_length: int, ggml_type: str, rng: random.Random) -> bytes:
    if ggml_type == "F32":
        count = byte_length // 4
        vals = [round(rng.uniform(-0.5, 0.5), 4) for _ in range(count)]
        return struct.pack(f"<{count}f", *vals)
    if ggml_type == "F16":
        count = byte_length // 2
        vals = [round(rng.uniform(-0.5, 0.5), 3) for _ in range(count)]
        return struct.pack(f"<{count}e", *vals)
    if ggml_type == "BF16":
        count = byte_length // 2
        out = bytearray()
        for _ in range(count):
            f32_bits = struct.unpack("<I", struct.pack("<f", rng.uniform(-0.5, 0.5)))[0]
            out += struct.pack("<H", (f32_bits >> 16) & 0xFFFF)
        return bytes(out)
    return bytes(rng.randrange(0, 256) for _ in range(byte_length))


def write_gguf_fixture(
    out_path: Path,
    truth_path: Path,
    model_id: str,
    subgraph_name: str,
    kv_entries: list[tuple[str, int, Any]],
    tensor_specs: list[tuple[str, list[int], str]],
    expected_nodes: list[str],
    expected_histogram: dict[str, int],
    expected_inputs: list[dict[str, Any]],
    expected_outputs: list[dict[str, Any]],
    seed: int,
    alignment: int = 32,
) -> dict[str, Any]:
    rng = random.Random(seed)

    header = bytearray(b"GGUF")
    header += struct.pack("<IQQ", 3, len(tensor_specs), len(kv_entries))

    kv_blob = bytearray()
    for key, val_type, val in kv_entries:
        kv_blob += encode_kv(key, val_type, val)

    rel_offset = 0
    tensor_metas: list[tuple[str, list[int], str, int, int, bytes]] = []
    tensor_info_blob = bytearray()

    for name, ne_dims, ggml_type in tensor_specs:
        type_code, _, _, _, _, _ = GGML_TYPES[ggml_type]
        byte_len = compute_tensor_byte_length(ggml_type, ne_dims)
        payload = make_deterministic_payload(byte_len, ggml_type, rng)

        tensor_info_blob += encode_string(name)
        tensor_info_blob += struct.pack("<I", len(ne_dims))
        for d in ne_dims:
            tensor_info_blob += struct.pack("<Q", d)
        tensor_info_blob += struct.pack("<IQ", type_code, rel_offset)

        tensor_metas.append((name, ne_dims, ggml_type, rel_offset, byte_len, payload))
        rel_offset = align_up(rel_offset + byte_len, alignment)

    header_end = len(header) + len(kv_blob) + len(tensor_info_blob)
    data_base_offset = align_up(header_end, alignment)

    file_buf = bytearray(header)
    file_buf += kv_blob
    file_buf += tensor_info_blob
    file_buf += b"\x00" * (data_base_offset - header_end)

    initializers_truth: list[dict[str, Any]] = []
    for name, ne_dims, ggml_type, r_off, byte_len, payload in tensor_metas:
        abs_offset = data_base_offset + r_off
        if len(file_buf) < abs_offset:
            file_buf += b"\x00" * (abs_offset - len(file_buf))
        file_buf += payload

        _, type_name, block_size, _, ir_dtype, is_quant = GGML_TYPES[ggml_type]
        initializers_truth.append(
            {
                "name": name,
                "ggmlType": type_name,
                "dataType": ir_dtype,
                "quantizationScheme": type_name if is_quant else None,
                "blockSize": block_size if is_quant else None,
                "shape": [str(d) for d in reversed(ne_dims)],
                "streamOffset": abs_offset,
                "byteLength": byte_len,
            }
        )

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(bytes(file_buf))

    sha256 = hashlib.sha256(file_buf).hexdigest()
    truth = {
        "modelId": model_id,
        "format": "GGUF v3",
        "alignment": alignment,
        "dataBaseOffset": data_base_offset,
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": subgraph_name,
            "nodeCount": len(expected_nodes),
            "operatorHistogram": expected_histogram,
            "inputs": expected_inputs,
            "outputs": expected_outputs,
            "initializers": initializers_truth,
        },
    }
    truth_path.write_text(json.dumps(truth, indent=2) + "\n", encoding="utf-8")

    return {
        "id": model_id,
        "fileName": out_path.name,
        "byteLength": len(file_buf),
        "sha256": sha256,
    }


def generate_all(fixtures_dir: Path) -> list[dict[str, Any]]:
    results = []

    # 1. Tiny Llama-style Transformer LLM (mixed F16 / Q4_0 / Q8_0 / F32)
    results.append(
        write_gguf_fixture(
            out_path=fixtures_dir / "tiny_llm.gguf",
            truth_path=fixtures_dir / "tiny_llm.truth.json",
            model_id="gguf-tiny_llm",
            subgraph_name="tiny-llama-2L",
            kv_entries=[
                ("general.architecture", GGUF_TYPE_STRING, "llama"),
                ("general.name", GGUF_TYPE_STRING, "tiny-llama-2L"),
                ("general.quantized_by", GGUF_TYPE_STRING, "dot-node-viewer-model-gen 1.0"),
                ("general.file_type", GGUF_TYPE_UINT32, 2),
                ("general.alignment", GGUF_TYPE_UINT32, 32),
                ("llama.context_length", GGUF_TYPE_UINT32, 512),
                ("llama.embedding_length", GGUF_TYPE_UINT32, 64),
                ("llama.block_count", GGUF_TYPE_UINT32, 2),
                ("llama.feed_forward_length", GGUF_TYPE_UINT32, 128),
                ("llama.attention.head_count", GGUF_TYPE_UINT32, 4),
                ("llama.attention.head_count_kv", GGUF_TYPE_UINT32, 2),
                ("llama.vocab_size", GGUF_TYPE_UINT32, 128),
                ("tokenizer.ggml.model", GGUF_TYPE_STRING, "llama"),
                ("tokenizer.ggml.tokens", GGUF_TYPE_ARRAY, (GGUF_TYPE_STRING, ["<unk>", "<s>", "</s>", "hello", "world"])),
            ],
            tensor_specs=[
                ("token_embd.weight", [64, 128], "F16"),
                ("blk.0.attn_norm.weight", [64], "F32"),
                ("blk.0.attn_q.weight", [64, 64], "Q4_0"),
                ("blk.0.attn_k.weight", [64, 32], "Q4_0"),
                ("blk.0.attn_v.weight", [64, 32], "Q4_0"),
                ("blk.0.attn_output.weight", [64, 64], "Q4_0"),
                ("blk.0.ffn_norm.weight", [64], "F32"),
                ("blk.0.ffn_gate.weight", [64, 128], "Q4_0"),
                ("blk.0.ffn_up.weight", [64, 128], "Q4_0"),
                ("blk.0.ffn_down.weight", [128, 64], "Q8_0"),
                ("blk.1.attn_norm.weight", [64], "F32"),
                ("blk.1.attn_q.weight", [64, 64], "Q4_0"),
                ("blk.1.attn_k.weight", [64, 32], "Q4_0"),
                ("blk.1.attn_v.weight", [64, 32], "Q4_0"),
                ("blk.1.attn_output.weight", [64, 64], "Q4_0"),
                ("blk.1.ffn_norm.weight", [64], "F32"),
                ("blk.1.ffn_gate.weight", [64, 128], "Q4_0"),
                ("blk.1.ffn_up.weight", [64, 128], "Q4_0"),
                ("blk.1.ffn_down.weight", [128, 64], "Q8_0"),
                ("output_norm.weight", [64], "F32"),
                ("output.weight", [64, 128], "Q8_0"),
            ],
            expected_nodes=["token_embd", "blk.0", "blk.1", "output_norm", "output"],
            expected_histogram={"TokenEmbedding": 1, "TransformerBlock": 2, "OutputNorm": 1, "LMHead": 1},
            expected_inputs=[{"name": "input_ids", "dataType": "Int32", "shape": ["batch", "seq_len"]}],
            expected_outputs=[{"name": "logits", "dataType": "Float32", "shape": ["batch", "seq_len", "128"]}],
            seed=101,
        )
    )

    # 2. Tiny DeepSeek41-style MoE + Engram fixture
    results.append(
        write_gguf_fixture(
            out_path=fixtures_dir / "tiny_moe_engram.gguf",
            truth_path=fixtures_dir / "tiny_moe_engram.truth.json",
            model_id="gguf-tiny_moe_engram",
            subgraph_name="tiny-deepseek41-moe-engram",
            kv_entries=[
                ("general.architecture", GGUF_TYPE_STRING, "deepseek41"),
                ("general.name", GGUF_TYPE_STRING, "tiny-deepseek41-moe-engram"),
                ("general.quantized_by", GGUF_TYPE_STRING, "dot-node-viewer-model-gen 1.0"),
                ("deepseek41.embedding_length", GGUF_TYPE_UINT32, 64),
                ("deepseek41.block_count", GGUF_TYPE_UINT32, 2),
                ("deepseek41.attention.head_count", GGUF_TYPE_UINT32, 4),
                ("deepseek41.attention.head_count_kv", GGUF_TYPE_UINT32, 2),
                ("deepseek41.expert_count", GGUF_TYPE_UINT32, 4),
                ("deepseek41.expert_used_count", GGUF_TYPE_UINT32, 2),
                ("deepseek41.vocab_size", GGUF_TYPE_UINT32, 96),
            ],
            tensor_specs=[
                ("token_embd.weight", [64, 96], "BF16"),
                ("blk.0.attn_norm.weight", [64], "F32"),
                ("blk.0.attn_q_a.weight", [64, 32], "F16"),
                ("blk.0.attn_kv_a_mqa.weight", [64, 32], "F16"),
                ("blk.0.attn_output.weight", [64, 64], "Q8_0"),
                ("blk.0.ffn_norm.weight", [64], "F32"),
                ("blk.0.ffn_gate_inp.weight", [64, 4], "F32"),
                ("blk.0.ffn_gate_exps.weight", [64, 64, 4], "Q4_0"),
                ("blk.0.ffn_up_exps.weight", [64, 64, 4], "Q4_0"),
                ("blk.0.ffn_down_exps.weight", [64, 64, 4], "Q4_0"),
                ("blk.1.attn_norm.weight", [64], "F32"),
                ("blk.1.attn_q_a.weight", [64, 32], "F16"),
                ("blk.1.attn_output.weight", [64, 64], "Q8_0"),
                ("blk.1.engram_table.weight", [64, 32], "Q8_0"),
                ("blk.1.engram_proj.weight", [64, 64], "F16"),
                ("blk.1.ffn_norm.weight", [64], "F32"),
                ("blk.1.ffn_gate.weight", [64, 128], "Q4_0"),
                ("blk.1.ffn_down.weight", [128, 64], "Q4_0"),
                ("output_norm.weight", [64], "F32"),
                ("output.weight", [64, 96], "BF16"),
            ],
            expected_nodes=["token_embd", "blk.0", "blk.1", "output_norm", "output"],
            expected_histogram={
                "TokenEmbedding": 1,
                "MoETransformerBlock": 1,
                "EngramTransformerBlock": 1,
                "OutputNorm": 1,
                "LMHead": 1,
            },
            expected_inputs=[{"name": "input_ids", "dataType": "Int32", "shape": ["batch", "seq_len"]}],
            expected_outputs=[{"name": "logits", "dataType": "Float32", "shape": ["batch", "seq_len", "96"]}],
            seed=202,
        )
    )

    # 3. Tiny Kokoro TTS audio fixture
    results.append(
        write_gguf_fixture(
            out_path=fixtures_dir / "tiny_audio_kokoro.gguf",
            truth_path=fixtures_dir / "tiny_audio_kokoro.truth.json",
            model_id="gguf-tiny_audio_kokoro",
            subgraph_name="tiny-kokoro-tts",
            kv_entries=[
                ("general.architecture", GGUF_TYPE_STRING, "kokoro"),
                ("general.name", GGUF_TYPE_STRING, "tiny-kokoro-tts"),
                ("general.quantized_by", GGUF_TYPE_STRING, "dot-node-viewer-model-gen 1.0"),
                ("kokoro.embedding_length", GGUF_TYPE_UINT32, 64),
                ("kokoro.vocab_size", GGUF_TYPE_UINT32, 64),
            ],
            tensor_specs=[
                ("bert.embeddings.word_embeddings.weight", [64, 64], "F16"),
                ("bert.encoder.layer.0.attention.self.query.weight", [64, 64], "Q8_0"),
                ("bert_encoder.weight", [64, 64], "F16"),
                ("text_encoder.lstm.weight_ih_l0", [64, 64], "Q8_0"),
                ("predictor.duration_proj.weight", [64, 16], "F16"),
                ("decoder.generator.conv_post.weight", [32, 32, 3], "F32"),
            ],
            expected_nodes=["bert", "bert_encoder", "text_encoder", "predictor", "decoder"],
            expected_histogram={
                "AlbertEncoder": 1,
                "BertProjection": 1,
                "TextEncoder": 1,
                "ProsodyPredictor": 1,
                "AudioDecoder": 1,
            },
            expected_inputs=[{"name": "input_ids", "dataType": "Int32", "shape": ["batch", "seq_len"]}],
            expected_outputs=[{"name": "audio", "dataType": "Float32", "shape": ["batch", "samples"]}],
            seed=303,
        )
    )

    # 4. Tiny LFM3 Hybrid Attention / State-Space fixture with Q4_K super-blocks
    results.append(
        write_gguf_fixture(
            out_path=fixtures_dir / "tiny_hybrid_ssm.gguf",
            truth_path=fixtures_dir / "tiny_hybrid_ssm.truth.json",
            model_id="gguf-tiny_hybrid_ssm",
            subgraph_name="tiny-lfm3-hybrid",
            kv_entries=[
                ("general.architecture", GGUF_TYPE_STRING, "lfm3"),
                ("general.name", GGUF_TYPE_STRING, "tiny-lfm3-hybrid"),
                ("general.quantized_by", GGUF_TYPE_STRING, "dot-node-viewer-model-gen 1.0"),
                ("lfm3.embedding_length", GGUF_TYPE_UINT32, 256),
                ("lfm3.block_count", GGUF_TYPE_UINT32, 2),
                ("lfm3.attention.head_count", GGUF_TYPE_UINT32, 8),
                ("lfm3.vocab_size", GGUF_TYPE_UINT32, 64),
            ],
            tensor_specs=[
                ("token_embd.weight", [256, 64], "F16"),
                ("blk.0.attn_norm.weight", [256], "F32"),
                ("blk.0.ssm_in.weight", [256, 32], "Q4_K"),
                ("blk.0.ssm_conv1d.weight", [4, 256], "F32"),
                ("blk.0.ssm_out.weight", [256, 32], "Q4_K"),
                ("blk.1.attn_norm.weight", [256], "F32"),
                ("blk.1.attn_q.weight", [256, 32], "Q4_K"),
                ("blk.1.attn_output.weight", [256, 32], "Q8_0"),
                ("blk.1.ffn_norm.weight", [256], "F32"),
                ("blk.1.ffn_down.weight", [256, 32], "Q4_K"),
                ("output_norm.weight", [256], "F32"),
                ("output.weight", [256, 64], "Q8_0"),
            ],
            expected_nodes=["token_embd", "blk.0", "blk.1", "output_norm", "output"],
            expected_histogram={
                "TokenEmbedding": 1,
                "SSMBlock": 1,
                "TransformerBlock": 1,
                "OutputNorm": 1,
                "LMHead": 1,
            },
            expected_inputs=[{"name": "input_ids", "dataType": "Int32", "shape": ["batch", "seq_len"]}],
            expected_outputs=[{"name": "logits", "dataType": "Float32", "shape": ["batch", "seq_len", "64"]}],
            seed=404,
        )
    )

    return results


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate deterministic GGUF test fixtures and truth files.")
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path(__file__).resolve().parents[3] / "tests" / "DotNodeViewer.Core.Tests" / "Fixtures" / "gguf",
    )
    args = parser.parse_args()

    generated = generate_all(args.out_dir)
    for item in generated:
        print(f"{item['id']}: {item['fileName']} ({item['byteLength']} bytes) sha256={item['sha256']}")


if __name__ == "__main__":
    main()
