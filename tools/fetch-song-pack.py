"""Download the chart files (notes.mid, notes.chart, song.ini) of a public Google Drive chart pack for pipeline.song_direction.

The pack file (pipeline/song_packs/<pack>.json) gives the Drive folder. The tool lists each song folder, downloads only the
chart files (no audio, no images), and writes listing.json: every file of every folder with its Drive ID and date.
Keep the output outside the repository (for example ~/.cache/gh3-song-pack/<pack>); the charts are not ours to commit.
"""

import argparse
import hashlib
import html
import json
import re
import time
import urllib.request
from pathlib import Path

FOLDER_VIEW = "https://drive.google.com/embeddedfolderview?id={}"
DOWNLOAD = "https://drive.google.com/uc?export=download&id={}"
CHART_FILES = re.compile(r"^(notes\.(mid|chart)|song\.ini)$", re.IGNORECASE)
ENTRY = re.compile(r'<a href="([^"]+)"[^>]*>.*?<div class="flip-entry-title">([^<]*)</div>', re.DOTALL)
MODIFIED = re.compile(r"flip-entry-last-modified[^>]*><div>([^<]*)")
DRIVE_ID = re.compile(r"(?:/d/|folders/)([A-Za-z0-9_-]+)")
ATTEMPTS = 5
RETRY_SECONDS = 5
TIMEOUT_SECONDS = 60


def _get(url):
    for attempt in range(ATTEMPTS):
        try:
            with urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:
                return response.read()
        except OSError:
            if attempt == ATTEMPTS - 1:
                raise
            time.sleep(RETRY_SECONDS * (attempt + 1))
    raise AssertionError("unreachable")


def folder_listing(folder_id):
    """[(name, Drive ID, is folder, modified date text)] of a public Drive folder."""
    page = _get(FOLDER_VIEW.format(folder_id)).decode()
    entries = ENTRY.findall(page)
    dates = MODIFIED.findall(page)
    if len(entries) != len(dates):
        raise ValueError(f"unexpected Drive folder page for {folder_id}")
    return [
        (html.unescape(name), DRIVE_ID.search(url).group(1), "/folders/" in url, date)
        for (url, name), date in zip(entries, dates, strict=True)
    ]


def fetch(pack, output):
    """Download the chart files of every song folder of the pack and write listing.json."""
    listing = {"driveFolderId": pack["source"]["driveFolderId"], "folders": {}}
    for name, folder_id, is_folder, date in folder_listing(pack["source"]["driveFolderId"]):
        if not is_folder:
            continue
        files = folder_listing(folder_id)
        folder = output / name
        folder.mkdir(parents=True, exist_ok=True)
        rows = []
        for file_name, file_id, nested, modified in files:
            row = {"name": file_name, "id": file_id, "modified": modified, "folder": nested}
            if not nested and CHART_FILES.match(file_name):
                path = folder / file_name
                if not path.exists():
                    path.write_bytes(_get(DOWNLOAD.format(file_id)))
                row["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
            rows.append(row)
        listing["folders"][name] = {"id": folder_id, "modified": date, "files": rows}
        print(f"{name}: {len(rows)} files")
    (output / "listing.json").write_text(json.dumps(listing, indent=1, ensure_ascii=False) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--pack", required=True, type=Path, help="pipeline/song_packs/<pack>.json")
    parser.add_argument(
        "--output", required=True, type=Path, help="Folder for the chart files (outside the repository)"
    )
    args = parser.parse_args()
    fetch(json.loads(args.pack.read_text()), args.output)


if __name__ == "__main__":
    main()
