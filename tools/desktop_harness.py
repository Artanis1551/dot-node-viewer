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
import Xlib.ext.xtest
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

    raw_image = None
    for attempt in range(15):
        try:
            disp.sync()
            raw_image = win.get_image(0, 0, w, h, Xlib.X.ZPixmap, 0xFFFFFFFF)
            if raw_image:
                break
        except Exception:
            time.sleep(0.4)

    if not raw_image:
        raise RuntimeError(f"Failed to capture window image after {w}x{h} mapping")

    img = Image.frombytes("RGB", (w, h), raw_image.data, "raw", "BGRX")
    
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    img.save(output_path)
    print(f"Captured screenshot to: {output_path} ({w}x{h})")

def get_window_absolute_coordinates(win):
    geom = win.get_geometry()
    cur = win
    abs_x = 0
    abs_y = 0
    while cur:
        try:
            g = cur.get_geometry()
            abs_x += g.x
            abs_y += g.y
            query = cur.query_tree()
            if query.parent and query.parent.id == cur.id:
                break
            cur = query.parent
        except Exception:
            break
    return abs_x, abs_y, geom.width, geom.height

def click_window_ratio(win, disp, rx, ry):
    abs_x, abs_y, w, h = get_window_absolute_coordinates(win)
    target_x = int(abs_x + (w * rx))
    target_y = int(abs_y + (h * ry))
    
    print(f"Clicking at window ratio ({rx:.2f}, {ry:.2f}) -> absolute screen ({target_x}, {target_y})")
    Xlib.ext.xtest.fake_input(disp, Xlib.X.MotionNotify, x=target_x, y=target_y)
    disp.sync()
    time.sleep(0.05)
    Xlib.ext.xtest.fake_input(disp, Xlib.X.ButtonPress, detail=1)
    disp.sync()
    time.sleep(0.05)
    Xlib.ext.xtest.fake_input(disp, Xlib.X.ButtonRelease, detail=1)
    disp.sync()
    time.sleep(0.15)

def scroll_window(win, disp, rx, ry, is_up=True, clicks=3):
    abs_x, abs_y, w, h = get_window_absolute_coordinates(win)
    target_x = int(abs_x + (w * rx))
    target_y = int(abs_y + (h * ry))
    
    button = 4 if is_up else 5
    Xlib.ext.xtest.fake_input(disp, Xlib.X.MotionNotify, x=target_x, y=target_y)
    disp.sync()
    time.sleep(0.05)
    for _ in range(clicks):
        Xlib.ext.xtest.fake_input(disp, Xlib.X.ButtonPress, detail=button)
        disp.sync()
        time.sleep(0.02)
        Xlib.ext.xtest.fake_input(disp, Xlib.X.ButtonRelease, detail=button)
        disp.sync()
        time.sleep(0.05)
    time.sleep(0.15)

def drag_window_ratio(win, disp, rx1, ry1, rx2, ry2, button=1):
    abs_x, abs_y, w, h = get_window_absolute_coordinates(win)
    start_x = int(abs_x + (w * rx1))
    start_y = int(abs_y + (h * ry1))
    end_x = int(abs_x + (w * rx2))
    end_y = int(abs_y + (h * ry2))

    print(f"Dragging from ({start_x}, {start_y}) to ({end_x}, {end_y}) with button {button}")
    Xlib.ext.xtest.fake_input(disp, Xlib.X.MotionNotify, x=start_x, y=start_y)
    disp.sync()
    time.sleep(0.05)
    Xlib.ext.xtest.fake_input(disp, Xlib.X.ButtonPress, detail=button)
    disp.sync()
    time.sleep(0.05)

    steps = 10
    for s in range(1, steps + 1):
        cur_x = int(start_x + (end_x - start_x) * (s / steps))
        cur_y = int(start_y + (end_y - start_y) * (s / steps))
        Xlib.ext.xtest.fake_input(disp, Xlib.X.MotionNotify, x=cur_x, y=cur_y)
        disp.sync()
        time.sleep(0.02)

    Xlib.ext.xtest.fake_input(disp, Xlib.X.ButtonRelease, detail=button)
    disp.sync()
    time.sleep(0.15)

def send_key_combo(disp, *keys):
    import Xlib.XK
    keycodes = []
    for k in keys:
        keysym = Xlib.XK.string_to_keysym(k)
        if keysym == 0:
            # Try lowercase or fallback
            keysym = getattr(Xlib.XK, f"XK_{k}", 0)
        kc = disp.keysym_to_keycode(keysym)
        keycodes.append(kc)

    for kc in keycodes:
        if kc:
            Xlib.ext.xtest.fake_input(disp, Xlib.X.KeyPress, kc)
            disp.sync()
            time.sleep(0.02)

    for kc in reversed(keycodes):
        if kc:
            Xlib.ext.xtest.fake_input(disp, Xlib.X.KeyRelease, kc)
            disp.sync()
            time.sleep(0.02)
    time.sleep(0.15)

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
    win = find_dotnodeviewer_window(disp)
    
    if cmd == "capture":
        out_path = sys.argv[2] if len(sys.argv) > 2 else "screenshot.png"
        if not win:
            print("DotNodeViewer window not found!")
            sys.exit(1)
        capture_window(win, out_path, disp)
    elif cmd == "dump":
        dump_atspi_tree()
    elif cmd == "click-ratio":
        rx = float(sys.argv[2])
        ry = float(sys.argv[3])
        if not win:
            print("DotNodeViewer window not found!")
            sys.exit(1)
        click_window_ratio(win, disp, rx, ry)
    elif cmd == "scroll":
        direction = sys.argv[2] if len(sys.argv) > 2 else "up"
        rx = float(sys.argv[3]) if len(sys.argv) > 3 else 0.4
        ry = float(sys.argv[4]) if len(sys.argv) > 4 else 0.5
        clicks = int(sys.argv[5]) if len(sys.argv) > 5 else 3
        if not win:
            print("DotNodeViewer window not found!")
            sys.exit(1)
        scroll_window(win, disp, rx, ry, is_up=(direction == "up"), clicks=clicks)
    elif cmd == "toggle-theme":
        click_element_by_name("DotNodeViewer", "Theme")
    elif cmd == "zoom-in":
        click_element_by_name("DotNodeViewer", "Zoom In")
    elif cmd == "zoom-out":
        click_element_by_name("DotNodeViewer", "Zoom Out")
    elif cmd == "zoom-fit":
        click_element_by_name("DotNodeViewer", "Zoom to Fit")
    elif cmd == "drag-ratio":
        rx1 = float(sys.argv[2])
        ry1 = float(sys.argv[3])
        rx2 = float(sys.argv[4])
        ry2 = float(sys.argv[5])
        button = int(sys.argv[6]) if len(sys.argv) > 6 else 1
        if not win:
            print("DotNodeViewer window not found!")
            sys.exit(1)
        drag_window_ratio(win, disp, rx1, ry1, rx2, ry2, button=button)
    elif cmd == "key-combo":
        keys = sys.argv[2:]
        send_key_combo(disp, *keys)
