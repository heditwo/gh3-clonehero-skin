"""Compare the C# importer cache with the runtime files of the Python pipeline (the same rules as roundtrip.py).

The mod's importer (src/Packages, run outside the game by tools/PackageImport) writes one cache folder for each package.
This check compares each cache folder with the files that the Python compiler writes from the same package:
`assets/stages/<id>` for venues (roundtrip.py shows that these are equal to `compile_stage` of the package) and
`assets/libraries/gh3-band-v1` with `assets/stages/events` for the library.

- JSON: equal, numbers within 1e-6 (relative) or 1e-9. pack.json `sources`, `files`, and `package` are not compared.
- meshes.bin, animation.bin: byte-equal, or the largest float difference (venue limit 1e-5 m; library UV 6e-8).
- Textures: the cache `.tex` (GH3TEX02: RGBA32, BC1, or BC3 with mips) is decoded and compared with the PNG: exact for RGBA32, the
  error is reported for BC1 and BC3 (maximum and PSNR of mip 0).
- Audio: `<sample>.pcm` against the decompressed `<sample>.pcm.gz`. Clips: `.q48.br` against the package bytes.

Usage: python -m pipeline.package.cachecheck CACHE [--stages DIR] [--library DIR] [--events DIR] [--packages DIR] [--json OUT]
"""

import argparse
import gzip
import json
import math
import struct
import zipfile
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image

from pipeline.package import runtime, texture
from pipeline.package.build import EVENTS, OUTPUT, json_bytes
from pipeline.package.roundtrip import UNREAD_MATERIAL_FIELDS
from pipeline.paths import LIBRARY, STAGES
from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX, STATIC_VERTEX

PACK_IGNORED = ("sources", "files", "package")
DXT1, DXT5, RGBA32 = 10, 12, 4


def close(a, b, path="", out=None):
    """Differences between two JSON values with a float tolerance."""
    out = [] if out is None else out
    if isinstance(a, dict) and isinstance(b, dict):
        for key in sorted(set(a) | set(b)):
            if key not in a or key not in b: out.append(f"{path}/{key}: only in {'cache' if key in a else 'python'}")
            else: close(a[key], b[key], f"{path}/{key}", out)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b): out.append(f"{path}: length {len(a)} != {len(b)}")
        else:
            for i, (x, y) in enumerate(zip(a, b)): close(x, y, f"{path}[{i}]", out)
    elif isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
        if not math.isclose(a, b, rel_tol=1e-6, abs_tol=1e-9): out.append(f"{path}: {a} != {b}")
    elif a != b: out.append(f"{path}: {a!r} != {b!r}")
    return out


def read_tex(data):
    """Return (format, mip 0 RGBA array top row first, mip count, header and size ok)."""
    try:
        fmt, _, _, mips, _ = texture.read_header(data)
    except texture.TextureError:
        return None, None, 0, False
    return fmt, texture.decode(data), mips, True


def texture_difference(tex, png):
    fmt, pixels, mips, sizes = read_tex(tex)
    if not sizes: return {"format": fmt, "shape": False}
    expected = np.asarray(Image.open(BytesIO(png)).convert("RGBA"))
    if pixels.shape != expected.shape or not sizes: return {"format": fmt, "shape": False}
    if fmt == DXT1: pixels = pixels.copy(); pixels[..., 3] = np.where(pixels[..., 3] == 0, 0, 255)
    difference = np.abs(pixels.astype(np.int32) - expected.astype(np.int32))
    mse = float((difference[..., :3].astype(np.float64) ** 2).mean())
    return {"format": fmt, "max": int(difference.max()), "maxAlpha": int(difference[..., 3].max()),
            "psnr": 99.0 if mse == 0 else 10 * math.log10(255 ** 2 / mse), "opaqueSource": bool((expected[..., 3] == 255).all())}


def float_difference(actual, expected, dtype, magic):
    a, e = runtime.read_meshes(actual, magic, dtype), runtime.read_meshes(expected, magic, dtype)
    if len(a) != len(e) or any(x[:2] != y[:2] or not np.array_equal(x[3], y[3]) for x, y in zip(a, e)): return {"layout": "different"}
    result = {}
    for x, y in zip(a, e):
        left, right = np.frombuffer(x[2], dtype), np.frombuffer(y[2], dtype)
        for field in dtype.names:
            if (left[field] != right[field]).any():
                result[field] = max(result.get(field, 0.0), float(np.abs(left[field].astype(np.float64) - right[field]).max()))
    return result


def animation_difference(actual, expected):
    pa, ba, ca = runtime.read_animation(actual)
    pe, be, ce = runtime.read_animation(expected)
    if pa != pe or len(ca) != len(ce) or any(x[1].shape != y[1].shape for x, y in zip(ca, ce)): return {"layout": "different"}
    values = [np.abs(ba - be).max()] + [np.abs(x[1] - y[1]).max() for x, y in zip(ca, ce)] + [abs(x[0] - y[0]) for x, y in zip(ca, ce)]
    return {"max": float(max(values))}


def compare_folder(cache, expected, mesh_limit, library=False):
    """Compare one compiled stage or library model folder; return a report with the textures separate."""
    report = {"byteEqual": 0, "jsonEqual": 0, "close": 0, "different": {}, "missing": [], "extra": [], "textures": {}}
    want = {p.relative_to(expected).as_posix() for p in Path(expected).rglob("*") if p.is_file()}
    have = {p.relative_to(cache).as_posix() for p in Path(cache).rglob("*") if p.is_file()}
    for name in sorted(want):
        target = name[:-4] + ".tex" if name.endswith(".png") and "/textures/" in "/" + name else name
        if target not in have:
            if not (library and target.endswith(".tex")): report["missing"].append(name)
            continue
        have.discard(target)
        left, right = (Path(cache)/target).read_bytes(), (Path(expected)/name).read_bytes()
        if target.endswith(".tex"):
            report["textures"][name] = texture_difference(left, right); continue
        if left == right: report["byteEqual"] += 1; continue
        if name.endswith(".json"):
            a, b = json.loads(left), json.loads(right)
            if Path(name).name == "pack.json":
                for key in PACK_IGNORED: a.pop(key, None); b.pop(key, None)
            if library and Path(name).name == "stage.json":
                for material in b.get("materials", {}).values():
                    for field in UNREAD_MATERIAL_FIELDS: material.pop(field, None)
                    if material.get("color") == [1, 1, 1, 1]: del material["color"]
            problems = close(a, b)
            if problems: report["different"][name] = problems[:5]
            else: report["jsonEqual"] += 1
            continue
        if Path(name).name == "meshes.bin":
            magic = left[:8]
            difference = float_difference(left, right, STATIC_VERTEX if magic == b"GH3STG04" else SKINNED_GH3_VERTEX, magic)
            if "layout" in difference or any(value > mesh_limit.get(field, mesh_limit["*"]) for field, value in difference.items()):
                report["different"][name] = difference
            else: report["close"] += 1; report.setdefault("meshDifference", {})[name] = difference
            continue
        if Path(name).name == "animation.bin":
            difference = animation_difference(left, right)
            if "layout" in difference or difference["max"] > 1e-5: report["different"][name] = difference
            else: report["close"] += 1; report.setdefault("animationDifference", {})[name] = difference
            continue
        report["different"][name] = "bytes"
    # The importer copies the manifest preview image for the in-game picker; the Python compiler has no such file.
    report["extra"] = sorted(n for n in have if n not in ("cache.json", "preview.png"))
    return report


def texture_summary(rows):
    rows = list(rows)
    if not rows: return {"count": 0}
    formats = {}
    for row in rows: formats[row["format"]] = formats.get(row["format"], 0) + 1
    finite = [r["psnr"] for r in rows if "psnr" in r]
    return {"count": len(rows), "formats": {{RGBA32: "RGBA32", DXT1: "BC1", DXT5: "BC3"}[k]: v for k, v in formats.items()},
            "shapeErrors": sum(1 for r in rows if r.get("shape") is False),
            "rgbaExact": all(r.get("max") == 0 for r in rows if r["format"] == RGBA32),
            "maxError": max(r.get("max", 0) for r in rows), "maxAlphaError": max(r.get("maxAlpha", 0) for r in rows),
            "minPsnr": min(finite) if finite else None, "medianPsnr": float(np.median(finite)) if finite else None,
            "bc1WithAlphaSource": sum(1 for r in rows if r["format"] == DXT1 and not r.get("opaqueSource", True))}


def venue_check(cache, expected):
    report = compare_folder(cache, expected, {"*": 1e-5, "color": 0})
    report["textureSummary"] = texture_summary(report.pop("textures").values())
    report["ok"] = not (report["different"] or report["missing"] or report["extra"]) and report["textureSummary"].get("shapeErrors", 0) == 0
    return report


def library_check(cache, library, events, package):
    cache, library = Path(cache), Path(library)
    report = {"models": {}, "files": {}, "audio": {}, "clips": {}}
    textures = []
    for folder in sorted({p.parent for p in library.rglob("meshes.bin")}):
        name = folder.relative_to(library).as_posix()
        row = compare_folder(cache/name, folder, {"*": 0, "uv": 6e-8}, library=True)
        textures.extend(row["textures"].values())
        row["textureSummary"] = texture_summary(row.pop("textures").values())
        row["ok"] = not (row["different"] or row["missing"]) and row["textureSummary"].get("shapeErrors", 0) == 0
        report["models"][name] = row
    for name in ("catalog.json", "band/band.json", "band/hair.json", "direction/performance.json", "crowd/crowd.json", "hand-fire/hand-fire.json",
                 "render/particle-materials.json", "render/tesla-material.json", "render/tesla-noise.png"):
        report["files"][name] = (cache/name).read_bytes() == (library/name).read_bytes()
    report["files"]["scripts.json"] = not close(json.loads((cache/"scripts.json").read_text()), json.loads((library/"scripts.json").read_text()))
    report["files"]["audio/audio.json"] = not close(json.loads((cache/"audio/audio.json").read_text()), json.loads((library/"audio/audio.json").read_text()))
    pngs = sorted(p.relative_to(library).as_posix() for d in ("particle-textures", "hand-fire") for p in (library/d).glob("*.png"))
    report["files"]["particle and hand-fire textures"] = all((cache/n).read_bytes() == (library/n).read_bytes() for n in pngs)
    movies = sorted(library.glob("movies/*.gh3video"))
    report["files"]["movies"] = all((cache/m.relative_to(library)).read_bytes() == m.read_bytes() for m in movies) and len(movies) == 4
    samples = sorted(library.glob("audio/*.pcm.gz"))
    report["audio"] = {"samples": len(samples), "pcmEqual": sum((cache/"audio"/(s.name[:-3])).read_bytes() == gzip.decompress(s.read_bytes()) for s in samples)}
    songs = sorted(p for p in Path(events).glob("*.json") if p.name != "export-report.json")
    report["files"]["songs"] = all((cache/"songs"/p.name).read_bytes() == p.read_bytes() for p in songs)
    clips = sorted(p.relative_to(cache).as_posix() for p in cache.rglob("*.q48.br"))
    with zipfile.ZipFile(package) as archive:
        report["clips"] = {"count": len(clips), "packageEqual": sum(archive.read(name) == (cache/name).read_bytes() for name in clips),
                           "currentClipFiles": len(list(library.rglob("*.bin.gz")))}
    report["textureSummary"] = texture_summary(textures)
    report["ok"] = (all(m["ok"] for m in report["models"].values()) and all(report["files"].values()) and
                    report["audio"]["pcmEqual"] == report["audio"]["samples"] and report["clips"]["packageEqual"] == report["clips"]["count"] == report["clips"]["currentClipFiles"])
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.cachecheck", description=__doc__.splitlines()[0])
    parser.add_argument("cache", type=Path)
    parser.add_argument("--stages", type=Path, default=STAGES)
    parser.add_argument("--library", type=Path, default=LIBRARY)
    parser.add_argument("--events", type=Path, default=EVENTS)
    parser.add_argument("--packages", type=Path, default=OUTPUT)
    parser.add_argument("--json", type=Path)
    args = parser.parse_args(argv)
    report = {}
    for folder in sorted(p for p in args.cache.iterdir() if (p/"cache.json").is_file()):
        info = json.loads((folder/"cache.json").read_text())
        identifier = info["package"]["id"]
        if info["package"]["format"].startswith("chchar"): continue
        if info["package"]["format"].startswith("chlib"):
            report[identifier] = row = library_check(folder, args.library, args.events, args.packages/f"{identifier}.chlib")
            summary = {k: row[k] for k in ("files", "audio", "clips")}
            summary["models"] = {k: v["ok"] for k, v in row["models"].items()}
            print(f"{identifier}: {'equal' if row['ok'] else 'DIFFERENT'} {json.dumps(summary)}; textures {json.dumps(row['textureSummary'])}")
            for name, model in row["models"].items():
                if not model["ok"]: print(f"  {name}: {json.dumps({k: model[k] for k in ('different', 'missing')})[:2000]}")
        else:
            report[identifier] = row = venue_check(folder, args.stages/identifier)
            print(f"{identifier}: {'equal' if row['ok'] else 'DIFFERENT'} ({row['byteEqual']} byte-equal, {row['jsonEqual']} JSON-equal, {row['close']} within tolerance; "
                  f"different {json.dumps(row['different'])[:3000]}, missing {row['missing'][:10]}, extra {row['extra'][:10]}); textures {json.dumps(row['textureSummary'])}")
    if args.json: args.json.write_bytes(json_bytes(report))
    return 0 if report and all(row["ok"] for row in report.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
