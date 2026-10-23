#!/usr/bin/env python3
"""Deterministic TFLite base fixture and ground-truth (.truth.json) generator for Stage S03."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from typing import Any

os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "3")
os.environ.setdefault("CUDA_VISIBLE_DEVICES", "-1")

import numpy as np
import tensorflow as tf
from tensorflow.lite.python import schema_py_generated as tflite_schema

TFLITE_DTYPE_TO_IR: dict[int, str] = {
    int(tflite_schema.TensorType.FLOAT32): "Float32",
    int(tflite_schema.TensorType.FLOAT16): "Float16",
    int(tflite_schema.TensorType.INT32): "Int32",
    int(tflite_schema.TensorType.UINT8): "UInt8",
    int(tflite_schema.TensorType.INT64): "Int64",
    int(tflite_schema.TensorType.STRING): "String",
    int(tflite_schema.TensorType.BOOL): "Bool",
    int(tflite_schema.TensorType.INT16): "Int16",
    int(tflite_schema.TensorType.COMPLEX64): "Complex64",
    int(tflite_schema.TensorType.INT8): "Int8",
    int(tflite_schema.TensorType.FLOAT64): "Float64",
    int(tflite_schema.TensorType.COMPLEX128): "Complex128",
    int(tflite_schema.TensorType.UINT64): "UInt64",
    int(tflite_schema.TensorType.RESOURCE): "Resource",
    int(tflite_schema.TensorType.VARIANT): "Variant",
    int(tflite_schema.TensorType.UINT32): "UInt32",
    int(tflite_schema.TensorType.UINT16): "UInt16",
    int(tflite_schema.TensorType.INT4): "Int4",
}

BUILTIN_OP_ID_TO_NAME: dict[int, str] = {
    value: name
    for name, value in vars(tflite_schema.BuiltinOperator).items()
    if not name.startswith("_") and isinstance(value, int)
}


def build_cnn_keras_model() -> tf.keras.Model:
    tf.keras.utils.set_random_seed(42)
    inputs = tf.keras.Input(shape=(8, 8, 3), batch_size=1, name="input")
    x = tf.keras.layers.Conv2D(
        filters=4,
        kernel_size=(3, 3),
        strides=(1, 1),
        padding="same",
        activation="relu",
        name="conv2d",
    )(inputs)
    x = tf.keras.layers.DepthwiseConv2D(
        kernel_size=(3, 3),
        strides=(1, 1),
        padding="same",
        activation="relu",
        name="depthwise_conv2d",
    )(x)
    x = tf.keras.layers.AveragePooling2D(
        pool_size=(8, 8),
        strides=(8, 8),
        padding="valid",
        name="avg_pool",
    )(x)
    x = tf.keras.layers.Reshape((4,), name="flatten")(x)
    x = tf.keras.layers.Dense(3, activation=None, name="fc")(x)
    outputs = tf.keras.layers.Softmax(name="softmax")(x)
    return tf.keras.Model(inputs=inputs, outputs=outputs, name="tiny_cnn")


def generate_tiny_cnn_float() -> bytes:
    model = build_cnn_keras_model()
    converter = tf.lite.TFLiteConverter.from_keras_model(model)
    tflite_bytes = converter.convert()
    verify_with_interpreter(tflite_bytes)
    return tflite_bytes


def generate_tiny_cnn_int8() -> bytes:
    model = build_cnn_keras_model()
    rng = np.random.default_rng(seed=42)

    def representative_dataset():
        for _ in range(32):
            sample = rng.uniform(-1.0, 1.0, size=(1, 8, 8, 3)).astype(np.float32)
            yield [sample]

    converter = tf.lite.TFLiteConverter.from_keras_model(model)
    converter.optimizations = [tf.lite.Optimize.DEFAULT]
    converter.representative_dataset = representative_dataset
    converter.target_spec.supported_ops = [tf.lite.OpsSet.TFLITE_BUILTINS_INT8]
    converter.inference_input_type = tf.int8
    converter.inference_output_type = tf.int8
    tflite_bytes = converter.convert()
    verify_with_interpreter(tflite_bytes)
    return tflite_bytes


class WhileLoopModule(tf.Module):
    @tf.function(
        input_signature=[
            tf.TensorSpec(shape=[1, 4], dtype=tf.float32, name="x"),
            tf.TensorSpec(shape=[], dtype=tf.int32, name="steps"),
        ]
    )
    def __call__(self, x: tf.Tensor, steps: tf.Tensor) -> tf.Tensor:
        i0 = tf.constant(0, dtype=tf.int32)

        def cond(i: tf.Tensor, val: tf.Tensor) -> tf.Tensor:
            return tf.less(i, steps)

        def body(i: tf.Tensor, val: tf.Tensor) -> tuple[tf.Tensor, tf.Tensor]:
            return (i + 1, val + val)

        _, out = tf.while_loop(cond, body, [i0, x])
        return tf.identity(out, name="y")


def generate_tiny_while() -> bytes:
    module = WhileLoopModule()
    concrete_fn = module.__call__.get_concrete_function()
    converter = tf.lite.TFLiteConverter.from_concrete_functions([concrete_fn], module)
    tflite_bytes = converter.convert()
    verify_with_interpreter(tflite_bytes)
    return tflite_bytes


def verify_with_interpreter(tflite_bytes: bytes) -> None:
    interpreter = tf.lite.Interpreter(model_content=tflite_bytes)
    interpreter.allocate_tensors()


def resolve_opcode_name(op_code: tflite_schema.OperatorCodeT) -> str:
    builtin_code = op_code.builtinCode if op_code.builtinCode != 0 else op_code.deprecatedBuiltinCode
    if builtin_code < 0:
        builtin_code = builtin_code & 0xFF
    if builtin_code == tflite_schema.BuiltinOperator.CUSTOM and op_code.customCode:
        custom_str = op_code.customCode.decode("utf-8") if isinstance(op_code.customCode, bytes) else str(op_code.customCode)
        return f"CUSTOM({custom_str})"
    return BUILTIN_OP_ID_TO_NAME.get(builtin_code, f"OPCODE_{builtin_code}")


def format_tensor_shape(tensor: tflite_schema.TensorT) -> list[str] | None:
    shape = list(tensor.shape) if tensor.shape is not None else []
    sig = list(tensor.shapeSignature) if tensor.shapeSignature is not None else []
    if not shape and not sig:
        return [] if getattr(tensor, "hasRank", False) else None
    rank = len(sig) if len(sig) == len(shape) and len(sig) > 0 else len(shape)
    if rank == 0:
        return []
    dims: list[str] = []
    for idx in range(rank):
        s_val = sig[idx] if idx < len(sig) else shape[idx]
        d_val = shape[idx] if idx < len(shape) else s_val
        if s_val < 0 or d_val < 0:
            dims.append("?")
        else:
            dims.append(str(d_val))
    return dims


def extract_quantization_dict(tensor: tflite_schema.TensorT) -> dict[str, Any] | None:
    q = tensor.quantization
    if q is None or q.scale is None or len(q.scale) == 0:
        return None
    scales = [float(np.float32(s)) for s in q.scale]
    zero_points = [int(z) for z in (q.zeroPoint if q.zeroPoint is not None else [])]
    result: dict[str, Any] = {
        "scale": scales,
        "zeroPoint": zero_points,
    }
    if len(scales) > 1 or q.quantizedDimension is not None:
        result["quantizedDimension"] = int(q.quantizedDimension or 0)
    return result


def extract_truth(tflite_bytes: bytes, model_id: str) -> dict[str, Any]:
    fb_model = tflite_schema.Model.GetRootAs(tflite_bytes, 0)
    model = tflite_schema.ModelT.InitFromObj(fb_model)

    subgraphs = model.subgraphs or []
    primary = subgraphs[0]

    op_codes = model.operatorCodes or []
    buffers = model.buffers or []

    histogram: dict[str, int] = {}
    for op in primary.operators or []:
        op_name = resolve_opcode_name(op_codes[op.opcodeIndex])
        histogram[op_name] = histogram.get(op_name, 0) + 1

    tensors = primary.tensors or []

    def build_port_entry(tensor_idx: int) -> dict[str, Any]:
        t = tensors[tensor_idx]
        name = t.name.decode("utf-8") if isinstance(t.name, bytes) else (t.name or f"tensor_{tensor_idx}")
        entry: dict[str, Any] = {
            "name": name,
            "dataType": TFLITE_DTYPE_TO_IR.get(int(t.type), "Unknown"),
            "shape": format_tensor_shape(t),
        }
        q_dict = extract_quantization_dict(t)
        if q_dict is not None:
            entry["quantization"] = q_dict
        return entry

    input_indices = list(primary.inputs) if primary.inputs is not None else []
    output_indices = list(primary.outputs) if primary.outputs is not None else []
    inputs = [build_port_entry(int(i)) for i in input_indices if i >= 0]
    outputs = [build_port_entry(int(i)) for i in output_indices if i >= 0]

    initializers = []
    for idx, t in enumerate(tensors):
        buf = buffers[t.buffer] if t.buffer < len(buffers) else None
        buf_len = len(buf.data) if (buf is not None and buf.data is not None) else 0
        if buf_len > 0:
            name = t.name.decode("utf-8") if isinstance(t.name, bytes) else (t.name or f"tensor_{idx}")
            init_entry: dict[str, Any] = {
                "name": name,
                "dataType": TFLITE_DTYPE_TO_IR.get(int(t.type), "Unknown"),
                "shape": format_tensor_shape(t),
                "byteLength": buf_len,
            }
            q_dict = extract_quantization_dict(t)
            if q_dict is not None:
                init_entry["quantization"] = q_dict
            initializers.append(init_entry)

    sg_name = primary.name.decode("utf-8") if isinstance(primary.name, bytes) else (primary.name or "main")
    if not sg_name.strip():
        sg_name = "main"

    return {
        "modelId": model_id,
        "format": f"TensorFlow Lite v{model.version}",
        "subgraphCount": len(subgraphs),
        "primarySubgraph": {
            "name": sg_name,
            "nodeCount": len(primary.operators or []),
            "operatorHistogram": histogram,
            "inputs": inputs,
            "outputs": outputs,
            "initializers": initializers,
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate deterministic TFLite fixtures and ground-truth JSON files.")
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path("tests/DotNodeViewer.Core.Tests/Fixtures/tflite"),
        help="Output directory for generated .tflite and .truth.json files.",
    )
    args = parser.parse_args()
    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    specs = [
        ("tiny_cnn_float", "tflite-tiny_cnn_float", generate_tiny_cnn_float()),
        ("tiny_cnn_int8", "tflite-tiny_cnn_int8", generate_tiny_cnn_int8()),
        ("tiny_while", "tflite-tiny_while", generate_tiny_while()),
    ]

    for stem, model_id, tflite_bytes in specs:
        tflite_path = out_dir / f"{stem}.tflite"
        truth_path = out_dir / f"{stem}.truth.json"
        tflite_path.write_bytes(tflite_bytes)
        truth = extract_truth(tflite_bytes, model_id)
        truth_path.write_text(json.dumps(truth, indent=2) + "\n", encoding="utf-8")
        print(f"Generated {tflite_path} ({len(tflite_bytes)} bytes) and {truth_path}")


if __name__ == "__main__":
    main()
