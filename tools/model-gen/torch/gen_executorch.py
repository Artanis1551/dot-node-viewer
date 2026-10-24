#!/usr/bin/env python3
"""Deterministic ExecuTorch base fixture and ground-truth (.truth.json) generator for Stage S07."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from typing import Any

import torch
import torch.nn as nn
from executorch.exir import to_edge, EdgeCompileConfig
from executorch.backends.xnnpack.partition.xnnpack_partitioner import XnnpackPartitioner
from executorch.exir._serialize._program import deserialize_pte_binary

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

    return {
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


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate deterministic ExecuTorch fixtures and ground-truth JSON files.")
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path("tests/DotNodeViewer.Core.Tests/Fixtures/executorch"),
        help="Output directory for generated .pte and .truth.json files.",
    )
    args = parser.parse_args()
    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    specs = [
        ("tiny_mlp_portable", "executorch-tiny_mlp_portable", build_tiny_mlp()),
        ("tiny_cnn_xnnpack", "executorch-tiny_cnn_xnnpack", build_tiny_cnn_xnnpack()),
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

