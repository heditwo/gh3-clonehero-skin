"""Send key presses to GH3 and save a screenshot. Usage: keys.py <shot-name> [key[:hold_seconds] | sleep:N ...]"""
import subprocess
import sys
import time

from Xlib import XK, X, display
from Xlib.ext import xtest

d = display.Display()


def windows(parent):
    for child in parent.query_tree().children:
        yield child
        yield from windows(child)


game = next((w for w in windows(d.screen().root) if w.get_wm_name() == "Guitar Hero III: Legends of Rock"), None)
if game is not None:
    game.set_input_focus(X.RevertToPointerRoot, X.CurrentTime)
    d.sync()
for item in sys.argv[2:]:
    name, _, value = item.partition(":")
    if name == "sleep":
        time.sleep(float(value))
        continue
    code = d.keysym_to_keycode(XK.string_to_keysym(name))
    if code == 0:
        raise SystemExit(f"Unknown key: {name}")
    xtest.fake_input(d, X.KeyPress, code)
    d.sync()
    time.sleep(float(value) if value else 0.1)
    xtest.fake_input(d, X.KeyRelease, code)
    d.sync()
    time.sleep(0.6)
shot = f"/home/hedi/.cache/gh3ref/{sys.argv[1]}"
subprocess.run(["import", "-window", "root", shot + ".ppm"], check=True)
subprocess.run(["magick", shot + ".ppm", "-alpha", "off", "-resize", "800x450", shot + ".jpg"], check=True)
print(shot + ".jpg", "window" if game else "no-window")
