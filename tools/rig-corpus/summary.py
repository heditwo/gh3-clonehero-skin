"""Merge the results of the one-model runs of tools/rig-corpus/run.sh into OUT/results.json and print the summary.
Usage: python3 summary.py OUT [--check]. Exit 1 with --check when a model without 'expect' does not map as expected."""

import json
import shutil
import sys
from pathlib import Path

out = Path(sys.argv[1])
check = "--check" in sys.argv[2:]
corpus = json.loads((Path(__file__).resolve().parent / "corpus.json").read_text())
rows = []
for model in corpus["models"]:
    folder = out / "models" / model["id"]
    found = folder / "results.json"
    if found.exists():
        rows += json.loads(found.read_text())
    else:
        rows.append({"id": model["id"], "failed": "Blender stopped (see corpus.log)"})
    for kind in ("renders", "packages"):
        for item in (folder / kind).glob("*") if (folder / kind).is_dir() else []:
            (out / kind).mkdir(exist_ok=True)
            shutil.copy2(item, out / kind / item.name)
(out / "results.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1))
expected = {m["id"]: m.get("expect") for m in corpus["models"]}
scored = [r for r in rows if "accuracy" in r and r.get("truthRoles")]
bad = [
    r["id"]
    for r in rows
    if not r.get("skipped")
    and expected.get(r["id"]) is None
    and (
        "failed" in r
        or r.get("fixes")
        or r.get("required")
        or r.get("errors")
        or any(c.startswith("ERROR") for c in r.get("checks", []))
    )
]
mean = sum(r["accuracy"] for r in scored) / len(scored) if scored else 0
print(
    f"CORPUS SUMMARY {len(scored)} models scored, mean accuracy {mean:.3f}, zero fixes {sum(1 for r in scored if r['fixes'] == 0)}, "
    f"failed {sum(1 for r in rows if 'failed' in r)}, skipped {sum(1 for r in rows if r.get('skipped'))}"
    + (f", not as expected: {', '.join(bad)}" if bad else "")
)
sys.exit(1 if check and bad else 0)
