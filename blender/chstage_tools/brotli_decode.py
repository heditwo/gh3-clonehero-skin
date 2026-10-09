"""Brotli decoder in pure Python (RFC 7932), for Blender builds without the brotli module.

Package clips are Brotli streams (docs/PACKAGE-FORMAT.md section 11). The add-on uses the brotli module when Blender
has it and this decoder when it does not (the official Blender builds do not ship brotli). No bpy; the tests compare
it with the brotli module. The tables are from RFC 7932 (section 7.1, appendices A and B, checked by their CRC-32
values); the static dictionary is brotli_dictionary.bin (zlib).
"""

import zlib
from pathlib import Path

DICTIONARY_CRC = 0x5136CB04
DICTIONARY_SIZE = 122784
CODE_LENGTH_ORDER = (1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15)
CODE_LENGTH_PREFIX_LENGTH = (2, 2, 2, 3, 2, 2, 2, 4, 2, 2, 2, 3, 2, 2, 2, 4)
CODE_LENGTH_PREFIX_VALUE = (0, 4, 3, 2, 0, 4, 3, 1, 0, 4, 3, 2, 0, 4, 3, 5)
BLOCK_COUNT_BASE = (1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305, 369, 497, 753, 1265, 2289, 4337, 8433, 16625)
BLOCK_COUNT_EXTRA = (2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24)
INSERT_BASE = (0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594)
INSERT_EXTRA = (0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24)
COPY_BASE = (2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118)
COPY_EXTRA = (0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24)
# Insert-and-copy cells (RFC 7932 section 5): (insert code base, copy code base) for symbol >> 6.
CELLS = ((0, 0), (0, 8), (0, 0), (0, 8), (8, 0), (8, 8), (0, 16), (16, 0), (8, 16), (16, 8), (16, 16))
DICTIONARY_BITS = (0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5)
DISTANCE_SHORT_INDEX = (0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1)
DISTANCE_SHORT_OFFSET = (0, 0, 0, 0, -1, 1, -2, 2, -3, 3, -1, 1, -2, 2, -3, 3)
LUT0 = (0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 12, 16, 12, 12, 20, 12, 16, 24, 28, 12, 12, 32, 12, 36, 12, 44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 32, 32, 24, 40, 28, 12, 12, 48, 52, 52, 52, 48, 52, 52, 52, 48, 52, 52, 52, 52, 52, 48, 52, 52, 52, 52, 52, 48, 52, 52, 52, 52, 52, 24, 12, 28, 12, 12, 12, 56, 60, 60, 60, 56, 60, 60, 60, 56, 60, 60, 60, 60, 60, 56, 60, 60, 60, 60, 60, 56, 60, 60, 60, 60, 60, 24, 12, 28, 12, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3)
LUT1 = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2)
LUT2 = (0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7)
TRANSFORMS = ((b'', 0, b''), (b'', 0, b' '), (b' ', 0, b' '), (b'', 3, b''), (b'', 1, b' '), (b'', 0, b' the '), (b' ', 0, b''), (b's ', 0, b' '), (b'', 0, b' of '), (b'', 1, b''), (b'', 0, b' and '), (b'', 4, b''), (b'', 12, b''), (b', ', 0, b' '), (b'', 0, b', '), (b' ', 1, b' '), (b'', 0, b' in '), (b'', 0, b' to '), (b'e ', 0, b' '), (b'', 0, b'"'), (b'', 0, b'.'), (b'', 0, b'">'), (b'', 0, b'\n'), (b'', 14, b''), (b'', 0, b']'), (b'', 0, b' for '), (b'', 5, b''), (b'', 13, b''), (b'', 0, b' a '), (b'', 0, b' that '), (b' ', 1, b''), (b'', 0, b'. '), (b'.', 0, b''), (b' ', 0, b', '), (b'', 6, b''), (b'', 0, b' with '), (b'', 0, b"'"), (b'', 0, b' from '), (b'', 0, b' by '), (b'', 7, b''), (b'', 8, b''), (b' the ', 0, b''), (b'', 15, b''), (b'', 0, b'. The '), (b'', 2, b''), (b'', 0, b' on '), (b'', 0, b' as '), (b'', 0, b' is '), (b'', 18, b''), (b'', 12, b'ing '), (b'', 0, b'\n\t'), (b'', 0, b':'), (b' ', 0, b'. '), (b'', 0, b'ed '), (b'', 11, b''), (b'', 9, b''), (b'', 17, b''), (b'', 0, b'('), (b'', 1, b', '), (b'', 19, b''), (b'', 0, b' at '), (b'', 0, b'ly '), (b' the ', 0, b' of '), (b'', 16, b''), (b'', 20, b''), (b' ', 1, b', '), (b'', 1, b'"'), (b'.', 0, b'('), (b'', 2, b' '), (b'', 1, b'">'), (b'', 0, b'="'), (b' ', 0, b'.'), (b'.com/', 0, b''), (b' the ', 0, b' of the '), (b'', 1, b"'"), (b'', 0, b'. This '), (b'', 0, b','), (b'.', 0, b' '), (b'', 1, b'('), (b'', 1, b'.'), (b'', 0, b' not '), (b' ', 0, b'="'), (b'', 0, b'er '), (b' ', 2, b' '), (b'', 0, b'al '), (b' ', 2, b''), (b'', 0, b"='"), (b'', 2, b'"'), (b'', 1, b'. '), (b' ', 0, b'('), (b'', 0, b'ful '), (b' ', 1, b'. '), (b'', 0, b'ive '), (b'', 0, b'less '), (b'', 2, b"'"), (b'', 0, b'est '), (b' ', 1, b'.'), (b'', 2, b'">'), (b' ', 0, b"='"), (b'', 1, b','), (b'', 0, b'ize '), (b'', 2, b'.'), (b'\xc2\xa0', 0, b''), (b' ', 0, b','), (b'', 1, b'="'), (b'', 2, b'="'), (b'', 0, b'ous '), (b'', 2, b', '), (b'', 1, b"='"), (b' ', 1, b','), (b' ', 2, b'="'), (b' ', 2, b', '), (b'', 2, b','), (b'', 2, b'('), (b'', 2, b'. '), (b' ', 2, b'.'), (b'', 2, b"='"), (b' ', 2, b'. '), (b' ', 1, b'="'), (b' ', 2, b"='"), (b' ', 1, b"='"))


_dictionary = None


def dictionary():
    global _dictionary
    if _dictionary is None:
        data = zlib.decompress((Path(__file__).with_name("brotli_dictionary.bin")).read_bytes())
        if len(data) != DICTIONARY_SIZE or zlib.crc32(data) != DICTIONARY_CRC: raise ValueError("the Brotli dictionary file is damaged")
        _dictionary = data
    return _dictionary


DICTIONARY_OFFSET = [0] * 25
for _length in range(4, 24): DICTIONARY_OFFSET[_length + 1] = DICTIONARY_OFFSET[_length] + _length * (1 << DICTIONARY_BITS[_length])


class BrotliError(ValueError):
    pass


class BitReader:
    def __init__(self, data):
        self.data = data
        self.position = 0
        self.value = 0
        self.count = 0

    def fill(self, need):
        while self.count < need:
            if self.position < len(self.data):
                self.value |= self.data[self.position] << self.count
                self.position += 1
            elif self.position < len(self.data) + 8:
                self.position += 1
            else:
                raise BrotliError("unexpected end of the stream")
            self.count += 8

    def bits(self, n):
        if n == 0: return 0
        if self.count < n: self.fill(n)
        value = self.value & ((1 << n) - 1)
        self.value >>= n
        self.count -= n
        return value

    def peek(self, n):
        if self.count < n: self.fill(n)
        return self.value & ((1 << n) - 1)

    def skip(self, n):
        self.value >>= n
        self.count -= n

    def align(self):
        """Go to the next byte boundary; the skipped bits must be zero."""
        if self.bits(self.count & 7): raise BrotliError("padding bits are not zero")

    def byte(self):
        return self.bits(8)


class Prefix:
    """A prefix code as a lookup table indexed by the next bits (LSB first)."""

    def __init__(self, lengths):
        symbols = [(length, symbol) for symbol, length in enumerate(lengths) if length]
        if len(symbols) == 1:
            self.bits, self.table = 0, [(symbols[0][1], 0)]
            return
        if not symbols: raise BrotliError("empty prefix code")
        self.bits = max(length for length, _ in symbols)
        size = 1 << self.bits
        table = [None] * size
        code = 0
        previous = 0
        for length, symbol in sorted(symbols):
            code <<= length - previous
            previous = length
            reverse = int(format(code, f"0{length}b")[::-1], 2)
            for index in range(reverse, size, 1 << length): table[index] = (symbol, length)
            code += 1
        if code != 1 << previous or None in table: raise BrotliError("incomplete prefix code")
        self.table = table

    def read(self, reader):
        if not self.bits: return self.table[0][0]
        if reader.count < self.bits: reader.fill(self.bits)
        symbol, length = self.table[reader.value & ((1 << self.bits) - 1)]
        reader.value >>= length
        reader.count -= length
        return symbol


def read_prefix(reader, alphabet):
    width = max(1, (alphabet - 1).bit_length())
    skip = reader.bits(2)
    lengths = [0] * alphabet
    if skip == 1:
        count = reader.bits(2) + 1
        symbols = [reader.bits(width) for _ in range(count)]
        if any(s >= alphabet for s in symbols) or len(set(symbols)) != count: raise BrotliError("invalid simple prefix code")
        if count == 1:
            single = Prefix.__new__(Prefix)
            single.bits, single.table = 0, [(symbols[0], 0)]
            return single
        if count == 2: lengths[symbols[0]] = lengths[symbols[1]] = 1
        elif count == 3: lengths[symbols[0]], lengths[symbols[1]], lengths[symbols[2]] = 1, 2, 2
        elif reader.bits(1): lengths[symbols[0]], lengths[symbols[1]], lengths[symbols[2]], lengths[symbols[3]] = 1, 2, 3, 3
        else:
            for s in symbols: lengths[s] = 2
        return Prefix(lengths)
    code_lengths = [0] * 18
    space, codes = 32, 0
    for index in range(skip, 18):
        peeked = reader.peek(4)
        reader.skip(CODE_LENGTH_PREFIX_LENGTH[peeked])
        value = CODE_LENGTH_PREFIX_VALUE[peeked]
        code_lengths[CODE_LENGTH_ORDER[index]] = value
        if value:
            space -= 32 >> value
            codes += 1
            if space <= 0: break
    if not (codes == 1 or space == 0): raise BrotliError("invalid code length code")
    length_code = Prefix(code_lengths)
    symbol, previous, repeat, repeat_length, space = 0, 8, 0, 0, 32768
    while symbol < alphabet and space > 0:
        length = length_code.read(reader)
        if length < 16:
            repeat = 0
            lengths[symbol] = length
            symbol += 1
            if length:
                previous = length
                space -= 32768 >> length
            continue
        extra = length - 14
        new = previous if length == 16 else 0
        if repeat_length != new:
            repeat, repeat_length = 0, new
        old = repeat
        if repeat > 0: repeat = (repeat - 2) << extra
        repeat += reader.bits(extra) + 3
        delta = repeat - old
        if symbol + delta > alphabet: raise BrotliError("code length repeat goes past the alphabet")
        for _ in range(delta):
            lengths[symbol] = repeat_length
            symbol += 1
        if repeat_length: space -= delta << (15 - repeat_length)
    if space != 0: raise BrotliError("invalid code lengths")
    return Prefix(lengths)


def read_count(reader):
    """NBLTYPES and NTREES: 1 to 256."""
    if not reader.bits(1): return 1
    n = reader.bits(3)
    return 2 if n == 0 else (1 << n) + reader.bits(n) + 1


def read_block_count(reader, code):
    symbol = code.read(reader)
    return BLOCK_COUNT_BASE[symbol] + reader.bits(BLOCK_COUNT_EXTRA[symbol])


def read_context_map(reader, size, trees):
    if trees < 2: return [0] * size
    rle = reader.bits(4) + 1 if reader.bits(1) else 0
    code = read_prefix(reader, trees + rle)
    values = []
    while len(values) < size:
        symbol = code.read(reader)
        if symbol == 0: values.append(0)
        elif symbol <= rle: values.extend([0] * ((1 << symbol) + reader.bits(symbol)))
        else: values.append(symbol - rle)
    if len(values) > size: raise BrotliError("context map is too long")
    if reader.bits(1):
        table = list(range(256))
        for i, value in enumerate(values):
            item = table[value]
            values[i] = item
            if value:
                del table[value]
                table.insert(0, item)
    return values


class Blocks:
    """Block types and counts of one category (literals, insert-and-copy commands, or distances)."""

    def __init__(self, reader):
        self.types = read_count(reader)
        self.type, self.last, self.second = 0, 0, 1
        self.count = 1 << 28
        if self.types >= 2:
            self.type_code = read_prefix(reader, self.types + 2)
            self.count_code = read_prefix(reader, 26)
            self.count = read_block_count(reader, self.count_code)

    def switch(self, reader):
        symbol = self.type_code.read(reader)
        if symbol == 0: new = self.second
        elif symbol == 1: new = (self.last + 1) % self.types
        else: new = symbol - 2
        if new >= self.types: raise BrotliError("invalid block type")
        self.second, self.last, self.type = self.last, new, new
        self.count = read_block_count(reader, self.count_code)


def transform_word(word, index):
    prefix, kind, suffix = TRANSFORMS[index]
    word = bytearray(word)
    if 3 <= kind <= 11: word = word[kind - 2:]
    elif 12 <= kind <= 20: word = word[:max(0, len(word) - (kind - 11))]
    elif kind in (1, 2):
        i = 0
        while i < len(word):
            if word[i] < 0xC0:
                if 97 <= word[i] <= 122: word[i] ^= 32
                step = 1
            elif word[i] < 0xE0:
                if i + 1 < len(word): word[i + 1] ^= 32
                step = 2
            else:
                if i + 2 < len(word): word[i + 2] ^= 5
                step = 3
            if kind == 1: break
            i += step
    return prefix + bytes(word) + suffix


def decompress(data):
    """The decompressed bytes of a Brotli stream. Raises BrotliError for a malformed stream."""
    reader = BitReader(bytes(data))
    if not reader.bits(1): window = 16
    else:
        n = reader.bits(3)
        if n: window = 17 + n
        else:
            n = reader.bits(3)
            if n == 1: raise BrotliError("invalid window size")
            window = 8 + n if n else 17
    max_backward = (1 << window) - 16
    out = bytearray()
    distances = [16, 15, 11, 4]
    while True:
        last = reader.bits(1)
        if last and reader.bits(1): break
        nibbles = reader.bits(2)
        if nibbles == 3:
            if reader.bits(1): raise BrotliError("reserved bit is set")
            size_bytes = reader.bits(2)
            skip = 0
            for i in range(size_bytes): skip |= reader.bits(8) << (8 * i)
            if size_bytes: skip += 1
            reader.align()
            for _ in range(skip): reader.byte()
            if last: break
            continue
        length = reader.bits(4 * (nibbles + 4)) + 1
        if not last and reader.bits(1):
            reader.align()
            for _ in range(length): out.append(reader.byte())
            continue
        literal_blocks, command_blocks, distance_blocks = Blocks(reader), Blocks(reader), Blocks(reader)
        postfix = reader.bits(2)
        direct = reader.bits(4) << postfix
        postfix_mask = (1 << postfix) - 1
        modes = [reader.bits(2) for _ in range(literal_blocks.types)]
        literal_trees = read_count(reader)
        literal_map = read_context_map(reader, 64 * literal_blocks.types, literal_trees)
        distance_trees = read_count(reader)
        distance_map = read_context_map(reader, 4 * distance_blocks.types, distance_trees)
        literal_codes = [read_prefix(reader, 256) for _ in range(literal_trees)]
        command_codes = [read_prefix(reader, 704) for _ in range(command_blocks.types)]
        distance_alphabet = 16 + direct + (48 << postfix)
        distance_codes = [read_prefix(reader, distance_alphabet) for _ in range(distance_trees)]
        end = len(out) + length
        while len(out) < end:
            if command_blocks.count == 0: command_blocks.switch(reader)
            command_blocks.count -= 1
            symbol = command_codes[command_blocks.type].read(reader)
            cell = symbol >> 6
            insert_code = CELLS[cell][0] + ((symbol >> 3) & 7)
            copy_code = CELLS[cell][1] + (symbol & 7)
            insert = INSERT_BASE[insert_code] + reader.bits(INSERT_EXTRA[insert_code])
            copy = COPY_BASE[copy_code] + reader.bits(COPY_EXTRA[copy_code])
            for _ in range(insert):
                if literal_blocks.count == 0: literal_blocks.switch(reader)
                literal_blocks.count -= 1
                p1 = out[-1] if out else 0
                p2 = out[-2] if len(out) > 1 else 0
                mode = modes[literal_blocks.type]
                if mode == 0: context = p1 & 0x3F
                elif mode == 1: context = p1 >> 2
                elif mode == 2: context = LUT0[p1] | LUT1[p2]
                else: context = (LUT2[p1] << 3) | LUT2[p2]
                out.append(literal_codes[literal_map[64 * literal_blocks.type + context]].read(reader))
            if len(out) >= end: break
            if cell < 2:
                code = 0
            else:
                if distance_blocks.count == 0: distance_blocks.switch(reader)
                distance_blocks.count -= 1
                context = 3 if copy > 4 else copy - 2
                code = distance_codes[distance_map[4 * distance_blocks.type + context]].read(reader)
            if code < 16:
                distance = distances[-1 - DISTANCE_SHORT_INDEX[code]] + DISTANCE_SHORT_OFFSET[code]
                if distance <= 0: raise BrotliError("invalid distance")
            elif code < 16 + direct:
                distance = code - 15
            else:
                code_offset = code - direct - 16
                bits = 1 + (code_offset >> (postfix + 1))
                high = code_offset >> postfix
                offset = ((2 + (high & 1)) << bits) - 4
                distance = ((offset + reader.bits(bits)) << postfix) + (code_offset & postfix_mask) + direct + 1
            limit = min(max_backward, len(out))
            if distance > limit:
                if not 4 <= copy <= 24: raise BrotliError("invalid dictionary reference length")
                word_id = distance - limit - 1
                bits = DICTIONARY_BITS[copy]
                transform = word_id >> bits
                if transform >= len(TRANSFORMS): raise BrotliError("invalid dictionary transform")
                start = DICTIONARY_OFFSET[copy] + (word_id & ((1 << bits) - 1)) * copy
                out += transform_word(dictionary()[start:start + copy], transform)
            else:
                if code: distances = [distances[1], distances[2], distances[3], distance]
                start = len(out) - distance
                if distance >= copy: out += out[start:start + copy]
                else:
                    for i in range(copy): out.append(out[start + i])
        if len(out) > end: raise BrotliError("meta-block is longer than its size")
        if last: break
    return bytes(out)
