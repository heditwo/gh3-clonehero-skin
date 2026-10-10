"""One original-GH3 session with Havok body sampling (machine-specific paths of the 2026-10-10 run). Run under both locks:
flock /tmp/opencode/gh3-original.lock flock /tmp/opencode/gh3-record.lock uv run --with numpy --with pefile python gh3_session.py <char> <out> <secs>
Env: POS, FOV, BONE, ZONE (camera zone), CAM (1), LEVEL, SONG, DOWNS."""

import ctypes
import filecmp
import json
import os
import shutil
import struct
import subprocess
import sys
import threading
import time

import numpy as np

char, out, secs = sys.argv[1], sys.argv[2], float(sys.argv[3])
os.makedirs(out, exist_ok=True)
H = str(__import__("pathlib").Path(__file__).resolve().parent)
G = "/home/hedi/games/gh3-matched-capture/Guitar Hero III/DATA"
T = "/home/hedi/games/gh3-test-tools/game/DATA"
O = "/home/hedi/.cache/rockgod-gh3/orig"
SAVE = os.path.expanduser("~/Documents/Aspyr/Guitar Hero III/Save")
SAVEBAK = os.path.expanduser("~/.cache/hair-motion/save-orig/Save")
libc = ctypes.CDLL(None)
assert libc.prctl(36, 1, 0, 0, 0) == 0
env = dict(os.environ)
env.update(WAYLAND_DISPLAY="wayland-1", XDG_RUNTIME_DIR=f"/run/user/{os.getuid()}", MISE_QUIET="1")
env["HYPRLAND_INSTANCE_SIGNATURE"] = json.loads(
    subprocess.run(["hyprctl", "instances", "-j"], capture_output=True, text=True).stdout
)[0]["instance"]
log = open(f"{out}/run.log", "a")


def say(*a):
    print(*a, flush=True)
    print(*a, file=log, flush=True)


def restore():
    subprocess.run(["bash", os.path.expanduser("~/.cache/gh3ref/stop-gh3.sh")], env=env)
    time.sleep(2)
    for p in os.listdir("/proc"):
        if p.isdigit():
            try:
                if b"GH3.exe" in open(f"/proc/{p}/cmdline", "rb").read():
                    os.kill(int(p), 9)
            except Exception:
                pass
    shutil.rmtree(SAVE, ignore_errors=True)
    shutil.copytree(SAVEBAK, SAVE, symlinks=True)
    for z in ["z_dive", "z_party", "z_artdeco"]:
        shutil.copyfile(f"{O}/{z}.pak.xen", f"{G}/ZONES/{z}.pak.xen")
    shutil.copyfile(f"{O}/qb.pab.xen", f"{G}/PAK/qb.pab.xen")
    ok = all(
        filecmp.cmp(f"{T}/{f}", f"{G}/{f}", shallow=False)
        for f in ["ZONES/z_dive.pak.xen", "ZONES/z_party.pak.xen", "ZONES/z_artdeco.pak.xen", "PAK/qb.pab.xen"]
    )
    same = subprocess.run(["diff", "-r", SAVE, SAVEBAK], capture_output=True).returncode == 0
    say("restored files byte-equal" if ok else "RESTORE FAILED", "save equal" if same else "SAVE DIFFERS")


VT = 0x8CAE30


def gh3pid():
    for p in os.listdir("/proc"):
        if not p.isdigit():
            continue
        try:
            c = open(f"/proc/{p}/cmdline", "rb").read()
        except Exception:
            continue
        if b"GH3.exe" in c and b"explorer" not in c and b"winedevice" not in c:
            return int(p)


def scan(pid):
    f = open(f"/proc/{pid}/mem", "rb", 0)
    bodies = []
    for line in open(f"/proc/{pid}/maps"):
        a, perm = line.split()[:2]
        s, e = [int(x, 16) for x in a.split("-")]
        if "rw" not in perm or e > 0x100000000 or e - s > 0x20000000:
            continue
        try:
            f.seek(s)
            b = f.read(e - s)
        except Exception:
            continue
        arr = np.frombuffer(b[: len(b) // 4 * 4], np.uint32)
        for i in np.nonzero(arr == VT)[0]:
            addr = s + 4 * int(i)
            try:
                f.seek(addr)
                rec = f.read(416)
                name_ptr = struct.unpack_from("<I", rec, 72)[0]
                f.seek(name_ptr)
                name = f.read(32).split(b"\0")[0].decode("ascii")
            except Exception:
                continue
            if not name.startswith("Bone_"):
                continue
            mtype = rec[168]
            inv = struct.unpack_from("<4f", rec, 368)
            ms = struct.unpack_from("<4f", rec, 176 + 168)
            bodies.append(
                dict(
                    addr=addr,
                    name=name,
                    type=mtype,
                    invmass=inv[3],
                    invI=inv[:3],
                    maxLin=ms[0],
                    maxAng=ms[1],
                    linDamp=ms[2],
                    angDamp=ms[3],
                )
            )
    return bodies


def sampler(t_start):
    while time.time() < t_start:
        time.sleep(0.2)
    pid = gh3pid()
    bodies = scan(pid)
    say("bodies", len(bodies))
    json.dump(bodies, open(f"{out}/bodies.json", "w"), indent=1)
    if not bodies:
        return
    f = open(f"/proc/{pid}/mem", "rb", 0)
    rows = []
    ts = []
    t0 = time.time()
    while time.time() - t0 < secs - 14:
        t = time.time()
        row = []
        try:
            for b in bodies:
                f.seek(b["addr"] + 176)
                row.append(f.read(64))
        except Exception as e:
            say("sample error", e)
            break
        rows.append(b"".join(row))
        ts.append(t)
        time.sleep(max(0, 1 / 300 - (time.time() - t)))
    data = np.frombuffer(b"".join(rows), np.float32).reshape(len(rows), len(bodies), 16)
    np.savez_compressed(f"{out}/bodies.npz", t=np.array(ts), m=data)
    say("samples", data.shape)


try:
    if os.environ.get("SAVE_IN"):
        shutil.rmtree(SAVE, ignore_errors=True)
        shutil.copytree(os.environ["SAVE_IN"], SAVE, symlinks=True)
    r = subprocess.run(
        [
            "uv",
            "run",
            "-q",
            "--with",
            "numpy",
            "python",
            f"{H}/gh3_patch.py",
            char,
            os.environ.get("POS", "0,1.9,1"),
            os.environ.get("FOV", "30"),
            os.environ.get("BONE", "bone_head"),
        ],
        env=env,
        capture_output=True,
        text=True,
    )
    say("patch", r.stdout.strip(), r.stderr[-500:])
    if r.returncode:
        raise SystemExit(1)
    subprocess.run(["bash", os.path.expanduser("~/.cache/gh3ref/start-gh3-gpu.sh"), ":9310"], env=env)
    pin = subprocess.Popen(["bash", os.path.expanduser("~/.cache/gh3ref/pin-window.sh"), ":9310", "600"], env=env)
    time.sleep(5)
    nav = subprocess.run(
        [
            "uv",
            "run",
            "-q",
            "--with",
            "python-xlib",
            "--with",
            "numpy",
            "--with",
            "pillow",
            "python",
            f"{H}/gh3_nav.py",
            out,
            os.environ.get("DOWNS", "0"),
        ],
        env={**env, "DISPLAY": ":9310"},
        capture_output=True,
        text=True,
    )
    say("nav", nav.stdout, nav.stderr[-300:])
    if nav.returncode:
        raise SystemExit(1)
    if os.environ.get("NAV_ONLY"):
        if os.environ.get("SAVE_OUT"):
            subprocess.run(["bash", os.path.expanduser("~/.cache/gh3ref/stop-gh3.sh")], env=env)
            time.sleep(3)
            shutil.rmtree(os.environ["SAVE_OUT"], ignore_errors=True)
            shutil.copytree(SAVE, os.environ["SAVE_OUT"], symlinks=True)
            say("saved", os.environ["SAVE_OUT"])
        raise SystemExit(0)
    th = threading.Thread(target=sampler, args=(time.time() + 12,))
    th.start()
    rec = subprocess.run(
        ["bash", os.path.expanduser("~/.cache/gh3ref/agent-fingers3/tools/rec.sh"), f"{out}/gh3.mp4", str(secs)],
        env=env,
        capture_output=True,
        text=True,
    )
    say("rec", rec.stdout[-300:])
    th.join()
    pin.kill()
finally:
    restore()
