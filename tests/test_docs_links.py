"""The Markdown files of the repository: relative links and anchors resolve, and Mermaid diagrams stay small.

The full Mermaid syntax check needs Node and the mermaid package; this test checks the diagram type and the size only."""

import re
import subprocess
import unicodedata
import unittest
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r"!?\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
FENCE = re.compile(r"^\s*(```|~~~)")
MERMAID_TYPES = ("flowchart ", "graph ", "sequenceDiagram", "stateDiagram", "classDiagram")
# The owner asked for simple diagrams; a diagram with more nodes than this should be split or cut.
MAX_MERMAID_NODES = 16
KEYWORDS = {"subgraph", "end", "direction", "style", "classDef", "class", "click", "linkStyle"}


def flowchart_nodes(lines):
    nodes = set()
    for line in lines:
        if line.split()[0] in KEYWORDS:
            continue
        line = re.sub(r"\"[^\"]*\"", "", line)
        line = re.sub(r"\[[^\]]*\]|\{[^}]*\}|\([^)]*\)|\|[^|]*\|", "", line)
        line = re.sub(r"--\s.*?-->|-\.\s.*?\.->|==\s.*?==>", "-->", line)
        nodes.update(re.findall(r"[A-Za-z_][\w]*", re.sub(r"[-.=]+>|---|&|:::\w+", " ", line)))
    return nodes


def markdown_files():
    names = (
        subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "*.md"],
            cwd=ROOT,
            capture_output=True,
            check=True,
        )
        .stdout.decode()
        .split("\0")
    )
    return [ROOT / name for name in names if name and (ROOT / name).is_file()]


def split_blocks(text):
    """Yields (line number, line, fence info or None) for the prose lines, and the fenced blocks as one item each."""
    prose, blocks, fence, info, body, start = [], [], None, "", [], 0
    for number, line in enumerate(text.splitlines(), 1):
        match = FENCE.match(line)
        if fence is None and match:
            fence, info, body, start = match.group(1), line.strip()[3:].strip(), [], number
        elif fence is not None and line.strip().startswith(fence):
            blocks.append((start, info, body))
            fence = None
        elif fence is not None:
            body.append(line)
        else:
            prose.append((number, line))
    return prose, blocks


def slug(heading):
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    text = text.replace("`", "").replace("*", "").strip().lower()
    text = "".join(c for c in text if c in " -_" or unicodedata.category(c)[0] in "LN")
    return text.replace(" ", "-")


def anchors(path, cache={}):
    if path not in cache:
        prose, _ = split_blocks(path.read_text(encoding="utf-8"))
        seen, result = {}, set()
        for _, line in prose:
            match = HEADING.match(line)
            if not match:
                continue
            base = slug(match.group(2))
            count = seen.get(base, 0)
            seen[base] = count + 1
            result.add(base if count == 0 else f"{base}-{count}")
        cache[path] = result
    return cache[path]


class MarkdownLinkTests(unittest.TestCase):
    def test_relative_links_and_anchors(self):
        problems = []
        for path in markdown_files():
            prose, _ = split_blocks(path.read_text(encoding="utf-8"))
            for number, line in prose:
                for target in LINK.findall(re.sub(r"`[^`]*`", "", line)):
                    if re.match(r"^[a-z][a-z0-9+.-]*:", target):
                        continue
                    file_part, _, anchor = target.partition("#")
                    destination = (path.parent / unquote(file_part)).resolve() if file_part else path
                    where = f"{path.relative_to(ROOT)}:{number}: {target}"
                    if not destination.exists():
                        problems.append(f"{where}: no such file")
                    elif anchor and destination.suffix == ".md" and anchor not in anchors(destination):
                        problems.append(f"{where}: no such heading")
        self.assertEqual(problems, [])

    def test_player_guide_links_stay_inside_the_release_zip(self):
        # scripts/build-release.sh puts PLAYER-GUIDE.md and some of its images into the zip, without the other docs.
        shipped = set(
            re.findall(r"docs/(images/[^\"\s]+)", (ROOT / "scripts" / "build-release.sh").read_text(encoding="utf-8"))
        )
        prose, _ = split_blocks((ROOT / "docs" / "PLAYER-GUIDE.md").read_text(encoding="utf-8"))
        outside = [
            target
            for _, line in prose
            for target in LINK.findall(re.sub(r"`[^`]*`", "", line))
            if not re.match(r"^[a-z][a-z0-9+.-]*:", target) and target.partition("#")[0] not in shipped | {""}
        ]
        self.assertEqual(outside, [])

    def test_mermaid_blocks_are_small(self):
        problems = []
        for path in markdown_files():
            _, blocks = split_blocks(path.read_text(encoding="utf-8"))
            for start, info, body in blocks:
                if info != "mermaid":
                    continue
                where = f"{path.relative_to(ROOT)}:{start}"
                lines = [line.strip() for line in body if line.strip() and not line.strip().startswith("%%")]
                if not lines or not lines[0].startswith(MERMAID_TYPES):
                    problems.append(f"{where}: unknown diagram type")
                    continue
                nodes = set() if not lines[0].startswith(("flowchart ", "graph ")) else flowchart_nodes(lines[1:])
                if len(nodes) > MAX_MERMAID_NODES:
                    problems.append(f"{where}: {len(nodes)} nodes (at most {MAX_MERMAID_NODES})")
        self.assertEqual(problems, [])


if __name__ == "__main__":
    unittest.main()
