"""Markdown summary of the round-trip reports of blender/roundtrip.sh. Usage: python3 roundtrip_summary.py OUTPUT_DIR"""

import json
import sys
from pathlib import Path


def number(value):
    return "0" if value == 0 else f"{value:.2g}"


def row(report):
    keep = report["keep"]
    compare = keep.get("compare")
    if compare is None: return None
    stage = compare["stage"]
    validator = keep["validator"]
    json_files = compare["json"]
    sidecars = "equal" if all(r["equal"] for r in json_files.values()) else ", ".join(n for n, r in json_files.items() if not r["equal"])
    maximum = stage["max"]
    gh3 = max((v for k, v in maximum.items() if k.startswith("_GH3")), default=0)
    uv = max((v for k, v in maximum.items() if k.startswith("TEXCOORD")), default=0)
    triangles = "same" if not stage["meshes"]["triangleCountDifferent"] else f"{len(stage['meshes']['triangleCountDifferent'])} differ"
    return [Path(report["package"]).stem, report["blender"].split()[0], f"{report['openSeconds']} / {keep['seconds']}",
            f"{len(validator['errors'])} / {len(validator['warnings'])}", sidecars,
            f"{stage['nodes']['countA']} / {stage['nodes']['countB']}, extras {'equal' if not stage['nodes']['extrasDifferent'] else 'differ'}",
            f"{number(stage['nodeTransform']['position'])} m, {number(stage['nodeTransform']['rotation'])}°",
            f"{stage['materials']['countA']} / {stage['materials']['countB']}, {'equal' if not stage['materials']['different'] else 'differ'}",
            f"{stage['meshes']['primitives']}, {triangles}",
            f"{number(maximum.get('POSITION', 0))} / {number(maximum.get('NORMAL', 0))}° ({stage['meshes']['normalCornersOver1Degree'][0]} of "
            f"{stage['meshes']['normalCornersOver1Degree'][1]} corners > 1°) / {number(uv)} / {number(maximum.get('COLOR_0', 0))} / {number(gh3)}",
            f"{stage['images']['countA']} → {stage['images']['countB']} + {stage['images']['movedToFiles']} files",
            "pass" if not compare["failures"] else "; ".join(compare["failures"]), "yes" if report.get("reproducible") else "no",
            str(report.get("maxRssMB", ""))]


def main(output):
    reports = [json.loads(p.read_text()) for p in sorted(Path(output).glob("*/*.json")) if p.parent.name != "dist"]
    print("| Venue | Blender | Open / export (s) | Validator errors / warnings | Sidecars | GLB nodes | Node transform max | Materials | Primitives, triangles | Max POSITION (m) / NORMAL / TEXCOORD / COLOR_0 / _GH3_* | Images | Result | Same bytes on a second export | Max RSS (MB) |")
    print("|" + "---|" * 14)
    for report in reports:
        values = row(report)
        if values is None:
            print(f"| {Path(report['package']).stem} | {report['blender']} | not written: {report['keep'].get('issues')} |")
            continue
        print("| " + " | ".join(values) + " |")
    print()
    print("Export from the Blender data only (no kept source rows): differences by kind")
    print()
    for report in reports:
        compare = report.get("compute", {}).get("compare")
        if compare is None: continue
        print(f"- {Path(report['package']).stem} ({report['blender'].split()[0]}): {'; '.join(compare['failures']) or 'none'}")
        for name, result in compare["json"].items():
            for group, data in result["groups"].items():
                print(f"  - {name} {group}: {data['count']}, max {number(data['max'])}")
        extras = compare["stage"]["nodes"]["extrasDifferent"]
        if extras: print(f"  - stage.glb node extras differ: {len(extras)} nodes")
        for group, data in compare["stage"]["nodes"].get("extrasGroups", {}).items():
            print(f"    - {group}: {data['count']}, max {number(data['max'])}")
        for name, differences in list(compare["stage"]["materials"]["different"].items())[:3]:
            print(f"  - stage.glb material {name}: {differences[:2]}")


if __name__ == "__main__":
    main(sys.argv[1])
