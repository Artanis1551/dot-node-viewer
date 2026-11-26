#!/usr/bin/env python3
"""Deterministic ExecuTorch base fixture and ground-truth (.truth.json) generator for Stage S07."""

from __future__ import annotations

import argparse
import json
import os
import re
import tempfile
from pathlib import Path
from typing import Any

import torch
import torch.nn as nn
from executorch.exir import to_edge, to_edge_transform_and_lower, EdgeCompileConfig
from executorch.backends.xnnpack.partition.xnnpack_partitioner import XnnpackPartitioner
from executorch.backends.xnnpack.quantizer.xnnpack_quantizer import (
    XNNPACKQuantizer,
    get_symmetric_quantization_config,
)
from executorch.backends.xnnpack.serialization.xnnpack_graph_serialize import (
    XNNHeader,
    _get_schema_bytes,
)
from executorch.exir._serialize._flatbuffer import _flatc_decompile
from executorch.exir._serialize._program import deserialize_pte_binary
from torchao.quantization.pt2e.quantize_pt2e import convert_pt2e, prepare_pt2e

SCALAR_TYPE_TO_IR: dict[int, str] = {
    0: "UInt8",
    1: "Int8",
    2: "Int16",
    3: "Int32",
    4: "Int64",
    5: "Float16",
    6: "Float32",
    7: "Float64",
    11: "Bool",
    12: "Int8",
    13: "UInt8",
    14: "Int32",
    15: "BFloat16",
    16: "UInt4",
    17: "UInt4",
    22: "UInt16",
    23: "Float8E5M2",
    24: "Float8E4M3",
    25: "Float8E5M2",
    26: "Float8E4M3",
    27: "UInt16",
    28: "UInt32",
    29: "UInt64",
}


def build_tiny_mlp() -> bytes:
    torch.manual_seed(42)

    class TinyMlp(nn.Module):
        def __init__(self):
            super().__init__()
            self.fc1 = nn.Linear(4, 3)
            self.fc2 = nn.Linear(3, 2)

        def forward(self, x):
            h = torch.relu(self.fc1(x))
            return self.fc2(h) + 1.0

    model = TinyMlp().eval()
    example_inputs = (torch.randn(1, 4),)
    ep = torch.export.export(model, example_inputs)
    edge = to_edge(ep, compile_config=EdgeCompileConfig(_check_ir_validity=False))
    et_prog = edge.to_executorch()
    return et_prog.buffer


def build_tiny_cnn_xnnpack() -> bytes:
    torch.manual_seed(42)

    class TinyCNN(nn.Module):
        def __init__(self):
            super().__init__()
            self.conv = nn.Conv2d(3, 8, 3, padding=1)
            self.relu = nn.ReLU()

        def forward(self, x):
            return self.relu(self.conv(x))

    model = TinyCNN().eval()
    example_inputs = (torch.randn(1, 3, 16, 16),)
    ep = torch.export.export(model, example_inputs)
    edge = to_edge(ep, compile_config=EdgeCompileConfig(_check_ir_validity=False))
    edge_lowered = edge.to_backend(XnnpackPartitioner())
    et_prog = edge_lowered.to_executorch()
    return et_prog.buffer


def build_tiny_xnnpack_ops() -> bytes:
    torch.manual_seed(42)

    class RichXnnModel(nn.Module):
        def __init__(self):
            super().__init__()
            self.conv = nn.Conv2d(3, 8, 3, padding=1)
            self.dw = nn.Conv2d(8, 8, 3, padding=1, groups=8)
            self.fc = nn.Linear(8, 4)

        def forward(self, x):
            h1 = torch.relu(self.conv(x))
            h2 = torch.nn.functional.hardswish(self.dw(h1))
            h = h1 + h2
            h = h.mean(dim=(2, 3), keepdim=True)
            h = h.reshape(1, 8)
            return torch.softmax(self.fc(h), dim=-1)

    model = RichXnnModel().eval()
    example_inputs = (torch.randn(1, 3, 8, 8),)
    ep = torch.export.export(model, example_inputs)
    et_prog = to_edge_transform_and_lower(ep, partitioner=[XnnpackPartitioner()]).to_executorch()
    return et_prog.buffer


def build_tiny_cnn_xnnpack_int8() -> bytes:
    torch.manual_seed(42)

    class TinyQuantCnn(nn.Module):
        def __init__(self):
            super().__init__()
            self.conv = nn.Conv2d(3, 4, 3, padding=1)
            self.relu = nn.ReLU()
            self.fc = nn.Linear(4, 2)

        def forward(self, x):
            h = self.relu(self.conv(x))
            h = h.mean(dim=(2, 3), keepdim=True).reshape(1, 4)
            return self.fc(h)

    model = TinyQuantCnn().eval()
    example_inputs = (torch.randn(1, 3, 8, 8),)
    ep = torch.export.export(model, example_inputs)
    quantizer = XNNPACKQuantizer()
    quantizer.set_global(get_symmetric_quantization_config(is_per_channel=True))
    prepared = prepare_pt2e(ep.module(), quantizer)
    prepared(*example_inputs)
    converted = convert_pt2e(prepared)
    ep_quant = torch.export.export(converted, example_inputs)
    et_prog = to_edge_transform_and_lower(ep_quant, partitioner=[XnnpackPartitioner()]).to_executorch()
    return et_prog.buffer


XNN_DATATYPE_TO_IR: dict[str, str] = {
    "xnn_datatype_fp32": "Float32",
    "xnn_datatype_fp16": "Float16",
    "xnn_datatype_qint8": "Int8",
    "xnn_datatype_quint8": "UInt8",
    "xnn_datatype_qint32": "Int32",
    "xnn_datatype_qcint8": "Int8",
    "xnn_datatype_qcint32": "Int32",
    "xnn_datatype_qcint4": "Int4",
    "xnn_datatype_qdint8": "Int8",
    "xnn_datatype_qbint4": "Int4",
    "xnn_datatype_qpint8": "Int8",
    "xnn_datatype_int32": "Int32",
    "xnn_datatype_pfp32": "Float32",
    "xnn_datatype_bf16": "BFloat16",
}


def extract_xnnpack_delegate_truth(pte_bytes: bytes) -> dict[str, Any] | None:
    pte = deserialize_pte_binary(pte_bytes)
    plan = pte.program.execution_plan[0]
    for delegate in plan.delegates:
        if delegate.id != "XnnpackBackend":
            continue
        if delegate.processed.location == 0:
            blob = pte.program.backend_delegate_data[delegate.processed.index].data
        else:
            blob = pte.segments[delegate.processed.index]

        if blob[4:8] == b"XH00":
            hdr = XNNHeader.from_bytes(blob[:30])
            fb_bytes = blob[hdr.flatbuffer_offset : hdr.flatbuffer_offset + hdr.flatbuffer_size]
        else:
            fb_bytes = blob

        with tempfile.TemporaryDirectory() as tmp:
            schema_path = os.path.join(tmp, "schema.fbs")
            bin_path = os.path.join(tmp, "xnn.bin")
            Path(schema_path).write_bytes(_get_schema_bytes())
            Path(bin_path).write_bytes(fb_bytes)
            _flatc_decompile(tmp, schema_path, bin_path)
            raw_json = Path(os.path.join(tmp, "xnn.json")).read_text(encoding="utf-8")
            normalized = re.sub(r":\s*([-+]?)inf\b", r": \1Infinity", raw_json)
            xnn_graph = json.loads(normalized)

        xnodes = xnn_graph.get("xnodes", [])
        xvalues = xnn_graph.get("xvalues", [])
        op_hist: dict[str, int] = {}
        for xnode in xnodes:
            op_name = xnode["xnode_union_type"]
            op_hist[op_name] = op_hist.get(op_name, 0) + 1

        def get_tv(xv: dict[str, Any]) -> dict[str, Any]:
            if xv["xvalue_union_type"] == "XNNQuantizedTensorValue":
                return xv["xvalue_union"]["tensor_value"]
            return xv["xvalue_union"]

        qval_count = sum(1 for xv in xvalues if xv["xvalue_union_type"] == "XNNQuantizedTensorValue")
        const_count = sum(1 for xv in xvalues if get_tv(xv).get("constant_buffer_idx", 0) > 0)

        def fmt_xval(idx: int) -> dict[str, Any]:
            tv = get_tv(xvalues[idx])
            return {
                "dataType": XNN_DATATYPE_TO_IR.get(tv.get("datatype", ""), "Unknown"),
                "shape": [int(d) for d in tv.get("dims", [])],
            }

        return {
            "backend": "xnnpack",
            "nodeCount": len(xnodes),
            "valueCount": len(xvalues),
            "constantCount": const_count,
            "quantizedValueCount": qval_count,
            "operatorHistogram": op_hist,
            "inputs": [fmt_xval(i) for i in xnn_graph.get("input_ids", [])],
            "outputs": [fmt_xval(i) for i in xnn_graph.get("output_ids", [])],
        }
    return None


def extract_truth(pte_bytes: bytes, model_id: str) -> dict[str, Any]:
    pte = deserialize_pte_binary(pte_bytes)
    prog = pte.program
    plan = prog.execution_plan[0]

    # Operators histogram
    histogram: dict[str, int] = {}
    for chain in plan.chains:
        for inst in chain.instructions:
            instr_args = inst.instr_args
            op_name = "unknown"
            if hasattr(instr_args, "op_index"):
                op = plan.operators[instr_args.op_index]
                overload = op.overload
                op_name = f"{op.name}.{overload}" if overload else op.name
            elif hasattr(instr_args, "delegate_index"):
                delegate = plan.delegates[instr_args.delegate_index]
                op_name = f"Delegate ({delegate.id})"
            elif type(instr_args).__name__ == "MoveCall":
                op_name = "executorch_prim::move"
            elif type(instr_args).__name__ == "JumpFalseCall":
                op_name = "executorch_prim::jump_false"

            histogram[op_name] = histogram.get(op_name, 0) + 1

    def format_shape(sizes: list[int] | None) -> list[Any]:
        if sizes is None:
            return []
        return [int(s) for s in sizes]

    def build_port_entry(tensor_idx: int) -> dict[str, Any]:
        val = plan.values[tensor_idx].val
        sizes = getattr(val, "sizes", None)
        scalar_type = getattr(val, "scalar_type", 6)
        fqn = getattr(val.extra_tensor_info, "fully_qualified_name", None) if getattr(val, "extra_tensor_info", None) else None
        name = fqn if fqn else f"v{tensor_idx}"
        return {
            "name": name,
            "dataType": SCALAR_TYPE_TO_IR.get(scalar_type, "Float32"),
            "shape": format_shape(sizes),
        }

    inputs = [build_port_entry(i) for i in plan.inputs]
    outputs = [build_port_entry(i) for i in plan.outputs]

    initializers = []
    for idx, v in enumerate(plan.values):
        val = v.val
        data_buffer_idx = getattr(val, "data_buffer_idx", 0)
        if data_buffer_idx and data_buffer_idx > 0:
            sizes = getattr(val, "sizes", None)
            scalar_type = getattr(val, "scalar_type", 6)
            fqn = getattr(val.extra_tensor_info, "fully_qualified_name", None) if getattr(val, "extra_tensor_info", None) else None
            name = fqn if fqn else f"v{idx}"
            numel = 1
            for s in (sizes or []):
                numel *= s if s > 0 else 1
            elem_size = 4
            if scalar_type in (0, 1, 11, 12, 13, 23, 24, 25, 26):
                elem_size = 1
            elif scalar_type in (2, 5, 15, 22, 27):
                elem_size = 2
            elif scalar_type in (4, 7, 29):
                elem_size = 8

            initializers.append({
                "name": name,
                "dataType": SCALAR_TYPE_TO_IR.get(scalar_type, "Float32"),
                "shape": format_shape(sizes),
                "byteLength": numel * elem_size,
            })

    total_nodes = sum(len(c.instructions) for c in plan.chains)
    version = getattr(prog, "version", 0)
    fmt = f"ExecuTorch v{version}" if version > 0 else "ExecuTorch"

    truth: dict[str, Any] = {
        "modelId": model_id,
        "format": fmt,
        "subgraphCount": len(prog.execution_plan),
        "primarySubgraph": {
            "name": plan.name or "forward",
            "nodeCount": total_nodes,
            "operatorHistogram": histogram,
            "inputs": inputs,
            "outputs": outputs,
            "initializers": initializers,
        },
    }
    xnn_truth = extract_xnnpack_delegate_truth(pte_bytes)
    if xnn_truth is not None:
        truth["delegate"] = xnn_truth
    return truth


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate deterministic ExecuTorch fixtures and ground-truth JSON files.")
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path("tests/DotNodeViewer.Core.Tests/Fixtures/executorch"),
        help="Output directory for generated .pte and .truth.json files.",
    )
    parser.add_argument(
        "--only-w11",
        action="store_true",
        help="Generate only the W11 XNNPACK fixtures and enrich tiny_cnn_xnnpack.truth.json without modifying existing .pte binaries.",
    )
    args = parser.parse_args()
    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    if args.only_w11:
        specs = [
            ("tiny_xnnpack_ops", "executorch-tiny_xnnpack_ops", build_tiny_xnnpack_ops()),
            ("tiny_cnn_xnnpack_int8", "executorch-tiny_cnn_xnnpack_int8", build_tiny_cnn_xnnpack_int8()),
        ]
        existing_xnn_pte = out_dir / "tiny_cnn_xnnpack.pte"
        if existing_xnn_pte.exists():
            existing_truth = extract_truth(existing_xnn_pte.read_bytes(), "executorch-tiny_cnn_xnnpack")
            (out_dir / "tiny_cnn_xnnpack.truth.json").write_text(
                json.dumps(existing_truth, indent=2) + "\n",
                encoding="utf-8",
            )
            print(f"Updated {out_dir / 'tiny_cnn_xnnpack.truth.json'} with XNNPACK delegate ground truth")
    else:
        specs = [
            ("tiny_mlp_portable", "executorch-tiny_mlp_portable", build_tiny_mlp()),
            ("tiny_cnn_xnnpack", "executorch-tiny_cnn_xnnpack", build_tiny_cnn_xnnpack()),
            ("tiny_xnnpack_ops", "executorch-tiny_xnnpack_ops", build_tiny_xnnpack_ops()),
            ("tiny_cnn_xnnpack_int8", "executorch-tiny_cnn_xnnpack_int8", build_tiny_cnn_xnnpack_int8()),
        ]

    for stem, model_id, pte_bytes in specs:
        pte_path = out_dir / f"{stem}.pte"
        truth_path = out_dir / f"{stem}.truth.json"
        pte_path.write_bytes(pte_bytes)
        truth = extract_truth(pte_bytes, model_id)
        truth_path.write_text(json.dumps(truth, indent=2) + "\n", encoding="utf-8")
        print(f"Generated {pte_path} ({len(pte_bytes)} bytes) and {truth_path}")


if __name__ == "__main__":
    main()

