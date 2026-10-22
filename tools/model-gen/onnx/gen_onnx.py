#!/usr/bin/env python3
"""Deterministic ONNX base fixture and ground-truth (.truth.json) generator for Stage S02."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np
import onnx
from onnx import TensorProto, helper

ONNX_DTYPE_TO_IR: dict[int, str] = {
    int(TensorProto.FLOAT): "Float32",
    int(TensorProto.UINT8): "UInt8",
    int(TensorProto.INT8): "Int8",
    int(TensorProto.UINT16): "UInt16",
    int(TensorProto.INT16): "Int16",
    int(TensorProto.INT32): "Int32",
    int(TensorProto.INT64): "Int64",
    int(TensorProto.STRING): "String",
    int(TensorProto.BOOL): "Bool",
    int(TensorProto.FLOAT16): "Float16",
    int(TensorProto.DOUBLE): "Float64",
    int(TensorProto.UINT32): "UInt32",
    int(TensorProto.UINT64): "UInt64",
    int(TensorProto.BFLOAT16): "BFloat16",
}


def make_raw_tensor(name: str, array: np.ndarray, data_type: int = TensorProto.FLOAT) -> TensorProto:
    return helper.make_tensor(
        name=name,
        data_type=data_type,
        dims=list(array.shape),
        vals=array.tobytes(),
        raw=True,
    )


def finalize_model(graph: onnx.GraphProto) -> onnx.ModelProto:
    model = helper.make_model(
        graph,
        producer_name="dot-node-viewer-model-gen",
        producer_version="1.0",
        opset_imports=[helper.make_opsetid("", 18)],
    )
    model.ir_version = 9
    onnx.checker.check_model(model)
    return model


def build_tiny_mlp(rng: np.random.Generator) -> onnx.ModelProto:
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 8])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 4])

    fc1_w = make_raw_tensor("fc1_w", rng.standard_normal((4, 8), dtype=np.float32))
    fc1_b = make_raw_tensor("fc1_b", rng.standard_normal((4,), dtype=np.float32))
    res_bias = make_raw_tensor("res_bias", rng.standard_normal((1, 4), dtype=np.float32))

    gemm_node = helper.make_node(
        "Gemm",
        inputs=["x", "fc1_w", "fc1_b"],
        outputs=["gemm_out"],
        name="gemm_0",
        transB=1,
    )
    relu_node = helper.make_node(
        "Relu",
        inputs=["gemm_out"],
        outputs=["relu_out"],
        name="relu_0",
    )
    add_node = helper.make_node(
        "Add",
        inputs=["relu_out", "res_bias"],
        outputs=["y"],
        name="add_0",
    )

    graph = helper.make_graph(
        nodes=[gemm_node, relu_node, add_node],
        name="tiny_mlp",
        inputs=[x],
        outputs=[y],
        initializer=[fc1_w, fc1_b, res_bias],
    )
    return finalize_model(graph)


def build_tiny_cnn_dynamic_batch(rng: np.random.Generator) -> onnx.ModelProto:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", 3, 8, 8])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", 4, 1, 1])

    conv_w = make_raw_tensor("conv_w", rng.standard_normal((4, 3, 3, 3), dtype=np.float32))
    conv_b = make_raw_tensor("conv_b", rng.standard_normal((4,), dtype=np.float32))
    bn_scale = make_raw_tensor("bn_scale", np.ones((4,), dtype=np.float32))
    bn_bias = make_raw_tensor("bn_bias", np.zeros((4,), dtype=np.float32))
    bn_mean = make_raw_tensor("bn_mean", np.zeros((4,), dtype=np.float32))
    bn_var = make_raw_tensor("bn_var", np.ones((4,), dtype=np.float32))

    conv_node = helper.make_node(
        "Conv",
        inputs=["input", "conv_w", "conv_b"],
        outputs=["conv_out"],
        name="conv_0",
        kernel_shape=[3, 3],
        pads=[1, 1, 1, 1],
        strides=[1, 1],
    )
    bn_node = helper.make_node(
        "BatchNormalization",
        inputs=["conv_out", "bn_scale", "bn_bias", "bn_mean", "bn_var"],
        outputs=["bn_out"],
        name="bn_0",
        epsilon=1e-5,
    )
    relu_node = helper.make_node(
        "Relu",
        inputs=["bn_out"],
        outputs=["relu_out"],
        name="relu_0",
    )
    gap_node = helper.make_node(
        "GlobalAveragePool",
        inputs=["relu_out"],
        outputs=["output"],
        name="gap_0",
    )

    graph = helper.make_graph(
        nodes=[conv_node, bn_node, relu_node, gap_node],
        name="tiny_cnn_dynamic_batch",
        inputs=[inp],
        outputs=[out],
        initializer=[conv_w, conv_b, bn_scale, bn_bias, bn_mean, bn_var],
    )
    return finalize_model(graph)


def build_if_subgraph(rng: np.random.Generator) -> onnx.ModelProto:
    cond = helper.make_tensor_value_info("cond", TensorProto.BOOL, [])
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 4])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 4])

    scale_w = make_raw_tensor("scale_w", rng.standard_normal((1, 4), dtype=np.float32))

    then_out = helper.make_tensor_value_info("then_out", TensorProto.FLOAT, [1, 4])
    then_graph = helper.make_graph(
        nodes=[helper.make_node("Relu", inputs=["x_scaled"], outputs=["then_out"], name="then_relu")],
        name="then_graph",
        inputs=[],
        outputs=[then_out],
    )

    else_out = helper.make_tensor_value_info("else_out", TensorProto.FLOAT, [1, 4])
    else_graph = helper.make_graph(
        nodes=[helper.make_node("Neg", inputs=["x_scaled"], outputs=["else_out"], name="else_neg")],
        name="else_graph",
        inputs=[],
        outputs=[else_out],
    )

    mul_node = helper.make_node(
        "Mul",
        inputs=["x", "scale_w"],
        outputs=["x_scaled"],
        name="mul_0",
    )
    if_node = helper.make_node(
        "If",
        inputs=["cond"],
        outputs=["y"],
        name="if_0",
        then_branch=then_graph,
        else_branch=else_graph,
    )

    graph = helper.make_graph(
        nodes=[mul_node, if_node],
        name="if_subgraph_main",
        inputs=[cond, x],
        outputs=[y],
        initializer=[scale_w],
    )
    return finalize_model(graph)


def build_external_data(rng: np.random.Generator, out_dir: Path) -> onnx.ModelProto:
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 4])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 2])

    ext_weights = rng.standard_normal((4, 2), dtype=np.float32)
    ext_bytes = ext_weights.tobytes()
    ext_bin_path = out_dir / "external_data.bin"
    ext_bin_path.write_bytes(ext_bytes)

    ext_tensor = TensorProto()
    ext_tensor.name = "ext_w"
    ext_tensor.data_type = TensorProto.FLOAT
    ext_tensor.dims.extend([4, 2])
    ext_tensor.data_location = TensorProto.EXTERNAL
    for k, v in [("location", "external_data.bin"), ("offset", "0"), ("length", str(len(ext_bytes)))]:
        entry = ext_tensor.external_data.add()
        entry.key = k
        entry.value = v

    inline_b = make_raw_tensor("inline_b", rng.standard_normal((1, 2), dtype=np.float32))

    matmul_node = helper.make_node(
        "MatMul",
        inputs=["x", "ext_w"],
        outputs=["mm_out"],
        name="matmul_0",
    )
    add_node = helper.make_node(
        "Add",
        inputs=["mm_out", "inline_b"],
        outputs=["y"],
        name="add_0",
    )

    graph = helper.make_graph(
        nodes=[matmul_node, add_node],
        name="external_data_main",
        inputs=[x],
        outputs=[y],
        initializer=[ext_tensor, inline_b],
    )
    model = helper.make_model(
        graph,
        producer_name="dot-node-viewer-model-gen",
        producer_version="1.0",
        opset_imports=[helper.make_opsetid("", 18)],
    )
    model.ir_version = 9
    return model


def extract_shape_list(vi: onnx.ValueInfoProto) -> list[str] | None:
    if not vi.type.HasField("tensor_type"):
        return None
    tt = vi.type.tensor_type
    if not tt.HasField("shape"):
        return None
    dims: list[str] = []
    for d in tt.shape.dim:
        if d.HasField("dim_param") and d.dim_param:
            dims.append(d.dim_param)
        elif d.HasField("dim_value"):
            dims.append(str(d.dim_value))
        else:
            dims.append("?")
    return dims


def extract_truth(model: onnx.ModelProto, model_id: str) -> dict[str, Any]:
    g = model.graph
    init_names = {init.name for init in g.initializer}

    histogram: dict[str, int] = {}
    for node in g.node:
        histogram[node.op_type] = histogram.get(node.op_type, 0) + 1

    inputs = []
    for vi in g.input:
        if vi.name in init_names:
            continue
        elem_type = vi.type.tensor_type.elem_type if vi.type.HasField("tensor_type") else 0
        inputs.append(
            {
                "name": vi.name,
                "dataType": ONNX_DTYPE_TO_IR.get(elem_type, "Unknown"),
                "shape": extract_shape_list(vi),
            }
        )

    outputs = []
    for vi in g.output:
        elem_type = vi.type.tensor_type.elem_type if vi.type.HasField("tensor_type") else 0
        outputs.append(
            {
                "name": vi.name,
                "dataType": ONNX_DTYPE_TO_IR.get(elem_type, "Unknown"),
                "shape": extract_shape_list(vi),
            }
        )

    initializers = []
    for init in g.initializer:
        ext_map = {e.key: e.value for e in init.external_data}
        is_ext = init.data_location == TensorProto.EXTERNAL or "location" in ext_map
        initializers.append(
            {
                "name": init.name,
                "dataType": ONNX_DTYPE_TO_IR.get(init.data_type, "Unknown"),
                "shape": [str(d) for d in init.dims],
                "byteLength": int(ext_map["length"]) if is_ext and "length" in ext_map else len(init.raw_data),
                "isExternal": is_ext,
                "externalLocation": ext_map.get("location"),
            }
        )

    return {
        "modelId": model_id,
        "format": f"ONNX IR v{model.ir_version}",
        "subgraphCount": 1 + len(model.functions),
        "primarySubgraph": {
            "name": g.name or "main",
            "nodeCount": len(g.node),
            "operatorHistogram": histogram,
            "inputs": inputs,
            "outputs": outputs,
            "initializers": initializers,
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate deterministic ONNX fixtures and ground-truth JSON files.")
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path("tests/DotNodeViewer.Core.Tests/Fixtures/onnx"),
        help="Output directory for generated .onnx and .truth.json files.",
    )
    args = parser.parse_args()
    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    rng = np.random.default_rng(seed=42)

    specs = [
        ("tiny_mlp", "onnx-tiny_mlp", build_tiny_mlp(rng)),
        ("tiny_cnn_dynamic_batch", "onnx-tiny_cnn_dynamic_batch", build_tiny_cnn_dynamic_batch(rng)),
        ("if_subgraph", "onnx-if_subgraph", build_if_subgraph(rng)),
        ("external_data", "onnx-external_data", build_external_data(rng, out_dir)),
    ]

    for stem, model_id, model in specs:
        onnx_path = out_dir / f"{stem}.onnx"
        truth_path = out_dir / f"{stem}.truth.json"
        onnx_path.write_bytes(model.SerializeToString())
        truth = extract_truth(model, model_id)
        truth_path.write_text(json.dumps(truth, indent=2) + "\n", encoding="utf-8")
        print(f"Generated {onnx_path} ({onnx_path.stat().st_size} bytes) and {truth_path}")


if __name__ == "__main__":
    main()
