"""The files of a package (zip or unpacked folder) and the report of a check."""

import json
import re
import stat
import zipfile
from pathlib import Path, PurePosixPath

STORED = {".png", ".br", ".ogg", ".mp3", ".opus"}
DEFLATED = {".json", ".wav", ".glb", ".tex"}
WINDOWS_RESERVED = re.compile(r"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$", re.IGNORECASE)


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []

    def error(self, where, message):
        self.errors.append(f"{where}: {message}")

    def warn(self, where, message):
        self.warnings.append(f"{where}: {message}")

    @property
    def ok(self):
        return not self.errors


def path_problem(name):
    """Return why a package path is not portable, or None."""
    if not name or len(name.encode("utf-8")) > 240:
        return "path is empty or longer than 240 bytes"
    if "\\" in name or name.startswith("/") or "//" in name:
        return "path must be relative and use single '/' separators"
    for part in name.split("/"):
        if not re.fullmatch(r"[A-Za-z0-9_-][A-Za-z0-9._-]*", part):
            return f"invalid path segment '{part}'"
        if part.endswith(".") or WINDOWS_RESERVED.match(part):
            return f"path segment '{part}' is not valid on Windows"
    return None


class Source:
    """The files of a zip package or an unpacked folder."""

    def __init__(self, path, report):
        self.path = Path(path)
        self.zip = None
        self.names = []
        self.infos = {}
        if self.path.is_dir():
            for file in sorted(self.path.rglob("*")):
                relative = file.relative_to(self.path).as_posix()
                if file.is_symlink():
                    report.error(relative, "symbolic links are not allowed")
                    continue
                if file.is_file():
                    self.names.append(relative)
        else:
            try:
                self.zip = zipfile.ZipFile(self.path)
            except (zipfile.BadZipFile, OSError) as error:
                report.error(self.path.name, f"not a zip file: {error}")
                return
            for info in self.zip.infolist():
                if info.is_dir():
                    continue
                if info.flag_bits & 0x1:
                    report.error(info.filename, "encrypted entries are not allowed")
                if info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    report.error(info.filename, "compression must be STORE or DEFLATE")
                if stat.S_ISLNK(info.external_attr >> 16):
                    report.error(info.filename, "symbolic links are not allowed")
                if info.filename in self.infos:
                    report.error(info.filename, "duplicate zip entry")
                suffix = PurePosixPath(info.filename).suffix.lower()
                if suffix in STORED and info.compress_type != zipfile.ZIP_STORED and info.file_size > 0:
                    report.warn(info.filename, "use STORE for this file type")
                if suffix in DEFLATED and info.compress_type != zipfile.ZIP_DEFLATED and info.file_size > 1024:
                    report.warn(info.filename, "use DEFLATE for this file type")
                self.infos[info.filename] = info
                self.names.append(info.filename)
        folded = {}
        for name in self.names:
            problem = path_problem(name)
            if problem:
                report.error(name, problem)
            if name.lower() in folded:
                report.error(name, f"differs only in letter case from {folded[name.lower()]}")
            folded.setdefault(name.lower(), name)
        self.set = set(self.names)

    def read(self, name):
        if self.zip is not None:
            return self.zip.read(self.infos[name])
        return (self.path / name).read_bytes()

    def size(self, name):
        if self.zip is not None:
            return self.infos[name].file_size
        return (self.path / name).stat().st_size


class MappedSource(Source):
    """Package files given as a mapping of package path to file path or bytes (dry conversion checks)."""

    def __init__(self, label, files, report):
        self.path = Path(label)
        self.zip = None
        self.files = files
        self.names = sorted(files)
        self.infos = {}
        for name in self.names:
            problem = path_problem(name)
            if problem:
                report.error(name, problem)
        self.set = set(self.names)

    def read(self, name):
        value = self.files[name]
        return value if isinstance(value, bytes) else Path(value).read_bytes()

    def size(self, name):
        value = self.files[name]
        return len(value) if isinstance(value, bytes) else Path(value).stat().st_size


def load_json(source, name, report):
    def reject(value):
        raise ValueError(f"non-finite number {value}")

    try:
        return json.loads(source.read(name).decode("utf-8"), parse_constant=reject)
    except (UnicodeDecodeError, ValueError) as error:
        report.error(name, f"invalid JSON: {error}")
        return None
