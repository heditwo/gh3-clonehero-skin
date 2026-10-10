"""All profiles, My Curse 240 s as in the game: head-relative hair motion before and after, and head capsule clearance."""

import json, sys, numpy as np
from hairlib import qmat, REPO, WORK

lib = json.load(open(str(REPO) + "/assets/libraries/gh3-characters-v1/characters.json"))
B = WORK + "/bench/"


def load(path, hair):
    n = 1 + len(hair["joints"])
    d = np.fromfile(path, np.float32).reshape(-1, 1 + 7 * n).astype(float)
    return d[:, 0], d[:, 1:].reshape(len(d), n, 7)


def analyse(path, hair):
    t, P = load(path, hair)
    Hp, Hq = P[:, 0, :3], P[:, 0, 3:]
    HR = qmat(Hq)
    onhead = {hair["head"]}
    for j in hair["joints"]:
        if j["parent"] in onhead:
            onhead.add(j["bone"])
    cap = hair["capsule"]
    a, b, r = np.array(cap["a"]), np.array(cap["b"]), cap["radius"]
    devs, bands, clear, swing = [], [], [], []
    for k, j in enumerate(hair["joints"]):
        R = qmat(P[:, 1 + k, 3:])
        o = P[:, 1 + k, :3]
        tip = o + j["length"] * R[:, :, 0]
        rel = np.einsum("nji,nj->ni", HR, tip - Hp)
        if j["bone"] in onhead:
            devs.append(np.linalg.norm(rel - np.median(rel, 0), axis=1))
            v = np.diff(rel, axis=0) * 60
            f = np.fft.rfftfreq(len(v), 1 / 60)
            Pw = (np.abs(np.fft.rfft(v - v.mean(0), axis=0)) ** 2).sum(1) / len(v) ** 2
            bands.append(np.sqrt(2 * Pw[(f >= 3) & (f < 12)].sum()))
            # clearance of the joint's mid and end points from the head capsule (head frame)
            for s in (0.5, 1.0):
                pt = np.einsum("nji,nj->ni", HR, o + s * j["length"] * R[:, :, 0] - Hp)
                ab = b - a
                u = np.clip(((pt - a) @ ab) / (ab @ ab), 0, 1)
                clear.append(np.linalg.norm(pt - (a + u[:, None] * ab), axis=1) - r)
    return dict(
        dev50=100 * np.median(np.concatenate(devs)),
        dev95=100 * np.percentile(np.concatenate(devs), 95),
        band=float(np.mean(bands)),
        clear=100 * float(np.percentile(np.concatenate(clear), 0.5)),
        inside=100 * float(np.mean(np.concatenate(clear) < -0.005)),
    )


rows = []
print("%-34s | %-30s | %-30s" % ("profile (head chains)", "before: dev p50/p95 cm, 3-12Hz, clr", "after"))
band = json.load(open(str(REPO) + "/assets/libraries/gh3-band-v1/band/hair.json"))["members"]
for pid in ["band:" + r for r in ("guitarist", "bassist", "singer", "drummer")] + [
    k for k, p in lib["profiles"].items() if p["hair"]
]:
    hair = band[pid[5:]] if pid.startswith("band:") else lib["hair"][lib["profiles"][pid]["hair"]]
    b = analyse(f"{B}song-before-{pid}-60.bin", hair)
    a = analyse(f"{B}song-after-{pid}-60.bin", hair)
    rows.append(dict(profile=pid, before=b, after=a))
    f = lambda x: "%5.1f %5.1f %5.2f %5.1f %4.1f%%" % (x["dev50"], x["dev95"], x["band"], x["clear"], x["inside"])
    print("%-34s | %s | %s" % (pid.replace("gh3-guitarist-", "").replace("-v1", ""), f(b), f(a)))
json.dump(rows, open(WORK + "/families.json", "w"), indent=1)
