"""Check helpers shared by the validator parts: JSON schema, rotations, PNG files."""

import math
import struct

from .texture import PNG_SIGNATURE

MAX_TEXTURE = 4096
HAIR_FRAME_FIELDS = ("twistAxis", "coneAxis", "planeAxis")
# Tolerances from docs/PACKAGING-PLAN.md section 9.6.
JOINT_POSITION = 1e-4
JOINT_ROTATION = 0.01
UNIT_SCALE = 1e-4
INVERSE_BIND = 1e-4
WEIGHT_SUM = 1e-3


def check_schema(schemas, value, schema, name, report):
    errors = schemas.validate(value, schema)
    for message in errors[:50]:
        report.error(name, message)
    if len(errors) > 50:
        report.error(name, f"{len(errors) - 50} more schema errors")
    return not errors


def unit_quaternions(value, name, report, location=""):
    """Check every 'rotation' quaternion in a JSON tree for unit length."""
    if isinstance(value, dict):
        for key, item in value.items():
            if (
                key in ("rotation", "gh3ObjectRotation")
                and isinstance(item, list)
                and len(item) == 4
                and all(isinstance(v, (int, float)) for v in item)
            ):
                if abs(math.sqrt(sum(v * v for v in item)) - 1) > 0.001:
                    report.error(name, f"{location}.{key}: rotation must have unit length")
            else:
                unit_quaternions(item, name, report, f"{location}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            unit_quaternions(item, name, report, f"{location}[{index}]")


def png_size(data: bytes) -> tuple[int, int] | None:
    """Width and height of a PNG file, or None when the data is not a PNG file."""
    if len(data) < 29 or data[:8] != PNG_SIGNATURE or data[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", data[16:24])


def png_problem(data: bytes) -> str | None:
    """Why a PNG file is not allowed in a package, or None."""
    size = png_size(data)
    if size is None:
        return "not a PNG file"
    depth, color, _, _, interlace = struct.unpack(">5B", data[24:29])
    if depth != 8 or color not in (0, 2, 3, 4, 6):
        return "PNG files must use 8 bits for each channel (or an 8-bit palette)"
    if interlace:
        return "PNG files must not be interlaced"
    if min(size) < 1 or max(size) > MAX_TEXTURE:
        return f"PNG size must be 1 to {MAX_TEXTURE} pixels"
    return None
