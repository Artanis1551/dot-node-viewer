#!/usr/bin/env python3
"""
Automated live UI capture script for Stage S11 screenshots using tools/desktop_harness.py.
"""

import os
import sys
import time
import subprocess
import shutil

import Xlib.display
import desktop_harness

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SCREENSHOTS_DIR = os.path.join(REPO_ROOT, "docs", "screenshots", "S11")
DOCS_SITE_DIR = os.path.join(REPO_ROOT, "docs", "site", "images", "s11")

os.makedirs(SCREENSHOTS_DIR, exist_ok=True)
os.makedirs(DOCS_SITE_DIR, exist_ok=True)

def launch_app(model_path):
    print(f"\n--- Launching DotNodeViewer.Desktop with {os.path.basename(model_path)} ---")
    proc = subprocess.Popen(
        [
            "dotnet", "run",
            "--project", os.path.join(REPO_ROOT, "src", "DotNodeViewer.Desktop"),
            "-c", "Release",
            "--no-build", "--no-restore",
            "--", model_path
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=dict(os.environ, DISPLAY=":0")
    )

    disp = desktop_harness.get_x11_display()
    win = None
    for _ in range(50):
        time.sleep(0.3)
        win = desktop_harness.find_dotnodeviewer_window(disp)
        if win:
            break

    if not win:
        proc.kill()
        raise RuntimeError("Window not found after launch!")

    time.sleep(2.5)
    return proc, disp, win

def save_and_copy(win, filename, disp):
    out1 = os.path.join(SCREENSHOTS_DIR, filename)
    out2 = os.path.join(DOCS_SITE_DIR, filename)
    desktop_harness.capture_window(win, out1, disp)
    shutil.copyfile(out1, out2)
    print(f"Saved: {out1} and {out2}")

def main():
    disp = desktop_harness.get_x11_display()

    # Scenario 1: ONNX Subgraph Drill-down
    p1, d1, w1 = launch_app(os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/onnx/if_subgraph.onnx"))
    try:
        # Click on the If operator node (middle-ish of canvas, around ratio 0.35, 0.40)
        desktop_harness.click_window_ratio(w1, d1, 0.35, 0.40)
        time.sleep(0.5)
        # Click drill-down into subgraph button or click Subgraph selector dropdown (around ratio 0.38, 0.025)
        # Let's switch subgraph via selector if needed
        desktop_harness.click_window_ratio(w1, d1, 0.38, 0.025)
        time.sleep(0.3)
        desktop_harness.send_key_combo(d1, "Down", "Return")
        time.sleep(0.6)
        save_and_copy(w1, "01-onnx-subgraph-drilldown.png", d1)
    finally:
        p1.terminate()
        p1.wait()

    # Scenario 2: TFLite Weight Inspector
    p2, d2, w2 = launch_app(os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite"))
    try:
        # Click on CONV_2D node (ratio ~0.35, 0.35)
        desktop_harness.click_window_ratio(w2, d2, 0.35, 0.35)
        time.sleep(0.5)
        # Click "Inspect Weights" in properties panel (right side, ratio ~0.92, 0.32)
        desktop_harness.click_window_ratio(w2, d2, 0.92, 0.32)
        time.sleep(0.6)
        save_and_copy(w2, "02-tflite-weight-inspector.png", d2)
    finally:
        p2.terminate()
        p2.wait()

    # Scenario 3: ExecuTorch XNNPACK Neighborhood Highlighting
    p3, d3, w3 = launch_app(os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/executorch/tiny_cnn_xnnpack.pte"))
    try:
        # Click on delegate node (ratio ~0.35, 0.38)
        desktop_harness.click_window_ratio(w3, d3, 0.35, 0.38)
        time.sleep(1.0)
        save_and_copy(w3, "03-executorch-xnnpack-neighborhood.png", d3)
    finally:
        p3.terminate()
        p3.wait()

    # Scenario 4: PyTorch Search Panel
    p4, d4, w4 = launch_app(os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/pytorch_pt2/tiny_cnn_export.pt2"))
    try:
        # Trigger Ctrl+F
        desktop_harness.send_key_combo(d4, "Control_L", "f")
        time.sleep(0.5)
        # Type "conv"
        desktop_harness.send_key_combo(d4, "c", "o", "n", "v")
        time.sleep(1.0)
        save_and_copy(w4, "04-pytorch-search.png", d4)
    finally:
        p4.terminate()
        p4.wait()

    # Scenario 5 & 6: Keras Residual Minimap (Dark & Light)
    p5, d5, w5 = launch_app(os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/keras/tiny_functional_residual.keras"))
    try:
        # Zoom fit to see skip connections and minimap clearly
        desktop_harness.send_key_combo(d5, "f")
        time.sleep(1.0)
        save_and_copy(w5, "05-keras-residual-minimap-dark.png", d5)

        # Click theme toggle (top right, ratio ~0.94, 0.025)
        desktop_harness.click_window_ratio(w5, d5, 0.94, 0.025)
        time.sleep(1.0)
        save_and_copy(w5, "06-keras-residual-minimap-light.png", d5)
    finally:
        p5.terminate()
        p5.wait()

    print("\nAll 6 Stage S11 screenshots captured successfully!")

if __name__ == "__main__":
    main()

