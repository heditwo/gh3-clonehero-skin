"""No new unnamed numbers in the GH3 readers and the package format code (docs/CODE-STYLE.md, "Constants").

A number in a function body must be a named module constant, unless it is in ALLOWED or it is the base of int().
tests/fixtures/magic-numbers.json keeps the count of the numbers that are still unnamed in each file; a file may go
below its count, never above it. After you name numbers, write the new counts:

    python -m tests.test_magic_numbers --update
"""

import ast
import json
import sys
import unittest
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCOPE = ("pipeline/gh3", "pipeline/library", "pipeline/package")
BASELINE = ROOT / "tests" / "fixtures" / "magic-numbers.json"
ALLOWED = {-1, 0, 1, 2, 3, 4, 0.5}


def _is_constant_name(name):
    return name.lstrip("_").isupper()


def _named(node, parents):
    """True when the number is part of a module or class level UPPER_CASE constant, or the base argument of int()."""
    parent = parents.get(node)
    if isinstance(parent, ast.Call) and getattr(parent.func, "id", "") == "int" and node in parent.args[1:]:
        return True
    while parent is not None and not isinstance(parent, (ast.Assign, ast.AnnAssign, ast.FunctionDef, ast.Lambda)):
        parent = parents.get(parent)
    if not isinstance(parent, (ast.Assign, ast.AnnAssign)):
        return False
    if not isinstance(parents.get(parent), (ast.Module, ast.ClassDef)):
        return False
    targets = parent.targets if isinstance(parent, ast.Assign) else [parent.target]
    names = [n.id for t in targets for n in ast.walk(t) if isinstance(n, ast.Name)]
    return bool(names) and all(_is_constant_name(n) for n in names)


def unnamed_numbers(path):
    """(line, value) of each unnamed number of a Python file."""
    tree = ast.parse(path.read_text())
    parents = {child: node for node in ast.walk(tree) for child in ast.iter_child_nodes(node)}
    found = []
    for node in ast.walk(tree):
        if not isinstance(node, ast.Constant) or type(node.value) not in (int, float):
            continue
        value = -node.value if isinstance(parents.get(node), ast.UnaryOp) else node.value
        if value in ALLOWED or _named(node, parents):
            continue
        found.append((node.lineno, value))
    return sorted(found)


def counts():
    result = Counter()
    for folder in SCOPE:
        for path in sorted((ROOT / folder).rglob("*.py")):
            found = unnamed_numbers(path)
            if found:
                result[path.relative_to(ROOT).as_posix()] = len(found)
    return dict(sorted(result.items()))


class MagicNumberTests(unittest.TestCase):
    def test_no_new_unnamed_numbers(self):
        baseline = json.loads(BASELINE.read_text())
        problems = []
        for name, count in counts().items():
            allowed = baseline.get(name, 0)
            if count > allowed:
                lines = ", ".join(f"{line}: {value!r}" for line, value in unnamed_numbers(ROOT / name))
                problems.append(f"{name}: {count} unnamed numbers, {allowed} allowed (line: value) {lines}")
        self.assertEqual(problems, [], "name the new numbers as module constants (docs/CODE-STYLE.md)")


if __name__ == "__main__":
    if "--update" in sys.argv:
        BASELINE.write_text(json.dumps(counts(), indent=1) + "\n")
        print(f"wrote {BASELINE.relative_to(ROOT)}: {sum(counts().values())} unnamed numbers")
    else:
        unittest.main()
