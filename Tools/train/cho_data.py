"""
Reader for ChoSim's binary formats, and a port of SimFeatures.Encode.

The encoder is duplicated here rather than shared, which is the one place this project
accepts a reimplementation. It is made safe by `goldens.bin`: ChoSim writes positions
together with the planes it actually produced, and test_features.py asserts this port
reproduces them exactly. Run that test after touching either encoder.

Layout mirrors the C# exactly: planes are (C, H, W) with H = intersectionHeight and
W = intersectionWidth, flattened as plane*H*W + y*W + x, which is what conv2d wants.
"""

import struct
import numpy as np

# --- plane indices, mirroring SimFeatures ---------------------------------
OWN_PIECES, OPP_PIECES = 0, 6
OWN_STONES, OPP_STONES = 12, 13
OWN_LIBERTIES, OPP_LIBERTIES = 14, 17
SURROUND_PRESS = 20
OWN_TERRITORY, OPP_TERRITORY = 23, 24
PHASE_CHESS, PHASE_TERRITORY, PHASE_BONUS, PHASE_MAIN_STONE = 25, 26, 27, 28
KO_POINT, PLACEABLE_MASK, LAST_MOVED, NO_PROGRESS = 29, 30, 31, 32
PLANE_COUNT = 33

WHITE, BLACK = 0, 1
NO_COORD = 0xFF


class Position:
    """A decoded SimState, carrying only what the encoder and trainer need."""

    __slots__ = ("bw", "bh", "iw", "ih", "to_move", "phase_one",
                 "waiting_territory", "waiting_bonus", "squares", "stones",
                 "ko", "last_moved", "no_progress")

    @property
    def flip(self):
        """Canonicalisation: flip vertically when Black moves, so the mover advances up."""
        return self.to_move == BLACK

    def map_square(self, x, y):
        return x, (self.bh - 1 - y) if self.flip else y

    def map_intersection(self, ix, iy):
        return ix, (self.ih - 1 - iy) if self.flip else iy


def decode_position(buf):
    """Inverse of PositionCodec.Encode."""
    p = Position()
    o = 0

    p.bw = buf[o]; o += 1
    p.bh = buf[o]; o += 1
    p.iw, p.ih = p.bw + 1, p.bh + 1

    flags = buf[o]; o += 1
    p.to_move = BLACK if (flags & 1) else WHITE
    p.phase_one = bool(flags & 2)
    p.waiting_territory = bool(flags & 4)
    p.waiting_bonus = bool(flags & 8)

    o += 1  # castling rights; not a feature plane

    # squares[x, y] -> (colour, type, has_moved, just_double_stepped) or None
    p.squares = np.zeros((p.bw, p.bh, 2), dtype=np.int8)
    p.squares[:] = -1
    for x in range(p.bw):
        for y in range(p.bh):
            b = buf[o]; o += 1
            if b == 0:
                continue
            v = b - 1
            idx = v >> 2
            p.squares[x, y, 0] = idx // 6      # colour
            p.squares[x, y, 1] = idx % 6       # PieceType

    p.stones = np.zeros((p.iw, p.ih), dtype=np.int8)
    for ix in range(p.iw):
        for iy in range(p.ih):
            p.stones[ix, iy] = buf[o]; o += 1

    def coord():
        nonlocal o
        cx, cy = buf[o], buf[o + 1]
        o += 2
        return None if (cx == NO_COORD and cy == NO_COORD) else (cx, cy)

    p.ko = coord()
    p.last_moved = coord()
    p.no_progress = buf[o]; o += 1

    return p


def mirror_position(p):
    """
    Left-right mirror. A free doubling of the training set: with castling disabled in the
    small variant there is no side-dependent rule, so the mirrored position is as legal and
    as meaningful as the original.

    Deliberately mirrors the POSITION and lets `encode` rebuild the planes, rather than
    flipping the tensor. The two grids differ in width - a square at cell x maps to bw-1-x
    while an intersection maps to iw-1-ix - so a single flip of the tensor's width axis
    would shift the piece planes one cell against the stone planes.
    """
    m = Position()
    m.bw, m.bh, m.iw, m.ih = p.bw, p.bh, p.iw, p.ih
    m.to_move = p.to_move
    m.phase_one = p.phase_one
    m.waiting_territory = p.waiting_territory
    m.waiting_bonus = p.waiting_bonus
    m.no_progress = p.no_progress

    m.squares = p.squares[::-1, :, :].copy()
    m.stones = p.stones[::-1, :].copy()

    m.ko = None if p.ko is None else (p.iw - 1 - p.ko[0], p.ko[1])
    m.last_moved = None if p.last_moved is None else (p.bw - 1 - p.last_moved[0], p.last_moved[1])
    return m


def mirror_policy_index(idx, is_chess, p):
    """The matching index under a left-right mirror. Horizontal, so it is independent of the
    vertical side-to-move canonicalisation already baked into the stored index."""
    if is_chess:
        squares = p.bw * p.bh

        def flip_square(i):
            cy, cx = divmod(i, p.bw)
            return cy * p.bw + (p.bw - 1 - cx)

        return flip_square(idx // squares) * squares + flip_square(idx % squares)

    cy, cx = divmod(idx, p.iw)
    return cy * p.iw + (p.iw - 1 - cx)


def liberty_map(p):
    """Liberty count of the group each stone belongs to; empty points read 0."""
    out = np.zeros((p.iw, p.ih), dtype=np.int32)
    done = np.zeros((p.iw, p.ih), dtype=bool)

    for sx in range(p.iw):
        for sy in range(p.ih):
            if done[sx, sy] or p.stones[sx, sy] == 0:
                continue

            colour = p.stones[sx, sy]
            group, libs, stack = [], set(), [(sx, sy)]
            done[sx, sy] = True

            while stack:
                x, y = stack.pop()
                group.append((x, y))
                for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                    if not (0 <= nx < p.iw and 0 <= ny < p.ih):
                        continue
                    c = p.stones[nx, ny]
                    if c == 0:
                        libs.add((nx, ny))
                    elif c == colour and not done[nx, ny]:
                        done[nx, ny] = True
                        stack.append((nx, ny))

            for x, y in group:
                out[x, y] = len(libs)

    return out


def encode(p, no_progress_limit=50):
    """Port of SimFeatures.Encode. Returns (PLANE_COUNT, ih, iw) float32."""
    t = np.zeros((PLANE_COUNT, p.ih, p.iw), dtype=np.float32)

    mover = p.to_move
    own_stone = 1 if mover == WHITE else 2      # SimStoneColor: White=1, Black=2
    opp_stone = 2 if mover == WHITE else 1

    # pieces
    for x in range(p.bw):
        for y in range(p.bh):
            colour = p.squares[x, y, 0]
            if colour < 0:
                continue
            block = OWN_PIECES if colour == mover else OPP_PIECES
            cx, cy = p.map_square(x, y)
            t[block + int(p.squares[x, y, 1]), cy, cx] = 1.0

    # stones and liberties
    libs = liberty_map(p)
    for ix in range(p.iw):
        for iy in range(p.ih):
            c = p.stones[ix, iy]
            if c == 0:
                continue
            cx, cy = p.map_intersection(ix, iy)
            t[OWN_STONES if c == own_stone else OPP_STONES, cy, cx] = 1.0

            block = OWN_LIBERTIES if c == own_stone else OPP_LIBERTIES
            l = libs[ix, iy]
            t[block + (0 if l <= 1 else (1 if l == 2 else 2)), cy, cx] = 1.0

    # surround pressure and territory, per square
    for x in range(p.bw):
        for y in range(p.bh):
            if x + 1 >= p.iw or y + 1 >= p.ih:
                continue
            own = opp = 0
            for i in range(4):
                c = p.stones[x + (i & 1), y + (i >> 1)]
                if c == own_stone:
                    own += 1
                elif c == opp_stone:
                    opp += 1

            cx, cy = p.map_square(x, y)
            if 1 <= opp <= 3:
                t[SURROUND_PRESS + opp - 1, cy, cx] = 1.0
            if own >= 3:
                t[OWN_TERRITORY, cy, cx] = 1.0
            if opp >= 3:
                t[OPP_TERRITORY, cy, cx] = 1.0

    # which decision this is
    if p.phase_one:
        t[PHASE_CHESS] = 1.0
    elif p.waiting_territory:
        t[PHASE_TERRITORY] = 1.0
    elif p.waiting_bonus:
        t[PHASE_BONUS] = 1.0
    else:
        t[PHASE_MAIN_STONE] = 1.0

    if p.ko is not None:
        cx, cy = p.map_intersection(*p.ko)
        t[KO_POINT, cy, cx] = 1.0

    for ix in range(p.iw):
        for iy in range(p.ih):
            if p.stones[ix, iy] != 0:
                continue
            if p.ko is not None and p.ko == (ix, iy):
                continue
            cx, cy = p.map_intersection(ix, iy)
            t[PLACEABLE_MASK, cy, cx] = 1.0

    if p.last_moved is not None:
        lx, ly = p.last_moved
        if 0 <= lx < p.bw and 0 <= ly < p.bh:
            cx, cy = p.map_square(lx, ly)
            t[LAST_MOVED, cy, cx] = 1.0

    t[NO_PROGRESS] = min(1.0, p.no_progress / max(1, no_progress_limit))
    return t


# --------------------------------------------------------------- file readers

class _Reader:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.buf = f.read()
        self.o = 0

    def take(self, n):
        b = self.buf[self.o:self.o + n]
        self.o += n
        return b

    def u8(self):
        v = self.buf[self.o]; self.o += 1; return v

    def u16(self):
        v = struct.unpack_from("<H", self.buf, self.o)[0]; self.o += 2; return v

    def i32(self):
        v = struct.unpack_from("<i", self.buf, self.o)[0]; self.o += 4; return v

    def f32(self):
        v = struct.unpack_from("<f", self.buf, self.o)[0]; self.o += 4; return v


def read_selfplay(path):
    """Yields dicts with position, policy indices/visits, and the value target."""
    r = _Reader(path)
    magic = r.take(6).decode("ascii")
    if magic != "CHOSP2":
        raise ValueError(f"expected CHOSP2, got {magic!r}")

    variant = r.u8()
    planes = r.i32()
    limit = r.i32()
    count = r.i32()

    if planes != PLANE_COUNT:
        raise ValueError(f"file has {planes} planes, this reader expects {PLANE_COUNT}")

    samples = []
    for _ in range(count):
        pos = r.take(r.u16())
        n = r.u16()
        idx = np.empty(n, dtype=np.int32)
        vis = np.empty(n, dtype=np.float32)
        for k in range(n):
            idx[k] = r.u16()
            vis[k] = r.u16()
        samples.append({
            "position": pos,
            "policy_index": idx,
            "policy_visits": vis,
            "value": r.f32(),
        })

    return {"variant": variant, "no_progress_limit": limit, "samples": samples}


def read_goldens(path):
    """Yields (position_bytes, planes) pairs written by ChoSim's `goldens` command."""
    r = _Reader(path)
    magic = r.take(6).decode("ascii")
    if magic != "CHOGD1":
        raise ValueError(f"expected CHOGD1, got {magic!r}")

    variant = r.u8()
    planes = r.i32()
    limit = r.i32()
    count = r.i32()

    out = []
    for _ in range(count):
        pos = r.take(r.u16())
        n = r.i32()
        flat = np.frombuffer(r.take(4 * n), dtype="<f4")
        out.append((pos, flat))

    return {"variant": variant, "no_progress_limit": limit, "pairs": out}
