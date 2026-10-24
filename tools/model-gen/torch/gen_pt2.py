#!/usr/bin/env python3
"""
Generates deterministic PyTorch 2 ExportedProgram fixtures for dot-node-viewer (.pt2):
1. tiny_cnn_export.pt2 (torch.export.export + torch.export.save of a CNN)
2. tiny_dynamic_batch_export.pt2 (torch.export.export with dynamic batch dimension)
"""

import json
import os
import sys
import torch
import torch.nn as nn

torch.manual_seed(42)

class TinyCNN(nn.Module):
    def __init__(self):
        super().__init__()
        self.conv = nn.Conv2d(1, 2, kernel_size=3, padding=1)
        self.relu = nn.ReLU()
        self.fc = nn.Linear(2 * 4 * 4, 3)

    def forward(self, x):
        x = self.conv(x)
        x = self.relu(x)
        x = x.flatten(1)
        x = self.fc(x)
        return x

class TinyDynamic(nn.Module):
    def __init__(self):
        super().__init__()
        self.fc = nn.Linear(4, 2)
        self.relu = nn.ReLU()

    def forward(self, x):
        return self.relu(self.fc(x))

def generate_tiny_cnn_export(output_dir):
    model = TinyCNN().eval()
    x = torch.randn(1, 1, 4, 4)
    exported = torch.export.export(model, (x,))

    pt2_path = os.path.join(output_dir, "tiny_cnn_export.pt2")
    torch.export.save(exported, pt2_path)

    truth = {
        "modelId": "pytorch_pt2-tiny_cnn_export",
        "format": "PyTorchPt2",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 4,
            "operatorHistogram": {
                "torch.ops.aten.conv2d.default": 1,
                "torch.ops.aten.relu.default": 1,
                "torch.ops.aten.flatten.using_ints": 1,
                "torch.ops.aten.linear.default": 1
            },
            "inputs": [
                {
                    "name": "x",
                    "dataType": "Float32",
                    "shape": [1, 1, 4, 4]
                }
            ],
            "outputs": [
                {
                    "name": "linear",
                    "dataType": "Float32",
                    "shape": [1, 3]
                }
            ],
            "initializers": [
                {
                    "name": "p_conv_weight",
                    "dataType": "Float32",
                    "shape": [2, 1, 3, 3],
                    "byteLength": 2 * 1 * 3 * 3 * 4
                },
                {
                    "name": "p_conv_bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "p_fc_weight",
                    "dataType": "Float32",
                    "shape": [3, 32],
                    "byteLength": 3 * 32 * 4
                },
                {
                    "name": "p_fc_bias",
                    "dataType": "Float32",
                    "shape": [3],
                    "byteLength": 3 * 4
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_cnn_export.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {pt2_path} ({os.path.getsize(pt2_path)} bytes)")
    print(f"Generated {truth_path}")

def generate_dynamic_batch_export(output_dir):
    model = TinyDynamic().eval()
    x = torch.randn(2, 4)
    batch_dim = torch.export.Dim("batch", min=1, max=32)
    exported = torch.export.export(model, (x,), dynamic_shapes={"x": {0: batch_dim}})

    pt2_path = os.path.join(output_dir, "tiny_dynamic_batch_export.pt2")
    torch.export.save(exported, pt2_path)

    truth = {
        "modelId": "pytorch_pt2-tiny_dynamic_batch_export",
        "format": "PyTorchPt2",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 2,
            "operatorHistogram": {
                "torch.ops.aten.linear.default": 1,
                "torch.ops.aten.relu.default": 1
            },
            "inputs": [
                {
                    "name": "x",
                    "dataType": "Float32"
                }
            ],
            "outputs": [
                {
                    "name": "relu",
                    "dataType": "Float32"
                }
            ],
            "initializers": [
                {
                    "name": "p_fc_weight",
                    "dataType": "Float32",
                    "shape": [2, 4],
                    "byteLength": 2 * 4 * 4
                },
                {
                    "name": "p_fc_bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                }
            ]
        },
        "rangeConstraints": {
            "minVal": 1,
            "maxVal": 32
        }
    }

    truth_path = os.path.join(output_dir, "tiny_dynamic_batch_export.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {pt2_path} ({os.path.getsize(pt2_path)} bytes)")
    print(f"Generated {truth_path}")

def main():
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    fixtures_dir = os.path.join(repo_root, "tests", "DotNodeViewer.Core.Tests", "Fixtures", "pytorch_pt2")
    os.makedirs(fixtures_dir, exist_ok=True)

    generate_tiny_cnn_export(fixtures_dir)
    generate_dynamic_batch_export(fixtures_dir)

if __name__ == "__main__":
    main()

