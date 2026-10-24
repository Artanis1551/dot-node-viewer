#!/usr/bin/env python3
"""
Automated capture script for W06 drag-and-drop live verification screenshots.
"""

import os
import sys
import time
import subprocess
from desktop_harness import get_x11_display, find_dotnodeviewer_window, capture_window, click_window_ratio

def run_and_capture(args, output_path, wait_seconds=1.0):
    cmd = ["dotnet", "run", "--project", "src/DotNodeViewer.Desktop", "-c", "Release", "--no-build", "--"] + args
    print(f"Launching: {' '.join(cmd)}")
    env = os.environ.copy()
    env["DISPLAY"] = env.get("DISPLAY", ":0")
    if "XAUTHORITY" not in env and os.path.exists(os.path.expanduser("~/.Xauthority")):
        env["XAUTHORITY"] = os.path.expanduser("~/.Xauthority")

    disp = get_x11_display()

    # Ensure any lingering previous window is gone
    for _ in range(20):
        if find_dotnodeviewer_window(disp) is None:
            break
        time.sleep(0.2)

    proc = subprocess.Popen(cmd, env=env)

    try:
        start = time.time()
        win = None
        while time.time() - start < 8.0:
            win = find_dotnodeviewer_window(disp)
            if win:
                break
            time.sleep(0.2)

        if not win:
            raise RuntimeError(f"Could not find DotNodeViewer window for args: {args}")

        time.sleep(0.5)
        try:
            subprocess.run(["wmctrl", "-i", "-a", hex(win.id)], check=False)
        except Exception:
            pass

        time.sleep(0.5)
        click_window_ratio(win, disp, 0.5, 0.5)
        time.sleep(wait_seconds)

        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        capture_window(win, output_path, disp)
        print(f"Captured: {output_path}")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=2.0)
        except subprocess.TimeoutExpired:
            proc.kill()
        time.sleep(0.5)
        for _ in range(20):
            if find_dotnodeviewer_window(disp) is None:
                break
            time.sleep(0.2)

if __name__ == "__main__":
    os.chdir(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

    # 1. Empty state
    run_and_capture([], "docs/screenshots/W06/01-empty-state-supported-formats.png", wait_seconds=1.5)

    # 2. Valid drop overlay
    run_and_capture(["--test-drop-overlay=valid"], "docs/screenshots/W06/02-visual-drop-overlay-valid.png", wait_seconds=1.5)

    # 3. Invalid drop overlay
    run_and_capture(["--test-drop-overlay=invalid"], "docs/screenshots/W06/03-visual-drop-overlay-invalid.png", wait_seconds=1.5)

    # 4. Loaded model canvas after drop
    run_and_capture(["tests/DotNodeViewer.Core.Tests/Fixtures/onnx/tiny_mlp.onnx"], "docs/screenshots/W06/04-dropped-model-graph-canvas.png", wait_seconds=2.0)

    print("All W06 screenshots captured successfully!")

