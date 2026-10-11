"""Build the compact pronunciation dictionary of the lip sync (src/Resources/cmudict.br) from the CMU Pronouncing Dictionary.

Usage: uv run --with brotli python tools/lipsync-dictionary.py CMUDICT [--output src/Resources/cmudict.br]

CMUDICT is `cmudict.dict` of https://github.com/cmusphinx/cmudict (BSD-style license, src/Resources/cmudict-LICENSE).
The output is one Brotli stream of UTF-8 text: one line "word<TAB>codes" for each word, sorted by the bytes of the word.
Only the first pronunciation of a word is kept, without stress. Each phoneme is one letter: the index in PHONEMES
(LyricPhonemes.Phonemes in the mod) as 'A'-'Z' then 'a'-'m'.
"""

import argparse
import re
from pathlib import Path

import brotli

PHONEMES = (
    "AA AE AH AO AW AY B CH D DH EH ER EY F G HH IH IY JH K L M N NG OW OY P R S SH T TH UH UW V W Y Z ZH".split()
)
LETTERS = 26
BROTLI_QUALITY = 11
BROTLI_WINDOW = 24


def code(index):
    return chr(ord("A") + index) if index < LETTERS else chr(ord("a") + index - LETTERS)


def build(source):
    """Return the sorted lines "word<TAB>codes" of the first pronunciation of each word."""
    codes = {p: code(i) for i, p in enumerate(PHONEMES)}
    lines = []
    for raw in source.read_text(encoding="utf-8").splitlines():
        text = raw.split("#")[0].strip()
        if not text:
            continue
        word, *phonemes = text.split()
        if "(" in word or not word.isascii():
            continue
        lines.append(word.lower() + "\t" + "".join(codes[re.sub(r"\d", "", p)] for p in phonemes))
    return sorted(lines, key=lambda line: line.split("\t")[0].encode())


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path, default=Path("src/Resources/cmudict.br"))
    args = parser.parse_args()
    lines = build(args.source)
    data = brotli.compress("\n".join(lines).encode(), quality=BROTLI_QUALITY, lgwin=BROTLI_WINDOW)
    args.output.write_bytes(data)
    print(f"{len(lines)} words, {len(data)} bytes")


if __name__ == "__main__":
    main()
