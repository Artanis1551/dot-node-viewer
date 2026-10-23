#!/usr/bin/env python3
"""
Desktop harness for live verification and screenshot capture of DotNodeViewer.Desktop on Linux X11/AT-SPI.
"""

import sys
import os
import time
import subprocess
from PIL import Image
import Xlib.display
import Xlib.X
import pyatspi

def get_x11_display():
    return Xlib.display.Display(os.environ.get("DISPLAY", ":0"))

def find_dotnodeviewer_window(disp):
    root = disp.screen().root
    query = root.query_tree()
    
    # Check top-level windows and their children
    for win in query.children:
        try:
            name = win.get_wm_name()
            wm_class = win.get_wm_class()
            if (name and "dot-node-viewer" in name.lower()) or (wm_class and any("dotnodeviewer" in c.lower() for c in wm_class)):
                geom = win.get_geometry()
                if geom.width > 100 and geom.height > 100:
                    return win
        except Exception:
            pass

        # check sub-children
        try:
            subquery = win.query_tree()
            for subwin in subquery.children:
                subname = subwin.get_wm_name()
                if subname and "dot-node-viewer" in subname.lower():
                    subgeom = subwin.get_geometry()
                    if subgeom.width > 100 and subgeom.height > 100:
                        return subwin
        except Exception:
            pass

    return None

def capture_window(win, output_path, disp):
    geom = win.get_geometry()
    w = geom.width
    h = geom.height

    raw_image = win.get_image(0, 0, w, h, Xlib.X.ZPixmap, 0xFFFFFFFF)
    img = Image.frombytes("RGB", (w, h), raw_image.data, "raw", "BGRX")
    
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    img.save(output_path)
    print(f"Captured screenshot to: {output_path} ({w}x{h})")

def dump_atspi_tree(app_name="DotNodeViewer"):
    reg = pyatspi.Registry
    target_app = None
    for i in range(reg.getDesktopCount()):
        desktop = reg.getDesktop(i)
        for child in desktop:
            if child and app_name.lower() in child.name.lower():
                target_app = child
                break
        if target_app:
            break

    if not target_app:
        print(f"Could not find AT-SPI application matching '{app_name}'")
        return []

    elements = []
    def walk(acc, depth=0):
        if not acc:
            return
        role = acc.getRoleName()
        name = acc.name
        elements.append((depth, role, name, acc))
        indent = "  " * depth
        print(f"{indent}[{role}] '{name}'")
        for child in acc:
            walk(child, depth + 1)

    print(f"=== AT-SPI Element Tree for {target_app.name} ===")
    walk(target_app)
    return elements

def click_element_by_name(app_name, name_substring):
    elements = dump_atspi_tree(app_name)
    for depth, role, name, acc in elements:
        if name and name_substring.lower() in name.lower():
            try:
                action = acc.queryAction()
                print(f"Invoking action on '{name}' ({role}). Actions: {action.nActions}")
                action.doAction(0)
                return True
            except Exception as e:
                print(f"Failed to invoke action on '{name}': {e}")
    return False

if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "dump"
    disp = get_x11_display()
    
    if cmd == "capture":
        out_path = sys.argv[2] if len(sys.argv) > 2 else "screenshot.png"
        win = find_dotnodeviewer_window(disp)
        if not win:
            print("DotNodeViewer window not found!")
            sys.exit(1)
        capture_window(win, out_path, disp)
    elif cmd == "dump":
        dump_atspi_tree()
    elif cmd == "toggle-theme":
        click_element_by_name("DotNodeViewer", "Theme")
