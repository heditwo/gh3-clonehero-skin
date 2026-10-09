"""Convert typed QB data into the public script library format."""

import json
import subprocess
import tempfile
from pathlib import Path

from pipeline import songtool


def definitions(files, command="qb-data"):
    executable = songtool.path()
    with tempfile.TemporaryDirectory(prefix="gh3-scripts-", dir="/tmp/opencode") as temporary:
        paths = []
        for index, data in enumerate(files):
            path = Path(temporary)/f"{index}.qb"; path.write_bytes(data); paths.append(str(path))
        return json.loads(subprocess.run(["dotnet", str(executable), command, *paths], check=True, capture_output=True, text=True).stdout)


def references(files):
    return {int(name, 16): [int(reference, 16) for reference in items] for name, items in definitions(files, "qb-references").items()}


def dependencies(graph, roots):
    pending = list(roots); found = set()
    while pending:
        current = pending.pop()
        if current in found: continue
        found.add(current)
        pending.extend(graph.get(current, []))
    return found
