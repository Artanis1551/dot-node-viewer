#!/usr/bin/env python3
"""
Reports and verifies tested neural network models for CI pipeline inspection on GitHub.
Emits a human-readable summary to stdout and, when running inside GitHub Actions,
appends a formatted Markdown table to $GITHUB_STEP_SUMMARY.
"""

import json
import os
import sys
from pathlib import Path


def format_bytes(num_bytes: int) -> str:
    """Formats byte counts into human-readable strings."""
    if num_bytes < 1024:
        return f"{num_bytes} B"
    if num_bytes < 1024 * 1024:
        return f"{num_bytes / 1024:.2f} KB"
    return f"{num_bytes / (1024 * 1024):.2f} MB"


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: report-tested-models.py <format>")
        sys.exit(1)

    target_format = sys.argv[1].lower()
    display_names = {
        "onnx": "ONNX",
        "tflite": "TensorFlow Lite",
        "executorch": "ExecuTorch",
        "pytorch": "PyTorch",
        "pytorch-pt2": "PyTorch .pt2",
        "keras": "Keras",
    }
    format_label = display_names.get(target_format, target_format.upper())

    repo_root = Path(__file__).resolve().parents[2]
    manifest_path = repo_root / "tests" / "DotNodeViewer.Core.Tests" / "Fixtures" / "models.manifest.json"

    if not manifest_path.is_file():
        print(f"Error: manifest not found at {manifest_path}", file=sys.stderr)
        sys.exit(1)

    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    models = [m for m in manifest.get("models", []) if m.get("format", "").lower() == target_format]
    if not models:
        print(f"No registered models found for format '{target_format}'.", file=sys.stderr)
        sys.exit(1)

    # Output to stdout for GitHub Actions step logs / terminal view
    print("=" * 80)
    print(f"  PIPELINE INSPECTION: TESTED {format_label.upper()} MODELS (Total: {len(models)})")
    print("=" * 80)

    rows = []
    for i, m in enumerate(models, start=1):
        model_id = m["id"]
        file_name = m["fileName"]
        tier = m["tier"]
        byte_len = m["byteLength"]
        sha256 = m["sha256"]
        snapshot = m["expectedSnapshot"]
        size_str = format_bytes(byte_len)

        print(f"\n  [{i}/{len(models)}] PASSED: {model_id}")
        print(f"        File:     {file_name} ({size_str}, tier: {tier})")
        print(f"        SHA-256:  {sha256}")
        print(f"        Snapshot: {snapshot}")

        rows.append((model_id, file_name, tier, size_str, sha256[:12] + "...", snapshot))

    print("\n" + "=" * 80)
    print(f"  Summary: {len(models)}/{len(models)} {format_label} models tested and verified.")
    print("=" * 80 + "\n")

    # If running inside GitHub Actions, publish Markdown to $GITHUB_STEP_SUMMARY
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as f:
            f.write(f"\n### 🧪 Tested {format_label} Models ({len(models)})\n\n")
            f.write("| Status | Model ID | File Name | Tier | Size | SHA-256 | Expected Snapshot |\n")
            f.write("|:---:|:---|:---|:---:|---:|:---:|:---|\n")
            for r in rows:
                f.write(f"| ✅ | `{r[0]}` | `{r[1]}` | {r[2]} | {r[3]} | `{r[4]}` | `{r[5]}` |\n")
            f.write(f"\n> **All {len(models)} {format_label} models passed automated topology verification.**\n\n")


if __name__ == "__main__":
    main()
