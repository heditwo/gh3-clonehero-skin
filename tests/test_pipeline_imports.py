"""The tools, tests, and research scripts import only names that the pipeline modules define (no import runs).

A rename in pipeline/ breaks scripts that the normal tests do not run (tools/research, blender/tests); this test
finds them.
"""

import ast
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FOLDERS = ("tools", "tests", "blender/tests", "examples", "pipeline")


def module_file(module):
    path = ROOT / Path(*module.split("."))
    if (path / "__init__.py").is_file():
        return path / "__init__.py"
    return path.with_suffix(".py") if path.with_suffix(".py").is_file() else None


def defined_names(path):
    names = set()
    for node in ast.parse(path.read_text()).body:
        if isinstance(node, (ast.FunctionDef, ast.ClassDef, ast.AsyncFunctionDef)):
            names.add(node.name)
        elif isinstance(node, (ast.Assign, ast.AnnAssign)):
            targets = node.targets if isinstance(node, ast.Assign) else [node.target]
            names.update(n.id for t in targets for n in ast.walk(t) if isinstance(n, ast.Name))
        elif isinstance(node, (ast.Import, ast.ImportFrom)):
            names.update((alias.asname or alias.name).split(".")[0] for alias in node.names)
    return names


class PipelineImportTests(unittest.TestCase):
    def test_imported_names_exist(self):
        problems = []
        for folder in FOLDERS:
            for path in sorted((ROOT / folder).rglob("*.py")):
                if "vendor" in path.parts:
                    continue
                for node in ast.walk(ast.parse(path.read_text())):
                    if (
                        not isinstance(node, ast.ImportFrom)
                        or node.level
                        or not (node.module or "").startswith("pipeline")
                    ):
                        continue
                    target = module_file(node.module)
                    where = f"{path.relative_to(ROOT)}:{node.lineno}"
                    if target is None:
                        problems.append(f"{where}: no module {node.module}")
                        continue
                    names = defined_names(target)
                    for alias in node.names:
                        if alias.name in names or alias.name == "*" or module_file(f"{node.module}.{alias.name}"):
                            continue
                        problems.append(f"{where}: {node.module} has no {alias.name}")
        self.assertEqual(problems, [])


if __name__ == "__main__":
    unittest.main()
