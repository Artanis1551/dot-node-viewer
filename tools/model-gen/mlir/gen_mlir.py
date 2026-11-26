#!/usr/bin/env python3
"""
Deterministic MLIR fixture generator for DotNodeViewer (Wish W13).

Emits committed textual (.mlir) and binary bytecode (.mlirbc) fixtures alongside
independent *.truth.json ground-truth topology files into:
  tests/DotNodeViewer.Core.Tests/Fixtures/mlir/
"""

from __future__ import annotations

import json
import math
import struct
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
FIXTURES_DIR = REPO_ROOT / "tests" / "DotNodeViewer.Core.Tests" / "Fixtures" / "mlir"


def encode_prefix_varint(val: int) -> bytes:
    """Encodes an unsigned 64-bit integer using LLVM MLIR PrefixVarInt."""
    if val < 0:
        raise ValueError(f"Negative unsigned varint: {val}")
    for n in range(9):
        if n == 8:
            return b"\x00" + struct.pack("<Q", val)
        max_val = 1 << (7 * (n + 1))
        if val < max_val:
            encoded = (val << (n + 1)) | (1 << n)
            return encoded.to_bytes(n + 1, "little")
    raise ValueError(f"Value exceeds uint64: {val}")


def encode_signed_prefix_varint(val: int) -> bytes:
    """Encodes a signed 64-bit integer using zigzag + PrefixVarInt."""
    u = ((val << 1) ^ (-1 if val < 0 else 0)) & 0xFFFFFFFFFFFFFFFF
    return encode_prefix_varint(u)


def make_section(section_id: int, payload: bytes, alignment: int | None = None) -> bytes:
    """Builds a top-level MLIR bytecode section."""
    if alignment is None:
        return bytes([section_id & 0x7F]) + encode_prefix_varint(len(payload)) + payload
    header = bytes([(section_id & 0x7F) | 0x80]) + encode_prefix_varint(len(payload)) + encode_prefix_varint(alignment)
    return header + payload


def build_stablehlo_bytecode_bytes() -> tuple[bytes, list[float]]:
    """
    Constructs a valid MLIR Bytecode v6 binary with:
      - kString (0), kDialect (1), kAttrType (2), kAttrTypeOffset (3),
        kIR (4), kResource (5), kResourceOffset (6)
      - Single-'?' dynamic dimension tensor<?x4xf32> backed by 32-byte kResource blob
      - Inline DenseIntOrFPElementsAttr bias tensor<4xf32>
    """
    weights = [1.0, 2.0, -3.5, 4.25, 0.5, -1.5, 2.5, 3.0]
    weight_bytes = struct.pack("<8f", *weights)
    bias = [0.1, 0.2, 0.3, 0.4]
    bias_bytes = struct.pack("<4f", *bias)

    # 1. String table
    string_list = [
        "builtin",           # 0
        "func",              # 1
        "stablehlo",         # 2
        "module",            # 3
        "func",              # 4
        "return",            # 5
        "constant",          # 6
        "dot_general",       # 7
        "add",               # 8
        "sym_name",          # 9
        "main",              # 10
        "value",             # 11
        "fc_weights",        # 12
    ]

    str_blobs = [s.encode("utf-8") + b"\x00" for s in string_list]
    str_sec_payload = bytearray()
    str_sec_payload += encode_prefix_varint(len(string_list))
    for blob in reversed(str_blobs):
        str_sec_payload += encode_prefix_varint(len(blob))
    for blob in str_blobs:
        str_sec_payload += blob

    # 2. Dialect table (version >= 5: dialect raw = (strIdx << 1) | 0, op raw = (strIdx << 1) | 0)
    dialect_sec = bytearray()
    dialect_sec += encode_prefix_varint(3)  # 3 dialects: builtin(0), func(1), stablehlo(2)
    dialect_sec += encode_prefix_varint(0 << 1)  # builtin
    dialect_sec += encode_prefix_varint(1 << 1)  # func
    dialect_sec += encode_prefix_varint(2 << 1)  # stablehlo

    # Op groups:
    # builtin: module (op 0)
    dialect_sec += encode_prefix_varint(0)
    dialect_sec += encode_prefix_varint(1)
    dialect_sec += encode_prefix_varint(3 << 1)
    # func: func (op 1), return (op 2)
    dialect_sec += encode_prefix_varint(1)
    dialect_sec += encode_prefix_varint(2)
    dialect_sec += encode_prefix_varint(4 << 1)
    dialect_sec += encode_prefix_varint(5 << 1)
    # stablehlo: constant (op 3), dot_general (op 4), add (op 5)
    dialect_sec += encode_prefix_varint(2)
    dialect_sec += encode_prefix_varint(3)
    dialect_sec += encode_prefix_varint(6 << 1)
    dialect_sec += encode_prefix_varint(7 << 1)
    dialect_sec += encode_prefix_varint(8 << 1)

    # 3. Resource section (kResource = 5, kResourceOffset = 6)
    # Resource 0: group=builtin (dialect 0 -> (0 << 1) | 1 = 1), key="fc_weights" (str 12), kind=3 (Blob)
    res_align = 4
    res_entry_inner = encode_prefix_varint(res_align) + b"\xCB\xCB\xCB" + weight_bytes  # 1 + 3 + 32 = 36 bytes
    # Wait: let's compute alignment relative to file offset after writing preceding sections!
    # Actually MlirBytecodeReader pads relative to stream offset modulo alignment.
    # Using alignment=1 requires 0 padding regardless of stream offset, and is valid power of 2!
    res_entry_inner = encode_prefix_varint(1) + weight_bytes

    res_offset_sec = bytearray()
    res_offset_sec += encode_prefix_varint(1)           # 1 group
    res_offset_sec += encode_prefix_varint((0 << 1) | 1)  # dialect 0 (builtin)
    res_offset_sec += encode_prefix_varint(1)           # 1 entry
    res_offset_sec += encode_prefix_varint(12)          # key = "fc_weights"
    res_offset_sec += encode_prefix_varint(len(res_entry_inner))
    res_offset_sec += bytes([3])                        # Kind = 3 (Blob)

    res_sec = bytes(res_entry_inner)

    # 4. AttrType & AttrTypeOffset sections
    # Types (all builtin, isCustom=1):
    #   0: f32 (code 5)
    #   1: tensor<2x4xf32> (code 13, rank=2, dims=[2, 4], elem=0)
    #   2: tensor<?x4xf32> (code 13, rank=2, dims=[-1, 4], elem=0) -> inferred to [2, 4]!
    #   3: tensor<4xf32> (code 13, rank=1, dims=[4], elem=0)
    types_raw = [
        encode_prefix_varint(5),
        encode_prefix_varint(13) + encode_prefix_varint(2) + encode_signed_prefix_varint(2) + encode_signed_prefix_varint(4) + encode_prefix_varint(0),
        encode_prefix_varint(13) + encode_prefix_varint(2) + encode_signed_prefix_varint(-1) + encode_signed_prefix_varint(4) + encode_prefix_varint(0),
        encode_prefix_varint(13) + encode_prefix_varint(1) + encode_signed_prefix_varint(4) + encode_prefix_varint(0),
    ]

    # Attrs (all builtin, isCustom=1):
    #   0: UnknownLoc (code 21)
    #   1: StringAttr("main") (code 2, str=10)
    #   2: DictionaryAttr({sym_name: "main"}) (code 1, count=1, key=9, val=1)
    #   3: DenseResourceElementsAttr(type=2, handle=0) (code 16, type=2, handle=0)
    #   4: DictionaryAttr({value: attr 3}) (code 1, count=1, key=11, val=3)
    #   5: DenseIntOrFPElementsAttr(type=3, blob=bias_bytes) (code 18, type=3, len=16, bias_bytes)
    #   6: DictionaryAttr({value: attr 5}) (code 1, count=1, key=11, val=5)
    attrs_raw = [
        encode_prefix_varint(21),
        encode_prefix_varint(2) + encode_prefix_varint(10),
        encode_prefix_varint(1) + encode_prefix_varint(1) + encode_prefix_varint(9) + encode_prefix_varint(1),
        encode_prefix_varint(16) + encode_prefix_varint(2) + encode_prefix_varint(0),
        encode_prefix_varint(1) + encode_prefix_varint(1) + encode_prefix_varint(11) + encode_prefix_varint(3),
        encode_prefix_varint(18) + encode_prefix_varint(3) + encode_prefix_varint(len(bias_bytes)) + bias_bytes,
        encode_prefix_varint(1) + encode_prefix_varint(1) + encode_prefix_varint(11) + encode_prefix_varint(5),
    ]

    attr_type_sec = bytearray()
    attr_type_off_sec = bytearray()
    attr_type_off_sec += encode_prefix_varint(len(attrs_raw))
    attr_type_off_sec += encode_prefix_varint(len(types_raw))

    # Group 1: attrs under builtin (dialect 0)
    attr_type_off_sec += encode_prefix_varint(0)
    attr_type_off_sec += encode_prefix_varint(len(attrs_raw))
    for blob in attrs_raw:
        attr_type_off_sec += encode_prefix_varint((len(blob) << 1) | 1)
        attr_type_sec += blob

    # Group 2: types under builtin (dialect 0)
    attr_type_off_sec += encode_prefix_varint(0)
    attr_type_off_sec += encode_prefix_varint(len(types_raw))
    for blob in types_raw:
        attr_type_off_sec += encode_prefix_varint((len(blob) << 1) | 1)
        attr_type_sec += blob

    # 5. IR Section (kIR = 4)
    # Build func.func @main body region first:
    # Block 0: 1 arg (%arg0: type 1 = tensor<2x4xf32>), 5 ops:
    #   %1 = stablehlo.constant {value = dense_resource<fc_weights>} : tensor<?x4xf32> (op 3, attr 4, res type 2)
    #   %2 = stablehlo.dot_general(%arg0, %1) : tensor<2x4xf32> (op 4, operands=[0, 1], res type 1)
    #   %3 = stablehlo.constant {value = dense<bias>} : tensor<4xf32> (op 3, attr 6, res type 3)
    #   %4 = stablehlo.add(%2, %3) : tensor<2x4xf32> (op 5, operands=[2, 3], res type 1)
    #   func.return(%4) (op 2, operands=[4])
    func_body = bytearray()
    func_body += encode_prefix_varint(1)  # 1 block
    func_body += encode_prefix_varint(5)  # 5 SSA values (%arg0 + 4 op results)
    func_body += encode_prefix_varint((5 << 1) | 1)  # 5 ops, hasArgs=1
    func_body += encode_prefix_varint(1)  # 1 block arg
    func_body += encode_prefix_varint(1)  # arg0 type = tensor<2x4xf32>
    func_body += encode_prefix_varint(0)  # arg0 loc = UnknownLoc

    # Op 0: stablehlo.constant (w0)
    func_body += encode_prefix_varint(3)
    func_body += bytes([0x01 | 0x02])  # HasAttrs | HasResults
    func_body += encode_prefix_varint(0)  # loc
    func_body += encode_prefix_varint(4)  # attr dict 4
    func_body += encode_prefix_varint(1)  # 1 result
    func_body += encode_prefix_varint(2)  # result type = tensor<?x4xf32>

    # Op 1: stablehlo.dot_general(%arg0, %1)
    func_body += encode_prefix_varint(4)
    func_body += bytes([0x02 | 0x04])  # HasResults | HasOperands
    func_body += encode_prefix_varint(0)
    func_body += encode_prefix_varint(1)  # 1 result
    func_body += encode_prefix_varint(1)  # tensor<2x4xf32>
    func_body += encode_prefix_varint(2)  # 2 operands
    func_body += encode_prefix_varint(0)  # %arg0
    func_body += encode_prefix_varint(1)  # %1

    # Op 2: stablehlo.constant (bias)
    func_body += encode_prefix_varint(3)
    func_body += bytes([0x01 | 0x02])  # HasAttrs | HasResults
    func_body += encode_prefix_varint(0)
    func_body += encode_prefix_varint(6)  # attr dict 6
    func_body += encode_prefix_varint(1)
    func_body += encode_prefix_varint(3)  # tensor<4xf32>

    # Op 3: stablehlo.add(%2, %3)
    func_body += encode_prefix_varint(5)
    func_body += bytes([0x02 | 0x04])  # HasResults | HasOperands
    func_body += encode_prefix_varint(0)
    func_body += encode_prefix_varint(1)
    func_body += encode_prefix_varint(1)  # tensor<2x4xf32>
    func_body += encode_prefix_varint(2)
    func_body += encode_prefix_varint(2)
    func_body += encode_prefix_varint(3)

    # Op 4: func.return(%4)
    func_body += encode_prefix_varint(2)
    func_body += bytes([0x04])  # HasOperands
    func_body += encode_prefix_varint(0)
    func_body += encode_prefix_varint(1)
    func_body += encode_prefix_varint(4)

    # func.func operation wrapping func_body (non-isolated inline region flag=0)
    module_body = bytearray()
    module_body += encode_prefix_varint(1)  # 1 block
    module_body += encode_prefix_varint(0)  # 0 values
    module_body += encode_prefix_varint((1 << 1) | 0)  # 1 op, hasArgs=0
    module_body += encode_prefix_varint(1)  # op 1 = func.func
    module_body += bytes([0x01 | 0x10])     # HasAttrs | HasInlineRegions
    module_body += encode_prefix_varint(0)  # loc
    module_body += encode_prefix_varint(2)  # attr dict 2 ({sym_name = "main"})
    module_body += encode_prefix_varint(1)  # 1 region
    module_body += bytes([0x00])            # inline region (not isolated nested section)
    module_body += func_body

    # builtin.module wrapping module_body
    top_ir = bytearray()
    top_ir += encode_prefix_varint(1)  # 1 block
    top_ir += encode_prefix_varint(0)  # 0 values
    top_ir += encode_prefix_varint((1 << 1) | 0)  # 1 op, hasArgs=0
    top_ir += encode_prefix_varint(0)  # op 0 = builtin.module
    top_ir += bytes([0x10])            # HasInlineRegions
    top_ir += encode_prefix_varint(0)  # loc
    top_ir += encode_prefix_varint(1)  # 1 region
    top_ir += bytes([0x00])            # inline region
    top_ir += module_body

    out = bytearray()
    out += b"ML\xEFR"
    out += encode_prefix_varint(6)  # bytecode version 6
    out += b"dot-node-viewer-gen\x00"
    out += make_section(0, bytes(str_sec_payload))
    out += make_section(1, bytes(dialect_sec))
    out += make_section(2, bytes(attr_type_sec))
    out += make_section(3, bytes(attr_type_off_sec))
    out += make_section(5, res_sec)
    out += make_section(6, bytes(res_offset_sec))
    out += make_section(4, bytes(top_ir))
    return bytes(out), weights


def write_fixture_and_truth(stem: str, filename: str, content: bytes, truth: dict) -> None:
    FIXTURES_DIR.mkdir(parents=True, exist_ok=True)
    model_path = FIXTURES_DIR / filename
    truth_path = FIXTURES_DIR / f"{stem}.truth.json"
    model_path.write_bytes(content)
    truth_path.write_text(json.dumps(truth, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    # 1. tiny_stablehlo.mlir
    weights_hex = struct.pack("<8f", 1.0, 2.0, -3.5, 4.25, 0.5, -1.5, 2.5, 3.0).hex()
    # MLIR AsmPrinter 4-byte LE alignment header (4 = 04000000) + 32-byte payload
    stablehlo_text = f"""module @tiny_stablehlo {{
  func.func @main(%x: tensor<?x4xf32>, %cond: tensor<i1>) -> (tensor<?x4xf32>) {{
    %w = stablehlo.constant dense_resource<fc_weights> : tensor<?x4xf32>
    %dot = stablehlo.dot_general %x, %w, contracting_dims = [1] x [0] : (tensor<?x4xf32>, tensor<2x4xf32>) -> tensor<?x4xf32>
    %bias = stablehlo.constant dense<[0.1, 0.2, 0.3, 0.4]> : tensor<4xf32>
    %add = stablehlo.add %dot, %bias : tensor<?x4xf32>
    %zero = stablehlo.constant dense<0.0> : tensor<?x4xf32>
    %relu = stablehlo.maximum %add, %zero : tensor<?x4xf32>
    %sel = scf.if %cond -> (tensor<?x4xf32>) {{
      %neg = stablehlo.negate %relu : tensor<?x4xf32>
      scf.yield %neg : tensor<?x4xf32>
    }} else {{
      scf.yield %relu : tensor<?x4xf32>
    }}
    func.return %sel : tensor<?x4xf32>
  }}
}}

{{-#
  dialect_resources: {{
    builtin: {{
      fc_weights: "0x04000000{weights_hex}"
    }}
  }}
#-}}
"""
    stablehlo_truth = {
        "modelId": "mlir-tiny_stablehlo",
        "format": "MLIR Textual",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 7,
            "operatorHistogram": {
                "stablehlo.constant": 3,
                "stablehlo.dot_general": 1,
                "stablehlo.add": 1,
                "stablehlo.maximum": 1,
                "scf.if": 1,
            },
            "inputs": [
                {"name": "%x", "dataType": "Float32", "shape": ["?", "4"]},
                {"name": "%cond", "dataType": "Bool", "shape": []},
            ],
            "outputs": [
                {"name": "%sel", "dataType": "Float32", "shape": ["?", "4"]},
            ],
        },
    }
    write_fixture_and_truth("tiny_stablehlo", "tiny_stablehlo.mlir", stablehlo_text.encode("utf-8"), stablehlo_truth)

    # 2. tiny_tosa.mlir
    tosa_text = """module @tiny_tosa {
  func.func @main(%arg0: tensor<1x8x8x3x!quant.uniform<i8:f32, 0.05:128>>) -> (tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>) {
    %w = "tosa.const"() <{values = dense<1> : tensor<4x3x3x3xi8>}> : () -> tensor<4x3x3x3xi8>
    %b = "tosa.const"() <{values = dense<0> : tensor<4xi32>}> : () -> tensor<4xi32>
    %conv = tosa.conv2d %arg0, %w, %b {acc_type = i32, dilation = array<i64: 1, 1>, pad = array<i64: 1, 1, 1, 1>, stride = array<i64: 2, 2>} : (tensor<1x8x8x3x!quant.uniform<i8:f32, 0.05:128>>, tensor<4x3x3x3xi8>, tensor<4xi32>) -> tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>
    %clamp = tosa.clamp %conv {max_fp = 6.0 : f32, max_int = 127 : i64, min_fp = 0.0 : f32, min_int = -128 : i64} : (tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>) -> tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>
    %rescale = tosa.rescale %clamp {double_round = false, input_zp = 0 : i32, multiplier = array<i32: 1073741824>, output_zp = 128 : i32, per_channel = false, scale32 = true, shift = array<i8: 30>} : (tensor<1x4x4x4x!quant.uniform<i32:f32, 0.01:0>>) -> tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>
    %pool = tosa.avg_pool2d %rescale {acc_type = i32, kernel = array<i64: 1, 1>, pad = array<i64: 0, 0, 0, 0>, stride = array<i64: 1, 1>} : (tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>) -> tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>
    func.return %pool : tensor<1x4x4x4x!quant.uniform<i8:f32, 0.02:128>>
  }
}
"""
    tosa_truth = {
        "modelId": "mlir-tiny_tosa",
        "format": "MLIR Textual",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 6,
            "operatorHistogram": {
                "tosa.const": 2,
                "tosa.conv2d": 1,
                "tosa.clamp": 1,
                "tosa.rescale": 1,
                "tosa.avg_pool2d": 1,
            },
            "inputs": [
                {
                    "name": "%arg0",
                    "dataType": "Int8",
                    "shape": ["1", "8", "8", "3"],
                    "quantization": {"scale": [0.05], "zeroPoint": [128]},
                }
            ],
            "outputs": [
                {
                    "name": "%pool",
                    "dataType": "Int8",
                    "shape": ["1", "4", "4", "4"],
                    "quantization": {"scale": [0.02], "zeroPoint": [128]},
                }
            ],
        },
    }
    write_fixture_and_truth("tiny_tosa", "tiny_tosa.mlir", tosa_text.encode("utf-8"), tosa_truth)

    # 3. tiny_torch_quant.mlir
    torch_quant_text = """module @tiny_torch_quant {
  func.func @forward(%x: !torch.vtensor<[1,8],f32>) -> (!torch.vtensor<[1,4],f32>) {
    %w_q = torch.vtensor.literal(dense<1> : tensor<8x4xsi8>) : !torch.vtensor<[8,4],si8>
    %scale = torch.constant.float 0.05
    %zp = torch.constant.int 0
    %qmin = torch.constant.int -128
    %qmax = torch.constant.int 127
    %dtype = torch.constant.int 1
    %xq = torch.quantized_decomposed.quantize_per_tensor %x, %scale, %zp, %qmin, %qmax, %dtype : !torch.vtensor<[1,8],f32>, f64, i64, i64, i64, i64 -> !torch.vtensor<[1,8],si8>
    %xdq = torch.quantized_decomposed.dequantize_per_tensor %xq, %scale, %zp, %qmin, %qmax, %dtype : !torch.vtensor<[1,8],si8>, f64, i64, i64, i64, i64 -> !torch.vtensor<[1,8],f32>
    %w_fp = torch.torchao.dequantize_affine %w_q, %scale, %zp : !torch.vtensor<[8,4],si8>, f64, i64 -> !torch.vtensor<[8,4],f32>
    %mm = torch.aten.mm %xdq, %w_fp : !torch.vtensor<[1,8],f32>, !torch.vtensor<[8,4],f32> -> !torch.vtensor<[1,4],f32>
    %out = torch.aten.relu %mm : !torch.vtensor<[1,4],f32> -> !torch.vtensor<[1,4],f32>
    func.return %out : !torch.vtensor<[1,4],f32>
  }
}
"""
    torch_quant_truth = {
        "modelId": "mlir-tiny_torch_quant",
        "format": "MLIR Textual",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "forward",
            "nodeCount": 11,
            "operatorHistogram": {
                "torch.vtensor.literal": 1,
                "torch.constant.float": 1,
                "torch.constant.int": 4,
                "torch.quantized_decomposed.quantize_per_tensor": 1,
                "torch.quantized_decomposed.dequantize_per_tensor": 1,
                "torch.torchao.dequantize_affine": 1,
                "torch.aten.mm": 1,
                "torch.aten.relu": 1,
            },
            "inputs": [
                {"name": "%x", "dataType": "Float32", "shape": ["1", "8"]},
            ],
            "outputs": [
                {"name": "%out", "dataType": "Float32", "shape": ["1", "4"]},
            ],
        },
    }
    write_fixture_and_truth("tiny_torch_quant", "tiny_torch_quant.mlir", torch_quant_text.encode("utf-8"), torch_quant_truth)

    # 4. tiny_triton_kernel.mlir
    triton_text = """module @tiny_triton_kernel {
  tt.func public @fused_gemm_kernel(%a_ptr: !tt.ptr<f16>, %b_ptr: !tt.ptr<f16>, %c_ptr: !tt.ptr<f32>) {
    %pid = tt.get_program_id x : i32
    %offs = tt.make_range {end = 16 : i32, start = 0 : i32} : tensor<16xi32>
    %pa = tt.addptr %a_ptr, %offs : !tt.ptr<f16>, tensor<16xi32>
    %pb = tt.addptr %b_ptr, %offs : !tt.ptr<f16>, tensor<16xi32>
    %a = tt.load %pa : !tt.ptr<tensor<16x16xf16>>
    %b = tt.load %pb : !tt.ptr<tensor<16x16xf16>>
    %acc_init = arith.constant dense<0.0> : tensor<16x16xf32>
    %dot = tt.dot %a, %b, %acc_init : tensor<16x16xf16> * tensor<16x16xf16> -> tensor<16x16xf32>
    %bias = arith.constant dense<1.0> : tensor<16x16xf32>
    %res = arith.addf %dot, %bias : tensor<16x16xf32>
    tt.store %c_ptr, %res : !tt.ptr<tensor<16x16xf32>>
    tt.return
  }
}
"""
    triton_truth = {
        "modelId": "mlir-tiny_triton_kernel",
        "format": "MLIR Textual",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "fused_gemm_kernel",
            "nodeCount": 11,
            "operatorHistogram": {
                "tt.get_program_id": 1,
                "tt.make_range": 1,
                "tt.addptr": 2,
                "tt.load": 2,
                "arith.constant": 2,
                "tt.dot": 1,
                "arith.addf": 1,
                "tt.store": 1,
            },
            "inputs": [
                {"name": "%a_ptr", "dataType": "Float16", "shape": []},
                {"name": "%b_ptr", "dataType": "Float16", "shape": []},
                {"name": "%c_ptr", "dataType": "Float32", "shape": []},
            ],
            "outputs": [],
        },
    }
    write_fixture_and_truth("tiny_triton_kernel", "tiny_triton_kernel.mlir", triton_text.encode("utf-8"), triton_truth)

    # 5. tiny_stablehlo_bytecode.mlirbc
    bc_bytes, _ = build_stablehlo_bytecode_bytes()
    bc_truth = {
        "modelId": "mlir-tiny_stablehlo_bytecode",
        "format": "MLIR Bytecode v6",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 4,
            "operatorHistogram": {
                "stablehlo.constant": 2,
                "stablehlo.dot_general": 1,
                "stablehlo.add": 1,
            },
            "inputs": [
                {"name": "%arg0", "dataType": "Float32", "shape": ["2", "4"]},
            ],
            "outputs": [
                {"name": "%4", "dataType": "Float32", "shape": ["2", "4"]},
            ],
        },
    }
    write_fixture_and_truth("tiny_stablehlo_bytecode", "tiny_stablehlo_bytecode.mlirbc", bc_bytes, bc_truth)

    print(f"Generated 5 MLIR fixtures in {FIXTURES_DIR}")


if __name__ == "__main__":
    main()
