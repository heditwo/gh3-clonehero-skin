"""Typed QB data and script references from tools/Gh3SongTool (the C# QB reader of the mod), for the script library."""

import json
import subprocess
import tempfile
from pathlib import Path

from pipeline import songtool


def definitions(files, command="qb-data"):
    """The JSON output of a Gh3SongTool command for these QB files."""
    executable = songtool.path()
    with tempfile.TemporaryDirectory(prefix="gh3-scripts-", dir="/tmp/opencode") as temporary:
        paths = []
        for index, data in enumerate(files):
            path = Path(temporary) / f"{index}.qb"
            path.write_bytes(data)
            paths.append(str(path))
        return json.loads(
            subprocess.run(
                ["dotnet", str(executable), command, *paths], check=True, capture_output=True, text=True
            ).stdout
        )


def tokens(qb, name):
    """The tokens of one script of a QB file: [{"op": hex opcode, "value": decoded QB value or None}]."""
    executable = songtool.path()
    with tempfile.TemporaryDirectory(prefix="gh3-scripts-", dir="/tmp/opencode") as temporary:
        path = Path(temporary) / "script.qb"
        path.write_bytes(qb)
        rows = json.loads(
            subprocess.run(
                ["dotnet", str(executable), "script-tokens", str(path), name],
                check=True,
                capture_output=True,
                text=True,
            ).stdout
        )
    return [{"op": row["op"], "value": None if row["value"] is None else json.loads(row["value"])} for row in rows]


def references(files):
    """{script checksum: [checksums it refers to]}."""
    return {
        int(name, 16): [int(reference, 16) for reference in items]
        for name, items in definitions(files, "qb-references").items()
    }


def dependencies(graph, roots):
    """All names reachable from the roots in a reference graph."""
    pending = list(roots)
    found = set()
    while pending:
        current = pending.pop()
        if current in found:
            continue
        found.add(current)
        pending.extend(graph.get(current, []))
    return found
