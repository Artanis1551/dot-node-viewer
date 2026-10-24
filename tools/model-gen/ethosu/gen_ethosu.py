#!/usr/bin/env python3
"""Deterministic Ethos-U model and ground truth generator for DotNodeViewer (Wish W01)."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path


def compile_vela_tflite(input_tflite: Path, accelerator: str, out_path: Path) -> Path:
    vela_bin = Path(sys.executable).parent / "vela"
    with tempfile.TemporaryDirectory() as tmpdir:
        cmd = [
            str(vela_bin),
            str(input_tflite),
            "--accelerator-config",
            accelerator,
            "--output-dir",
            tmpdir,
        ]
        res = subprocess.run(cmd, capture_output=True, text=True, check=True)
        # vela outputs <stem>_vela.tflite
        produced = list(Path(tmpdir).glob("*_vela.tflite"))
        if not produced:
            raise RuntimeError(f"Vela failed to produce output. Stdout: {res.stdout}\nStderr: {res.stderr}")
        out_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(produced[0], out_path)
    return out_path


def decode_command_stream(data: bytes, cop1_idx: int) -> dict:
    import struct

    cursor = cop1_idx + 4
    arch = "Ethos-U"
    product = 0
    macs = 128
    shram_kb = 32
    cmd_stream_words = []

    while cursor + 4 <= len(data):
        tag = struct.unpack('<I', data[cursor:cursor+4])[0]
        cursor += 4
        cmd_id = tag & 0xFF
        reserved = (tag >> 8) & 0xFF
        param = (tag >> 16) & 0xFFFF

        if cmd_id == 1:  # Config
            cfg, _ = struct.unpack('<II', data[cursor:cursor+8])
            cursor += 8
            log2_macs = cfg & 0xF
            shram_kb = (cfg >> 8) & 0xFF
            product = (cfg >> 28) & 0xF
            macs = 1 << log2_macs
            arch_prefix = "Ethos-U55" if product == 0 else ("Ethos-U65" if product == 1 else "Ethos-U85")
            arch = f"{arch_prefix}-{macs}"
        elif cmd_id == 2:  # CmdStream
            words_count = (reserved << 16) | param
            for i in range(words_count):
                cmd_stream_words.append(struct.unpack('<I', data[cursor+i*4:cursor+(i+1)*4])[0])
            break

    # Decode opcodes
    w_idx = 0
    commands = []
    while w_idx < len(cmd_stream_words):
        w0 = cmd_stream_words[w_idx]
        code = w0 & 0xFFFF
        param = w0 >> 16
        mode = code & 0xC000
        opcode = code & 0x03FF
        if mode == 0x0000:
            commands.append((opcode, param, None))
            w_idx += 1
        elif mode == 0x4000:
            payload = cmd_stream_words[w_idx + 1] if w_idx + 1 < len(cmd_stream_words) else 0
            commands.append((opcode, param, payload))
            w_idx += 2
        else:
            w_idx += 1

    # Extract executable op names matching EthosUSubgraphBuilder
    exec_ops = []
    for opcode, param, _ in commands:
        if opcode == 0x002:
            exec_ops.append("NPU_CONV")
        elif opcode == 0x003:
            exec_ops.append("NPU_DEPTHWISE")
        elif opcode == 0x005:
            if param == 0:
                exec_ops.append("NPU_MAXPOOL")
            elif param == 1:
                exec_ops.append("NPU_AVGPOOL")
            elif param == 2:
                exec_ops.append("NPU_REDUCE_SUM")
            else:
                exec_ops.append("NPU_POOL")
        elif opcode == 0x006:
            if param == 0:
                exec_ops.append("NPU_MUL")
            elif param == 1:
                exec_ops.append("NPU_ADD")
            elif param == 2:
                exec_ops.append("NPU_SUB")
            elif param == 3:
                exec_ops.append("NPU_MIN")
            elif param == 4:
                exec_ops.append("NPU_MAX")
            elif param == 5:
                exec_ops.append("NPU_LRELU")
            elif param == 6:
                exec_ops.append("NPU_ABS")
            elif param == 7:
                exec_ops.append("NPU_CLZ")
            elif param == 8:
                exec_ops.append("NPU_SHR")
            elif param == 9:
                exec_ops.append("NPU_SHL")
            else:
                exec_ops.append("NPU_ELEMENTWISE")
        elif opcode == 0x010:
            exec_ops.append("NPU_DMA")

    histogram = dict(Counter(exec_ops))

    return {
        "architecture": arch,
        "product": product,
        "macs": macs,
        "shramKb": shram_kb,
        "commandWords": len(cmd_stream_words),
        "totalNpuOperations": len(exec_ops),
        "operatorHistogram": histogram,
    }


def generate_tflite_truth(tflite_path: Path, model_id: str) -> dict:
    data = tflite_path.read_bytes()
    cop1_idx = data.find(b'COP1')
    if cop1_idx == -1:
        raise ValueError(f"No COP1 magic found in {tflite_path}")

    delegate_info = decode_command_stream(data, cop1_idx)

    return {
        "modelId": model_id,
        "format": "TensorFlow Lite v3",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 1,
            "operatorHistogram": {
                "CUSTOM(ethos-u)": 1,
            },
        },
        "delegate": delegate_info,
    }


def generate_pte_truth(pte_path: Path, model_id: str) -> dict:
    data = pte_path.read_bytes()
    cop1_idx = data.find(b'COP1')
    if cop1_idx == -1:
        raise ValueError(f"No COP1 magic found in {pte_path}")

    delegate_info = decode_command_stream(data, cop1_idx)

    return {
        "modelId": model_id,
        "format": "ExecuTorch",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "forward",
            "nodeCount": 3,
            "operatorHistogram": {
                "quantized_decomposed::quantize_per_tensor.out": 1,
                "Delegate (EthosUBackend)": 1,
                "quantized_decomposed::dequantize_per_tensor.out": 1,
            },
        },
        "delegate": delegate_info,
    }


def main():
    repo_root = Path(__file__).resolve().parents[3]
    tflite_fixtures = repo_root / "tests/DotNodeViewer.Core.Tests/Fixtures/tflite"
    et_fixtures = repo_root / "tests/DotNodeViewer.Core.Tests/Fixtures/executorch"

    base_tflite = tflite_fixtures / "tiny_cnn_int8.tflite"
    if not base_tflite.exists():
        raise FileNotFoundError(f"Base TFLite model not found at {base_tflite}")

    # 1. Generate tiny_cnn_int8_vela_u55.tflite
    print("Compiling tiny_cnn_int8_vela_u55.tflite...")
    u55_tflite = tflite_fixtures / "tiny_cnn_int8_vela_u55.tflite"
    compile_vela_tflite(base_tflite, "ethos-u55-128", u55_tflite)
    truth_u55 = generate_tflite_truth(u55_tflite, "tflite-tiny_cnn_int8_vela_u55")
    u55_truth_path = tflite_fixtures / "tiny_cnn_int8_vela_u55.truth.json"
    u55_truth_path.write_text(json.dumps(truth_u55, indent=2) + "\n")
    print(f"Written {u55_tflite} and {u55_truth_path}")

    # 2. Generate tiny_cnn_int8_vela_u65.tflite
    print("Compiling tiny_cnn_int8_vela_u65.tflite...")
    u65_tflite = tflite_fixtures / "tiny_cnn_int8_vela_u65.tflite"
    compile_vela_tflite(base_tflite, "ethos-u65-256", u65_tflite)
    truth_u65 = generate_tflite_truth(u65_tflite, "tflite-tiny_cnn_int8_vela_u65")
    u65_truth_path = tflite_fixtures / "tiny_cnn_int8_vela_u65.truth.json"
    u65_truth_path.write_text(json.dumps(truth_u65, indent=2) + "\n")
    print(f"Written {u65_tflite} and {u65_truth_path}")

    # 3. Generate tiny_cnn_ethosu55.pte
    print("Compiling tiny_cnn_ethosu55.pte via gen_pte.py...")
    pte_path = et_fixtures / "tiny_cnn_ethosu55.pte"
    torch_python = repo_root / "tools/model-gen/torch/.venv/bin/python3"
    gen_pte_script = Path(__file__).parent / "gen_pte.py"
    subprocess.run([str(torch_python), str(gen_pte_script), "--output", str(pte_path)], check=True)
    truth_pte = generate_pte_truth(pte_path, "executorch-tiny_cnn_ethosu55")
    pte_truth_path = et_fixtures / "tiny_cnn_ethosu55.truth.json"
    pte_truth_path.write_text(json.dumps(truth_pte, indent=2) + "\n")
    print(f"Written {pte_path} and {pte_truth_path}")


if __name__ == "__main__":
    main()
