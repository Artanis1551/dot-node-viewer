#!/usr/bin/env python3
"""
Generates deterministic Keras fixtures for dot-node-viewer (.keras / .h5):
1. tiny_sequential.keras (Keras 3 .keras ZIP archive)
2. tiny_functional_residual.keras (Keras 3 .keras Functional with Add skip connection)
3. tiny_cnn_legacy.h5 (Legacy HDF5 with model_config attribute)
4. tiny_weights_only.weights.h5 (HDF5 weights-only file)
"""

import json
import os
import sys

os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"
os.environ["TF_CPP_MIN_LOG_LEVEL"] = "3"

import keras
from keras import layers
import numpy as np

np.random.seed(42)

def generate_tiny_sequential(output_dir):
    model = keras.Sequential([
        layers.Input(shape=(4,), name="input_1"),
        layers.Dense(8, activation="relu", name="dense_1"),
        layers.Dense(2, name="dense_2")
    ], name="tiny_sequential")

    keras_path = os.path.join(output_dir, "tiny_sequential.keras")
    model.save(keras_path)

    truth = {
        "modelId": "keras-tiny_sequential",
        "format": "Keras",
        "modelType": "Sequential",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 2,
            "operatorHistogram": {
                "Dense": 2
            },
            "inputs": [
                {
                    "name": "input_1",
                    "dataType": "Float32",
                    "shape": ["?", "4"]
                }
            ],
            "outputs": [
                {
                    "name": "dense_2",
                    "dataType": "Float32"
                }
            ],
            "initializers": [
                {
                    "name": "dense_1/kernel",
                    "dataType": "Float32",
                    "shape": [4, 8],
                    "byteLength": 4 * 8 * 4
                },
                {
                    "name": "dense_1/bias",
                    "dataType": "Float32",
                    "shape": [8],
                    "byteLength": 8 * 4
                },
                {
                    "name": "dense_2/kernel",
                    "dataType": "Float32",
                    "shape": [8, 2],
                    "byteLength": 8 * 2 * 4
                },
                {
                    "name": "dense_2/bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_sequential.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {keras_path} ({os.path.getsize(keras_path)} bytes)")
    print(f"Generated {truth_path}")

def generate_tiny_functional_residual(output_dir):
    inputs = layers.Input(shape=(8,), name="input_1")
    x = layers.Dense(8, activation="relu", name="dense_res1")(inputs)
    x = layers.Dense(8, activation="relu", name="dense_res2")(x)
    out = layers.Add(name="add_skip")([inputs, x])
    model = keras.Model(inputs=inputs, outputs=out, name="tiny_functional_residual")

    keras_path = os.path.join(output_dir, "tiny_functional_residual.keras")
    model.save(keras_path)

    truth = {
        "modelId": "keras-tiny_functional_residual",
        "format": "Keras",
        "modelType": "Functional",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 3,
            "operatorHistogram": {
                "Dense": 2,
                "Add": 1
            },
            "inputs": [
                {
                    "name": "input_1",
                    "dataType": "Float32",
                    "shape": ["?", "8"]
                }
            ],
            "outputs": [
                {
                    "name": "add_skip",
                    "dataType": "Float32"
                }
            ],
            "initializers": [
                {
                    "name": "dense_res1/kernel",
                    "dataType": "Float32",
                    "shape": [8, 8],
                    "byteLength": 8 * 8 * 4
                },
                {
                    "name": "dense_res1/bias",
                    "dataType": "Float32",
                    "shape": [8],
                    "byteLength": 8 * 4
                },
                {
                    "name": "dense_res2/kernel",
                    "dataType": "Float32",
                    "shape": [8, 8],
                    "byteLength": 8 * 8 * 4
                },
                {
                    "name": "dense_res2/bias",
                    "dataType": "Float32",
                    "shape": [8],
                    "byteLength": 8 * 4
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_functional_residual.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {keras_path} ({os.path.getsize(keras_path)} bytes)")
    print(f"Generated {truth_path}")

def generate_tiny_cnn_legacy(output_dir):
    model = keras.Sequential([
        layers.Input(shape=(8, 8, 1), name="input_1"),
        layers.Conv2D(2, kernel_size=3, padding="same", activation="relu", name="conv_1"),
        layers.Flatten(name="flatten_1"),
        layers.Dense(3, name="dense_1")
    ], name="tiny_cnn_legacy")

    h5_path = os.path.join(output_dir, "tiny_cnn_legacy.h5")
    model.save(h5_path)

    truth = {
        "modelId": "keras-tiny_cnn_legacy",
        "format": "Keras",
        "modelType": "Sequential",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 3,
            "operatorHistogram": {
                "Conv2D": 1,
                "Flatten": 1,
                "Dense": 1
            },
            "inputs": [
                {
                    "name": "input_1",
                    "dataType": "Float32",
                    "shape": ["?", "8", "8", "1"]
                }
            ],
            "outputs": [
                {
                    "name": "dense_1",
                    "dataType": "Float32"
                }
            ],
            "initializers": [
                {
                    "name": "conv_1/kernel",
                    "dataType": "Float32",
                    "shape": [3, 3, 1, 2],
                    "byteLength": 3 * 3 * 1 * 2 * 4
                },
                {
                    "name": "conv_1/bias",
                    "dataType": "Float32",
                    "shape": [2],
                    "byteLength": 2 * 4
                },
                {
                    "name": "dense_1/kernel",
                    "dataType": "Float32",
                    "shape": [128, 3],
                    "byteLength": 128 * 3 * 4
                },
                {
                    "name": "dense_1/bias",
                    "dataType": "Float32",
                    "shape": [3],
                    "byteLength": 3 * 4
                }
            ]
        }
    }

    truth_path = os.path.join(output_dir, "tiny_cnn_legacy.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {h5_path} ({os.path.getsize(h5_path)} bytes)")
    print(f"Generated {truth_path}")

def generate_tiny_weights_only(output_dir):
    model = keras.Sequential([
        layers.Input(shape=(4,), name="input_1"),
        layers.Dense(4, name="dense_w")
    ], name="tiny_weights")

    h5_path = os.path.join(output_dir, "tiny_weights_only.weights.h5")
    model.save_weights(h5_path)

    truth = {
        "modelId": "keras-tiny_weights_only",
        "format": "Keras",
        "modelType": "WeightsOnly",
        "subgraphCount": 1,
        "primarySubgraph": {
            "name": "main",
            "nodeCount": 1
        }
    }

    truth_path = os.path.join(output_dir, "tiny_weights_only.truth.json")
    with open(truth_path, "w") as f:
        json.dump(truth, f, indent=2)

    print(f"Generated {h5_path} ({os.path.getsize(h5_path)} bytes)")
    print(f"Generated {truth_path}")

def main():
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
    fixtures_dir = os.path.join(repo_root, "tests", "DotNodeViewer.Core.Tests", "Fixtures", "keras")
    os.makedirs(fixtures_dir, exist_ok=True)

    generate_tiny_sequential(fixtures_dir)
    generate_tiny_functional_residual(fixtures_dir)
    generate_tiny_cnn_legacy(fixtures_dir)
    generate_tiny_weights_only(fixtures_dir)

if __name__ == "__main__":
    main()

