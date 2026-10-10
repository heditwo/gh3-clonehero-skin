"""Start Clone Hero in a nested Xwayland display, run timed steps, and save the loader log with memory and file samples.

Usage: measure-load.py --game DIR --out DIR [--song CHART] [--timeout S] [--env NAME=VALUE ...] STEP ...

Steps (in order):
  wait:REGEX       wait for a loader log line that matches REGEX after the line of the last wait step
  seen:REGEX       wait for a loader log line that matches REGEX anywhere in the log
  sleep:SECONDS    wait
  key:NAME[:HOLD]  send a key press to the game window (X keysym names: Return, Escape, Down, F6, ...)
  shot:NAME        save the display as OUT/NAME.png
  run:COMMAND      run a shell command with DISPLAY set to the game display and GAME_PID to the game process
The game runs with GH3_SKIN_TIMING=1. OUT gets Latest.log, samples.csv (time, resident and peak memory, read calls and
bytes, open file count of the game process), and run.json. Run it under the record lock (docs/VERIFICATION.md).
"""

import argparse
import contextlib
import json
import os
import re
import signal
import subprocess
import time
from pathlib import Path

project = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument("--game", required=True, type=Path)
parser.add_argument("--out", required=True, type=Path)
parser.add_argument("--song", type=Path)
parser.add_argument("--timeout", type=float, default=240)
parser.add_argument("--width", type=int, default=1600)
parser.add_argument("--height", type=int, default=900)
parser.add_argument("--env", action="append", default=[])
parser.add_argument("steps", nargs="*")
args = parser.parse_args()
game = args.game.resolve()
out = args.out
out.mkdir(parents=True, exist_ok=True)
log_path = game / "MelonLoader" / "Latest.log"
display_number = 100 + os.getpid() % 10000
display = f":{display_number}"
instance = json.loads(subprocess.check_output(["hyprctl", "instances", "-j"]))[0]["instance"]


def game_processes():
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if os.readlink(entry / "cwd") != str(game):
                continue
            if f"DISPLAY={display}".encode() not in (entry / "environ").read_bytes().split(b"\0"):
                continue
            yield int(entry.name)
        except OSError:
            continue


def stop_all():
    # MelonLoader leaves a second clonehero process; stop every process of this game folder on this display.
    for sig in (signal.SIGTERM, signal.SIGKILL):
        for pid in list(game_processes()):
            with contextlib.suppress(OSError):
                os.kill(pid, sig)
        time.sleep(0.5)


xwayland = subprocess.Popen(
    ["Xwayland", display, "-noreset", "-geometry", f"{args.width}x{args.height}"],
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
    env={**os.environ, "WAYLAND_DISPLAY": os.environ.get("WAYLAND_DISPLAY", "wayland-1")},
)
game_process = None
started = time.time()
try:
    for _ in range(50):
        if (
            subprocess.run(
                ["xrandr", "--query"], env={**os.environ, "DISPLAY": display}, capture_output=True
            ).returncode
            == 0
        ):
            break
        time.sleep(0.2)
    address = ""
    for _ in range(50):
        clients = json.loads(subprocess.check_output(["hyprctl", "-i", instance, "clients", "-j"]))
        address = next((c["address"] for c in clients if c["pid"] == xwayland.pid), "")
        if address:
            break
        time.sleep(0.2)
    if address:
        for command in (
            f"hl.dsp.window.float({{ action = 'on', window = 'address:{address}' }})",
            f"hl.dsp.window.resize({{ x = {args.width}, y = {args.height}, window = 'address:{address}' }})",
            f"hl.dsp.window.move({{ x = 3840, y = 90, window = 'address:{address}' }})",
        ):
            subprocess.run(["hyprctl", "-i", instance, "dispatch", command], capture_output=True)
    from Xlib import XK, X, display as xdisplay, protocol

    log_path.write_text("")
    env = {
        **os.environ,
        "WAYLAND_DISPLAY": "",
        "DISPLAY": display,
        "GH3_CLONE_HERO_DIR": str(game),
        "GH3_SKIN_TIMING": "1",
    }
    for item in args.env:
        name, _, value = item.partition("=")
        env[name] = value
    command = [str(project / "run-gh3-skin.sh")]
    if args.song:
        env["GH3_SKIN_PREVIEW_SONG"] = str(args.song)
        env["GH3_SKIN_PREVIEW_WIDTH"] = str(args.width)
        env["GH3_SKIN_PREVIEW_HEIGHT"] = str(args.height)
        command.append("--preview")
    else:
        command += ["-screen-fullscreen", "0", "-screen-width", str(args.width), "-screen-height", str(args.height)]
    started = time.time()
    game_process = subprocess.Popen(command, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    x = xdisplay.Display(display)
    samples = open(out / "samples.csv", "w")  # noqa: SIM115 - written during the whole run, closed after it
    samples.write("seconds,rss_mb,peak_mb,read_calls,read_mb,open_files\n")
    seen = 0
    events = []

    def sample():
        pid = game_process.pid
        try:
            status = (Path("/proc") / str(pid) / "status").read_text()
            io = dict(line.split(": ") for line in (Path("/proc") / str(pid) / "io").read_text().splitlines())
            files = len(os.listdir(f"/proc/{pid}/fd"))
        except OSError:
            return
        rss_match, peak_match = re.search(r"VmRSS:\s+(\d+)", status), re.search(r"VmHWM:\s+(\d+)", status)
        if not rss_match or not peak_match:
            return
        rss, peak = int(rss_match.group(1)) / 1024, int(peak_match.group(1)) / 1024
        samples.write(
            f"{time.time() - started:.2f},{rss:.0f},{peak:.0f},{io['syscr']},{int(io['rchar']) / 1e6:.0f},{files}\n"
        )

    # Each wait step starts after the line that matched the step before it.
    def matched(pattern, anywhere=False):
        rows = log_path.read_text(errors="replace").splitlines()
        for index in range(0 if anywhere else matched.seen, len(rows)):
            if pattern.search(rows[index]):
                if not anywhere:
                    matched.seen = index + 1
                return True
        return False

    matched.seen = 0
    last_sample = 0.0
    for step in args.steps:
        kind, _, value = step.partition(":")
        events.append({"step": step, "at": round(time.time() - started, 2)})
        if kind in ("wait", "seen"):
            pattern = re.compile(value)
            while True:
                if time.time() - started > args.timeout:
                    raise SystemExit(f"timeout at step {step}")
                if game_process.poll() is not None:
                    raise SystemExit(f"the game stopped at step {step}")
                if matched(pattern, kind == "seen"):
                    break
                if time.time() - last_sample > 0.25:
                    sample()
                    last_sample = time.time()
                time.sleep(0.05)
        elif kind == "sleep":
            end = time.time() + float(value)
            while time.time() < end:
                sample()
                time.sleep(0.25)
        elif kind == "key":
            # Synthetic key events to the game window: Clone Hero reads them, and XTest input does not reach a nested
            # Xwayland window without the compositor's keyboard focus.
            def windows(parent):
                for child in parent.query_tree().children:
                    yield child
                    yield from windows(child)

            name, _, hold = value.partition(":")
            code = x.keysym_to_keycode(XK.string_to_keysym(name))
            if code == 0:
                raise SystemExit(f"unknown key {name}")
            window = next(w for w in windows(x.screen().root) if (w.get_wm_class() or ("",))[0] == "clonehero")
            window.set_input_focus(X.RevertToParent, X.CurrentTime)
            x.sync()
            for event_type, hold_time in (
                (protocol.event.KeyPress, float(hold) if hold else 0.08),
                (protocol.event.KeyRelease, 0),
            ):
                window.send_event(
                    event_type(
                        time=X.CurrentTime,
                        root=x.screen().root,
                        window=window,
                        same_screen=1,
                        child=X.NONE,
                        root_x=10,
                        root_y=10,
                        event_x=10,
                        event_y=10,
                        state=0,
                        detail=code,
                    ),
                    propagate=True,
                )
                x.sync()
                time.sleep(hold_time)
        elif kind == "shot":
            root = x.screen().root
            geometry = root.get_geometry()
            image = root.get_image(0, 0, geometry.width, geometry.height, X.ZPixmap, 0xFFFFFFFF)
            from PIL import Image

            Image.frombytes("RGB", (geometry.width, geometry.height), image.data, "raw", "BGRX").save(
                out / f"{value}.png"
            )
        elif kind == "run":
            subprocess.run(value, shell=True, env={**os.environ, "DISPLAY": display, "GAME_PID": str(game_process.pid)})
        else:
            raise SystemExit(f"unknown step {step}")
    sample()
    samples.close()
    (out / "run.json").write_text(
        json.dumps(
            {
                "game": str(game),
                "song": str(args.song or ""),
                "env": args.env,
                "steps": events,
                "seconds": round(time.time() - started, 2),
            },
            indent=2,
        )
    )
finally:
    stop_all()
    if game_process:
        with contextlib.suppress(subprocess.TimeoutExpired):
            game_process.wait(5)
    xwayland.terminate()
    xwayland.wait()
    if log_path.exists():
        (out / "Latest.log").write_text(log_path.read_text(errors="replace"))
