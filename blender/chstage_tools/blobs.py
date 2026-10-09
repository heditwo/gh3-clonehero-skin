"""Package files packed into the .blend file: the bytes are a custom property of a text block.

Export then does not need the opened package. Equal files share one text block (by SHA-256). Text blocks are never
exported by the glTF exporter, and the text tells a user what the block is.
"""

import hashlib

import bpy

DATA = "chstage_data"
SHA256 = "chstage_sha256"


def find(sha256):
    return next((text for text in bpy.data.texts if text.get(SHA256) == sha256 and DATA in text), None)


def store(name, data):
    """A text block that holds the bytes (an existing one with the same bytes, or a new one)."""
    sha256 = hashlib.sha256(data).hexdigest()
    text = find(sha256)
    if text is not None: return text
    text = bpy.data.texts.new(name)
    text.from_string(f"CH Stage Tools: packed package file '{name}' ({len(data)} bytes, SHA-256 {sha256}).\n"
                     "Export writes it into the package. Do not edit or delete this text block.\n")
    text[DATA] = bytes(data)
    text[SHA256] = sha256
    text.use_fake_user = True
    return text


def load(text):
    """The bytes of a packed text block, or None."""
    if text is None or DATA not in text: return None
    return bytes(text[DATA])


def sha256(text):
    return text.get(SHA256, "") if text is not None else ""
