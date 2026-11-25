#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Generate the whiteboard's grab glow sprite (Assets/SpatialBlend/Samples/Objects/Whiteboard/Textures/panelEdgeGlow.png).

A white rounded rectangle the size of the board (832 x 586 px, corner radius 31 px): a 3 px rim at alpha 218, an inner glow that
fades from alpha 65 to the faint fill (alpha 2) of the panel's face, transparent outside the corners. Our own work (CC0 1.0), made
from this profile alone; it replaced a sample texture of the Meta MR Utility Kit. Deterministic: the same bytes every run.

    python3 tools/make_panel_glow.py OUT.png
"""

import math
import struct
import sys
import zlib
from pathlib import Path

WIDTH, HEIGHT = 832, 586
RADIUS = 31.0
RIM_PX = 3.0
RIM_ALPHA = 218
GLOW_ALPHA = 65
FILL_ALPHA = 2
GLOW_FALLOFF = 0.075  # per pixel inward from the rim


def inside_distance(x, y):
    """Distance in pixels from the pixel centre to the rounded rectangle's edge, positive inside, negative outside."""
    px, py = x + 0.5, y + 0.5
    dx = min(px, WIDTH - px)
    dy = min(py, HEIGHT - py)
    if dx < RADIUS and dy < RADIUS:
        return RADIUS - math.hypot(RADIUS - dx, RADIUS - dy)
    return min(dx, dy)


def alpha(d):
    if d < 0:
        return 0
    if d < RIM_PX:
        return RIM_ALPHA
    return max(FILL_ALPHA, int(round(FILL_ALPHA + (GLOW_ALPHA - FILL_ALPHA) * math.exp(-GLOW_FALLOFF * (d - RIM_PX)))))


def png_bytes():
    rows = bytearray()
    for y in range(HEIGHT):
        rows.append(0)
        for x in range(WIDTH):
            rows += bytes((255, 255, 255, alpha(inside_distance(x, y))))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def main(argv):
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[-1].strip(), file=sys.stderr)
        return 2
    Path(argv[1]).write_bytes(png_bytes())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
