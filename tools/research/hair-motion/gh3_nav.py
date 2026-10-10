"""Drive GH3 from boot to the quickplay setlist by screen matching, then press Down N times.
Usage: DISPLAY=:9310 nav.py <out-dir> <downs>"""
import os, subprocess, sys, time
from pathlib import Path

import numpy as np
from PIL import Image
from Xlib import XK, X, display
from Xlib.ext import xtest

REF = Path.home() / '.cache/rockgod-gh3/navref'
out, downs = Path(sys.argv[1]), int(sys.argv[2])
d = display.Display()


def windows(parent):
    for child in parent.query_tree().children:
        yield child
        yield from windows(child)


def press(*names):
    game = next((w for w in windows(d.screen().root) if w.get_wm_name() == "Guitar Hero III: Legends of Rock"), None)
    if game is not None:
        game.set_input_focus(X.RevertToPointerRoot, X.CurrentTime); d.sync()
    for name in names:
        code = d.keysym_to_keycode(XK.string_to_keysym(name))
        xtest.fake_input(d, X.KeyPress, code); d.sync(); time.sleep(0.1)
        xtest.fake_input(d, X.KeyRelease, code); d.sync(); time.sleep(0.6)


def thumb(img):
    return np.asarray(img.convert('L').resize((80, 45)), dtype=np.float32)


refs = {p.stem: thumb(Image.open(p)) for p in REF.glob('*.jpg')}


def shot(i):
    path = out / f'nav{i:02d}.png'
    subprocess.run(['import', '-window', 'root', str(path)], check=True)
    img = Image.open(path).convert('RGB'); img.resize((800, 450)).save(path.with_suffix('.jpg')); path.unlink()
    t = thumb(img)
    scores = sorted((float(np.mean((t - r) ** 2)), name) for name, r in refs.items())
    return scores[0][1], scores[0][0], scores[1]


actions = {'title': ['Return'], 'warn': ['Down', 'Return'], 'damaged': ['Down', 'Return'], 'instr1': ['Return'], 'instr2': ['Return'],
           'instr3': ['Return'], 'menu': ['Down', 'Down', 'Return'], 'difficulty': ['Down', 'Down', 'Return']}
time.sleep(15)
instr_n = 0
instr_keys = ['Return', 'space', 'F1', 'Escape', 'v', '1', 'a', 'Up', 'Return', 'KP_Enter', 'g', 'z']
for i in range(60):
    name, score, second = shot(i)
    print(i, name, round(score), second, flush=True)
    if score > (600 if name == 'setlist' else 60):
        time.sleep(3); continue
    if name == 'setlist':
        if os.environ.get('BONUS') == '1': press('4'); time.sleep(3)
        press(*['Down'] * downs); time.sleep(2); shot(99); print('setlist reached'); sys.exit(0)
    if name in ('online', 'login', 'invalid'):
        sys.exit('went online')
    if name.startswith('instr') and os.environ.get('PREP') == '1':
        time.sleep(60); print('prep done'); sys.exit(0)
    if name == 'damaged' and os.environ.get('PREP') == '1':
        press('Return'); time.sleep(6); continue
    if name.startswith('instr'):
        k = instr_keys[instr_n % len(instr_keys)]; instr_n += 1
        print('  instr key', k, flush=True); press(k); time.sleep(4); continue
    if name in actions:
        press(*actions[name]); time.sleep(6)
    else:
        time.sleep(3)
sys.exit('no setlist')
