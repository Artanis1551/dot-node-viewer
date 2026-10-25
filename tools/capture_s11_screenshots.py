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

BINARY_PATH = os.path.join(REPO_ROOT, "src", "DotNodeViewer.Desktop", "bin", "Release", "net10.0", "DotNodeViewer.Desktop")

def launch_app(model_path, scenario=None):
    cmd = [BINARY_PATH]
    if scenario:
        cmd.append(f"--test-scenario={scenario}")
    cmd.append(model_path)
    print(f"\n--- Launching DotNodeViewer.Desktop with {os.path.basename(model_path)} (scenario={scenario}) ---")
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=dict(os.environ, DISPLAY=":0")
    )

    disp = desktop_harness.get_x11_display()
    win = None
    for _ in range(50):
        time.sleep(0.2)
        win = desktop_harness.find_dotnodeviewer_window(disp)
        if win:
            try:
                name = win.get_wm_name()
                if name and ("—" in name or "â€”" in name or "- " in name):
                    break
            except Exception:
                pass

    if not win:
        proc.kill()
        raise RuntimeError("Window not found after launch!")

    time.sleep(1.2)
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
    p1, d1, w1 = launch_app(
        os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/onnx/if_subgraph.onnx"),
        scenario="drilldown"
    )
    try:
        desktop_harness.send_key_combo(d1, "f") # Zoom fit drilled subgraph
        time.sleep(0.6)
        save_and_copy(w1, "01-onnx-subgraph-drilldown.png", d1)
    finally:
        p1.terminate()
        p1.wait()

    # Scenario 2: TFLite Weight Inspector
    p2, d2, w2 = launch_app(
        os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/tflite/tiny_cnn_int8.tflite"),
        scenario="weights"
    )
    try:
        time.sleep(0.6)
        save_and_copy(w2, "02-tflite-weight-inspector.png", d2)
    finally:
        p2.terminate()
        p2.wait()

    # Scenario 3: ExecuTorch Neighborhood Highlighting
    p3, d3, w3 = launch_app(
        os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/executorch/tiny_mlp_portable.pte"),
        scenario="neighborhood"
    )
    try:
        desktop_harness.send_key_combo(d3, "f") # Zoom fit
        time.sleep(0.6)
        save_and_copy(w3, "03-executorch-xnnpack-neighborhood.png", d3)
    finally:
        p3.terminate()
        p3.wait()

    # Scenario 4: PyTorch Search Panel
    p4, d4, w4 = launch_app(
        os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/pytorch_pt2/tiny_cnn_export.pt2"),
        scenario="search"
    )
    try:
        time.sleep(0.6)
        save_and_copy(w4, "04-pytorch-search.png", d4)
    finally:
        p4.terminate()
        p4.wait()

    # Scenario 5: Keras Residual Minimap Dark
    p5, d5, w5 = launch_app(
        os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/keras/tiny_functional_residual.keras")
    )
    try:
        desktop_harness.send_key_combo(d5, "f") # Zoom fit
        time.sleep(0.6)
        save_and_copy(w5, "05-keras-residual-minimap-dark.png", d5)
    finally:
        p5.terminate()
        p5.wait()

    # Scenario 6: Keras Residual Minimap Light
    p6, d6, w6 = launch_app(
        os.path.join(REPO_ROOT, "tests/DotNodeViewer.Core.Tests/Fixtures/keras/tiny_functional_residual.keras"),
        scenario="light"
    )
    try:
        desktop_harness.send_key_combo(d6, "f") # Zoom fit
        time.sleep(0.6)
        save_and_copy(w6, "06-keras-residual-minimap-light.png", d6)
    finally:
        p6.terminate()
        p6.wait()

    print("\nAll 6 Stage S11 screenshots captured successfully!")

if __name__ == "__main__":
    main()

