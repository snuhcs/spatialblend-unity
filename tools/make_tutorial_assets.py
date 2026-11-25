#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Generate the lobby tutorial's synthetic sample assets (pure standard library, deterministic).

  sample_object.glb   a low-poly vase, revolved from a hand-written profile (our own geometry, no third-party source)
  sample_photo.png    a software render of that vase on a plain floor against a wall (our own render, not a photograph)
  sample_shared.glb   a small table lamp (base, stem, conical shade), revolved from its own hand-written profile: the object the
                      tutorial shows as "not in your room, created as a virtual object" (clearly not the vase)

All three are released under CC0 1.0 by the repository authors. The output is byte-identical on every run, so the sha256 in
Assets/SpatialBlend/Samples/Tutorial/ASSETS.md can be re-checked:  python3 tools/make_tutorial_assets.py OUTDIR
The files are shipped as `.bytes` text assets (see TutorialAssets.cs); this script writes the plain names.
"""
import json
import math
import struct
import sys
import zlib
from pathlib import Path

PROFILE = [(0.000, 0.000), (0.090, 0.000), (0.105, 0.015), (0.135, 0.080), (0.150, 0.160), (0.138, 0.240),
           (0.095, 0.300), (0.062, 0.340), (0.058, 0.390), (0.075, 0.425), (0.070, 0.440), (0.000, 0.440)]
# the table lamp: a flat base, a thin stem, a conical shade closed at the top
LAMP_PROFILE = [(0.000, 0.000), (0.100, 0.000), (0.100, 0.018), (0.040, 0.034), (0.014, 0.050), (0.014, 0.280),
                (0.050, 0.290), (0.170, 0.420), (0.000, 0.420)]
SEGMENTS = 28
BASE_COLOR = (0.82, 0.38, 0.24)
LAMP_COLOR = (0.24, 0.52, 0.84)


def build_mesh(profile=None):
    """Revolve a profile (default PROFILE, the vase) about the y axis. Smooth normals. Returns positions, normals, triangle indices."""
    profile = PROFILE if profile is None else profile
    ring = len(profile)
    positions, normals, indices = [], [], []
    # profile normals (in the r,y plane), averaged over the two adjacent segments
    pn = []
    for i, (r, y) in enumerate(profile):
        def seg(a, b):
            dr, dy = profile[b][0] - profile[a][0], profile[b][1] - profile[a][1]
            length = math.hypot(dr, dy) or 1.0
            return (dy / length, -dr / length)   # outward when the profile goes up and out
        n0 = seg(i - 1, i) if i > 0 else None
        n1 = seg(i, i + 1) if i < ring - 1 else None
        parts = [n for n in (n0, n1) if n]
        nr = sum(n[0] for n in parts) / len(parts)
        ny = sum(n[1] for n in parts) / len(parts)
        length = math.hypot(nr, ny) or 1.0
        pn.append((nr / length, ny / length))
    for s in range(SEGMENTS + 1):
        a = 2.0 * math.pi * s / SEGMENTS
        ca, sa = math.cos(a), math.sin(a)
        for (r, y), (nr, ny) in zip(profile, pn):
            positions.append((r * ca, y, r * sa))
            normals.append((nr * ca, ny, nr * sa))
    for s in range(SEGMENTS):
        for i in range(ring - 1):
            a = s * ring + i
            b = (s + 1) * ring + i
            indices += [a, b, a + 1, b, b + 1, a + 1]
    return positions, normals, indices


def pad4(data, fill):
    return data + fill * ((4 - len(data) % 4) % 4)


def write_glb(path, profile=None, color=None, name="sample_object", material="glaze"):
    color = BASE_COLOR if color is None else color
    positions, normals, indices = build_mesh(profile)
    pos_bytes = b"".join(struct.pack("<3f", *p) for p in positions)
    nrm_bytes = b"".join(struct.pack("<3f", *n) for n in normals)
    idx_bytes = b"".join(struct.pack("<H", i) for i in indices)
    idx_bytes = pad4(idx_bytes, b"\x00")
    binary = pos_bytes + nrm_bytes + idx_bytes
    xs = [p[0] for p in positions]; ys = [p[1] for p in positions]; zs = [p[2] for p in positions]
    doc = {
        "asset": {"version": "2.0", "generator": "tools/make_tutorial_assets.py"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": name, "mesh": 0}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 2, "material": 0}]}],
        "materials": [{"name": material, "pbrMetallicRoughness": {"baseColorFactor": list(color) + [1.0], "metallicFactor": 0.0, "roughnessFactor": 0.55}}],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions), "type": "VEC3",
             "min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]},
            {"bufferView": 1, "componentType": 5126, "count": len(normals), "type": "VEC3"},
            {"bufferView": 2, "componentType": 5123, "count": len(indices), "type": "SCALAR"},
        ],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(pos_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": len(pos_bytes), "byteLength": len(nrm_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": len(pos_bytes) + len(nrm_bytes), "byteLength": len(idx_bytes) , "target": 34963},
        ],
        "buffers": [{"byteLength": len(binary)}],
    }
    json_bytes = pad4(json.dumps(doc, separators=(",", ":"), sort_keys=True).encode("utf-8"), b" ")
    total = 12 + 8 + len(json_bytes) + 8 + len(binary)
    out = struct.pack("<III", 0x46546C67, 2, total)
    out += struct.pack("<II", len(json_bytes), 0x4E4F534A) + json_bytes
    out += struct.pack("<II", len(binary), 0x004E4942) + binary
    Path(path).write_bytes(out)


# ---------------------------------------------------------------- the render

def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a):
    l = math.sqrt(dot(a, a)) or 1.0
    return (a[0] / l, a[1] / l, a[2] / l)


def write_png(path, width, height, rgb):
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        raw += rgb[y * width * 3:(y + 1) * width * 3]
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
    Path(path).write_bytes(png)


def render(path, width=640, height=480):
    eye = (0.10, 0.38, 1.05)
    target = (0.0, 0.20, 0.0)
    forward = norm(sub(target, eye))
    right = norm(cross(forward, (0.0, 1.0, 0.0)))
    up = cross(right, forward)
    tan_half = math.tan(math.radians(42) / 2)
    aspect = width / height
    light = norm((-0.4, 0.8, 0.5))
    wall_z = -0.7
    rgb = bytearray(width * height * 3)
    depth = [1e9] * (width * height)

    def put(x, y, color, z):
        i = y * width + x
        if z < depth[i]:
            depth[i] = z
            j = i * 3
            rgb[j] = max(0, min(255, int(color[0] * 255)))
            rgb[j + 1] = max(0, min(255, int(color[1] * 255)))
            rgb[j + 2] = max(0, min(255, int(color[2] * 255)))

    # backdrop: floor (planks) and wall, analytic
    for y in range(height):
        for x in range(width):
            px = (2.0 * (x + 0.5) / width - 1.0) * tan_half * aspect
            py = (1.0 - 2.0 * (y + 0.5) / height) * tan_half
            d = norm((forward[0] + right[0] * px + up[0] * py,
                      forward[1] + right[1] * px + up[1] * py,
                      forward[2] + right[2] * px + up[2] * py))
            hit = None
            if d[1] < -1e-6:
                t = -eye[1] / d[1]
                p = (eye[0] + d[0] * t, 0.0, eye[2] + d[2] * t)
                if p[2] > wall_z:
                    plank = int(math.floor(p[0] / 0.12))
                    tone = 0.62 + 0.05 * ((plank * 37) % 5) / 4.0
                    seam = abs((p[0] / 0.12) - round(p[0] / 0.12)) < 0.025
                    shade = 0.78 if seam else 1.0
                    fall = 1.0 - min(0.35, 0.12 * max(0.0, -p[2] + 0.3))
                    hit = (t, (tone * shade * fall, 0.47 * shade * fall + 0.02, 0.33 * shade * fall))
            if hit is None and d[2] < -1e-6:
                t = (wall_z - eye[2]) / d[2]
                p = (eye[0] + d[0] * t, eye[1] + d[1] * t, wall_z)
                if p[1] >= 0.0:
                    g = 0.86 - 0.10 * min(1.0, p[1] / 1.2)
                    hit = (t, (g, g * 0.97, g * 0.92))
            if hit is None:
                hit = (1e8, (0.5, 0.5, 0.5))
            put(x, y, hit[1], hit[0])

    positions, normals, indices = build_mesh()
    cam = []
    for p in positions:
        v = sub(p, eye)
        cam.append((dot(v, right), dot(v, up), dot(v, forward)))
    for t in range(0, len(indices), 3):
        ia, ib, ic = indices[t], indices[t + 1], indices[t + 2]
        pts = []
        for i in (ia, ib, ic):
            x, y, z = cam[i]
            if z <= 0.01:
                break
            sx = (x / z / (tan_half * aspect) * 0.5 + 0.5) * width
            sy = (0.5 - y / z / tan_half * 0.5) * height
            pts.append((sx, sy, z, normals[i]))
        if len(pts) < 3:
            continue
        (x0, y0, z0, n0), (x1, y1, z1, n1), (x2, y2, z2, n2) = pts
        area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
        if area <= 0:     # back face
            continue
        minx = max(0, int(math.floor(min(x0, x1, x2)))); maxx = min(width - 1, int(math.ceil(max(x0, x1, x2))))
        miny = max(0, int(math.floor(min(y0, y1, y2)))); maxy = min(height - 1, int(math.ceil(max(y0, y1, y2))))
        for y in range(miny, maxy + 1):
            for x in range(minx, maxx + 1):
                w0 = ((x1 - (x + 0.5)) * (y2 - (y + 0.5)) - (x2 - (x + 0.5)) * (y1 - (y + 0.5))) / area
                w1 = ((x2 - (x + 0.5)) * (y0 - (y + 0.5)) - (x0 - (x + 0.5)) * (y2 - (y + 0.5))) / area
                w2 = 1.0 - w0 - w1
                if w0 < 0 or w1 < 0 or w2 < 0:
                    continue
                z = w0 * z0 + w1 * z1 + w2 * z2
                n = norm((w0 * n0[0] + w1 * n1[0] + w2 * n2[0], w0 * n0[1] + w1 * n1[1] + w2 * n2[1], w0 * n0[2] + w1 * n1[2] + w2 * n2[2]))
                lambert = max(0.0, dot(n, light))
                rim = max(0.0, 1.0 - abs(dot(n, norm(sub(eye, (0, 0.2, 0))))) ) * 0.15
                k = 0.30 + 0.75 * lambert + rim
                put(x, y, (BASE_COLOR[0] * k, BASE_COLOR[1] * k, BASE_COLOR[2] * k), z)
    write_png(path, width, height, rgb)


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    write_glb(out / "sample_object.glb")
    render(out / "sample_photo.png")
    write_glb(out / "sample_shared.glb", LAMP_PROFILE, LAMP_COLOR, "sample_shared", "lampshade")
    print("wrote", out / "sample_object.glb", out / "sample_photo.png", out / "sample_shared.glb")


if __name__ == "__main__":
    main()
