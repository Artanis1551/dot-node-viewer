#!/usr/bin/env python3
"""
Generates deterministic PyTorch fixtures for dot-node-viewer (.pt / .pth):
1. tiny_cnn_traced.pt (torch.jit.trace)
2. tiny_cnn_scripted_if.pt (torch.jit.script with conditional control flow)
3. tiny_cnn_state_dict.pt (torch.save(model.state_dict()))
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
        self.conv1 = nn.Conv2d(1, 2, kernel_size=3, padding=1)
        self.bn1 = nn.BatchNorm2d(2)
        self.relu = nn.ReLU()
        self.fc = nn.Linear(2 * 4 * 4, 3)

    def forward(self, x):
        x = self.conv1(x)
        x = self.bn1(x)
        x = self.relu(x)
        x = x.flatten(1)
        x = self.fc(x)
        return x

class ScriptedWithIf(nn.Module):
    def __init__(self):
        super().__init__()
        self.conv1 = nn.Conv2d(1, 2, kernel_size=3, padding=1)
        self.relu = nn.ReLU()
        self.fc = nn.Linear(2 * 4 * 4, 3)

    def forward(self, x):
        h = self.conv1(x)
        if bool(torch.gt(torch.sum(h), 0)):
            h = self.relu(h)
        h = torch.flatten(h, 1)
        return self.fc(h)

def generate_traced(output_dir):
    model = TinyCNN().eval()
    example_input = torch.randn(1, 1, 4, 4)
    traced = torch.jit.trace(model, example_input)

    pt_path = os.path.join(output_dir, "tiny_cnn_traced.pt")
    traced.save(pt_path)

    truth = {
        "modelId": "pytorch-tiny_cnn_traced",
        "format": "PyTorch",
        "modelType": "TorchScript",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 5,
            "operatorHistogram": {
                "Conv2d": 1,
                "BatchNorm2d": 1,
                "ReLU": 1,
                "torch.flatten": 1,
                "Linear": 1
            },
            "inputs": [
                {
                    "name": "x",
                    "dataType": "Float32"
                }
            ],
            "outputs": [
                {
                    "name": "output",
                    "dataType": "Float32"
                }
            ],
            "initializers": [
                {
                    "name": "conv1.weight",
                    "dataType": "Float32",
                    "shape": [2, 1, 3, 3],
                    "byteLength": 2 * 1 * 3 * 3 * 4
                },
                {
                    "name": "conv1.bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "bn1.weight",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "bn1.bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "bn1.running_mean",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "bn1.running_var",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "fc.weight",
                    "dataType": "Float32",
                    "shape": [3, 32],
                    "byteLength": 3 * 32 * 4
                },
                {
                    "name": "fc.bias",
                    "dataType": "Float32",
                    "shape": [3],
                    "byteLength": 3 * 4
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_cnn_traced.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {pt_path} ({os.path.getsize(pt_path)} bytes)")
    print(f"Generated {truth_path}")

def generate_scripted_if(output_dir):
    model = ScriptedWithIf().eval()
    scripted = torch.jit.script(model)

    pt_path = os.path.join(output_dir, "tiny_cnn_scripted_if.pt")
    scripted.save(pt_path)

    truth = {
        "modelId": "pytorch-tiny_cnn_scripted_if",
        "format": "PyTorch",
        "modelType": "TorchScript",
        "subgraphCount": 1,
        "hasControlFlow": True,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 3,
            "operatorHistogram": {
                "Conv2d": 1,
                "ReLU": 1,
                "Linear": 1
            },
            "initializers": [
                {
                    "name": "conv1.weight",
                    "dataType": "Float32",
                    "shape": [2, 1, 3, 3],
                    "byteLength": 72
                },
                {
                    "name": "conv1.bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 8
                },
                {
                    "name": "fc.weight",
                    "dataType": "Float32",
                    "shape": [3, 32],
                    "byteLength": 384
                },
                {
                    "name": "fc.bias",
                    "dataType": "Float32",
                    "shape": [3],
                    "byteLength": 12
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_cnn_scripted_if.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {pt_path} ({os.path.getsize(pt_path)} bytes)")
    print(f"Generated {truth_path}")

def generate_state_dict(output_dir):
    model = TinyCNN().eval()
    pt_path = os.path.join(output_dir, "tiny_cnn_state_dict.pt")
    torch.save(model.state_dict(), pt_path)

    truth = {
        "modelId": "pytorch-tiny_cnn_state_dict",
        "format": "PyTorch",
        "modelType": "state_dict",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 3,
            "operatorHistogram": {
                "Conv2d": 1,
                "BatchNorm2d": 1,
                "Linear": 1
            },
            "initializers": [
                {
                    "name": "conv1.weight",
                    "dataType": "Float32",
                    "shape": [2, 1, 3, 3],
                    "byteLength": 72
                },
                {
                    "name": "conv1.bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 8
                },
                {
                    "name": "bn1.weight",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 8
                },
                {
                    "name": "bn1.bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 8
                },
                {
                    "name": "bn1.running_mean",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 8
                },
                {
                    "name": "bn1.running_var",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 8
                },
                {
                    "name": "fc.weight",
                    "dataType": "Float32",
                    "shape": [3, 32],
                    "byteLength": 384
                },
                {
                    "name": "fc.bias",
                    "dataType": "Float32",
                    "shape": [3],
                    "byteLength": 12
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_cnn_state_dict.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {pt_path} ({os.path.getsize(pt_path)} bytes)")
    print(f"Generated {truth_path}")

def main():
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "../../../"))
    fixture_dir = os.path.join(repo_root, "tests/DotNodeViewer.Core.Tests/Fixtures/pytorch")
    os.makedirs(fixture_dir, exist_ok=True)

    print("Generating PyTorch fixtures...")
    generate_traced(fixture_dir)
    generate_scripted_if(fixture_dir)
    generate_state_dict(fixture_dir)
    print("Done!")

if __name__ == "__main__":
    main()
