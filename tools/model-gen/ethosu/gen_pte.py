#!/usr/bin/env python3
"""Deterministic ExecuTorch Ethos-U fixture generator for DotNodeViewer."""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import torch
import torch.nn as nn
from executorch.backends.arm.test.tester.test_pipeline import EthosU55PipelineINT


class TinyEthosUCNN(nn.Module):
    def __init__(self):
        super().__init__()
        self.conv = nn.Conv2d(3, 4, 3, padding=1)
        self.relu = nn.ReLU()

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.relu(self.conv(x))


def build_pte_bytes() -> bytes:
    torch.manual_seed(42)
    model = TinyEthosUCNN().eval()
    example_inputs = (torch.randn(1, 3, 8, 8),)

    pipe = EthosU55PipelineINT(
        model,
        example_inputs,
        aten_ops=[],
        run_on_fvp=False,
    )
    pipe.add_stage(pipe.tester.serialize)
    pipe.run()
    return pipe.tester.get_artifact()


def main():
    parser = argparse.ArgumentParser(description="Generate ExecuTorch EthosU model")
    parser.add_argument("--output", type=str, required=True, help="Output .pte file path")
    args = parser.parse_args()

    pte_bytes = build_pte_bytes()
    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(pte_bytes)
    print(f"Generated {out_path} ({len(pte_bytes)} bytes)")


if __name__ == "__main__":
    main()

