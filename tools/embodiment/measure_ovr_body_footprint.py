#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Measure the floor-footprint radius of the Meta/OVR preset avatar from its mesh (embodiment registration, `body`).

usage: python3 -I tools/embodiment/measure_ovr_body_footprint.py --glb 0_quest.glb --measurement MEASUREMENT.json [--out BODY.json]

Inputs
  --glb          a Meta Avatars SDK preset avatar (PresetAvatars_Quest/0_quest.glb; decompressed, or zstd-compressed with the `zstandard`
                 module installed or the `zstd` command on PATH). Read only, never copied into the repository.
  --measurement  the measurement artefact the registration data is generated from. Its asset sha256 values are checked against the GLB so the
                 radius is taken on the asset the offsets were measured on.

What is measured (the core's definition of the body floor-footprint radius): the radius of the circle on the floor that bounds
the body's footprint, about the root anchor, in metres.

Procedure (recorded in the provenance written to the output)
  1. The skinned mesh of LOD00_combined_geometry is evaluated in the asset's rest (bind) pose by linear blend skinning from JOINTS_0 / WEIGHTS_0 /
     the skin's inverse bind matrices and the joints' node world transforms (W_rest * inverseBind = I is asserted to 1e-6). Positions are metres
     in the asset's frame (+y up, the floor is y = 0 under root_joint).
  2. The centre of the circle is the root anchor's floor point: the root_joint's world position projected on the floor (x, z).
  3. TRUNK footprint = the vertices whose dominant skin joint (largest weight) is NOT a joint of the arms (clavicle, shoulder, elbow, wrist, hand
     and finger joints, their twist and partial joints). The radius is the largest horizontal distance (x, z) from the centre to any such vertex.
     The arms are articulated and are what the hand anchors and AffordancePose place on furniture, so they are not part of the clearance the
     ROOT keeps from obstacles. The registered radius is this one.
  4. For the record, the same distance over ALL vertices (arms included, A rest pose) is stored as `radius_with_arms_rest_pose_m`.
Output: a `body` block {floor_footprint_radius_m, provenance} merged into the measurement artefact by tools/embodiment/ovr_registration_artefacts.py
(--body). Nothing here is a default: the number is the mesh's, and it is a numeric artefact of the private repository (never commit it to a public one).
"""
import argparse, hashlib, json, os, shutil, struct, subprocess, sys, tempfile
import numpy as np

COMPONENT = {5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2), 5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
WIDTH = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}
NORM_DIV = {5120: 127.0, 5121: 255.0, 5122: 32767.0, 5123: 65535.0}
MESH = 'LOD00_combined_geometry'
ARM_JOINT_MARKERS = ('clavicle', 'shoulder', 'elbow', 'handWrist', 'hand', 'arm', 'forearm', 'wrist', 'palm')


def read_glb(path):
    raw = open(path, 'rb').read()
    if raw[:4] == b'\x28\xb5\x2f\xfd':
        try:
            import zstandard
            raw = zstandard.ZstdDecompressor().stream_reader(open(path, 'rb')).read()
        except ImportError:
            exe = shutil.which('zstd')
            if exe is None:
                raise SystemExit('the GLB is zstd-compressed: install the zstandard module or the zstd command')
            raw = subprocess.run([exe, '-d', '-c', '-q', path], check=True, stdout=subprocess.PIPE).stdout
    assert raw[:4] == b'glTF'
    off, j, binary = 12, None, None
    while off < len(raw):
        length, kind = struct.unpack('<II', raw[off:off + 8])
        if kind == 0x4E4F534A:
            j = json.loads(raw[off + 8:off + 8 + length])
        elif kind == 0x004E4942:
            binary = raw[off + 8:off + 8 + length]
        off += 8 + length
    return j, binary, hashlib.sha256(raw).hexdigest()


def accessor(j, binary, index):
    a = j['accessors'][index]
    view = j['bufferViews'][a['bufferView']]
    fmt, size = COMPONENT[a['componentType']]
    width = WIDTH[a['type']]
    stride = view.get('byteStride') or size * width
    base = view.get('byteOffset', 0) + a.get('byteOffset', 0)
    out = np.empty((a['count'], width), dtype=np.float64 if a['componentType'] == 5126 else np.int64)
    # one strided read per element is slow in python; use numpy's structured view over the buffer
    dtype = np.dtype([('v', '<' + fmt, (width,))])
    full = np.dtype({'names': ['v'], 'formats': [dtype['v']], 'offsets': [0], 'itemsize': stride})
    arr = np.frombuffer(binary, dtype=full, count=a['count'], offset=base)['v']
    out = arr.astype(np.float64) if a.get('normalized') or a['componentType'] == 5126 else arr.astype(np.int64)
    if a.get('normalized') and a['componentType'] in NORM_DIV:
        out = np.maximum(out / NORM_DIV[a['componentType']], -1.0)
    return out


def dequantize_positions(j, pos):
    """FB_mesh_quantization (Meta Avatars SDK): POSITION is int16 normalized to [-1, 1] across the mesh bounds the extension records
    (quantize_mesh_min_POSITION .. quantize_mesh_max_POSITION): p = (max + min) / 2 + n * (max - min) / 2."""
    ext = (j.get('extensions') or {}).get('FB_mesh_quantization')
    if ext is None:
        return pos
    lo = np.array(ext['quantize_mesh_min_POSITION'], float)
    hi = np.array(ext['quantize_mesh_max_POSITION'], float)
    return (hi + lo) / 2.0 + pos * (hi - lo) / 2.0


def quat_to_mat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def node_worlds(j):
    nodes = j['nodes']
    parent = {c: i for i, n in enumerate(nodes) for c in n.get('children', [])}
    cache = {}

    def world(i):
        if i in cache:
            return cache[i]
        n = nodes[i]
        if 'matrix' in n:
            M = np.array(n['matrix'], float).reshape(4, 4).T
        else:
            M = np.eye(4)
            M[:3, :3] = quat_to_mat(n.get('rotation', [0, 0, 0, 1])) * np.array(n.get('scale', [1, 1, 1]))[None, :]
            M[:3, 3] = n.get('translation', [0, 0, 0])
        cache[i] = (world(parent[i]) @ M) if i in parent else M
        return cache[i]

    return world


def is_arm_joint(name):
    low = name.lower()
    return any(m.lower() in low for m in ARM_JOINT_MARKERS)


def measure(glb_path):
    j, binary, sha_dec = read_glb(glb_path)
    world = node_worlds(j)
    nodes = j['nodes']
    mesh_node = next(i for i, n in enumerate(nodes) if n.get('name') == MESH)
    mesh = j['meshes'][nodes[mesh_node]['mesh']]
    skin = j['skins'][nodes[mesh_node]['skin']] if 'skin' in nodes[mesh_node] else j['skins'][0]
    prim = mesh['primitives'][0]
    pos = dequantize_positions(j, accessor(j, binary, prim['attributes']['POSITION']))
    joints = accessor(j, binary, prim['attributes']['JOINTS_0']).astype(np.int64)
    weights = accessor(j, binary, prim['attributes']['WEIGHTS_0'])
    ibm = accessor(j, binary, skin['inverseBindMatrices']).reshape(-1, 4, 4)
    ibm = np.transpose(ibm, (0, 2, 1))                    # glTF stores column-major
    skin_joints = skin['joints']
    skin_world = np.stack([world(n) for n in skin_joints])
    resid = np.abs(skin_world @ ibm - np.eye(4)).max()
    assert resid < 1e-6, 'the asset is not in its bind pose at rest (%g)' % resid
    # linear blend skinning at rest: sum_k w_k W_k IBM_k p ; the mesh node's own transform places the vertices (bind shape)
    P = np.concatenate([pos, np.ones((len(pos), 1))], axis=1)
    mesh_world = world(mesh_node)
    skinned = np.zeros((len(pos), 3))
    for k in range(4):
        M = skin_world[joints[:, k]] @ ibm[joints[:, k]] @ np.linalg.inv(mesh_world)
        skinned += weights[:, k:k + 1] * np.einsum('nij,nj->ni', M, P)[:, :3]
    root = next(i for i in skin_joints if nodes[i].get('name') == 'root_joint')
    centre = world(root)[:3, 3]
    horizontal = np.hypot(skinned[:, 0] - centre[0], skinned[:, 2] - centre[2])
    dominant = joints[np.arange(len(pos)), np.argmax(weights, axis=1)]
    arm = np.array([is_arm_joint(nodes[skin_joints[d]].get('name', '')) for d in dominant])
    assert arm.any() and (~arm).any()
    floor_y = float(skinned[:, 1].min())
    return {
        'trunk_radius_m': float(horizontal[~arm].max()),
        'with_arms_radius_m': float(horizontal.max()),
        'vertices': int(len(pos)), 'trunk_vertices': int((~arm).sum()), 'arm_vertices': int(arm.sum()),
        'floor_y_m': floor_y, 'root_floor_point_xz_m': [float(centre[0]), float(centre[2])],
        'bind_pose_residual': float(resid), 'sha_decompressed': sha_dec,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--glb', required=True)
    ap.add_argument('--measurement', required=True)
    ap.add_argument('--out')
    args = ap.parse_args()
    meas = json.load(open(args.measurement))
    m = measure(args.glb)
    assert m['sha_decompressed'] == meas['asset']['sha256_decompressed_glb'], 'the GLB is not the asset the measurement was taken on'
    body = {
        'floor_footprint_radius_m': round(m['trunk_radius_m'], 4),
        'provenance': {
            'procedure': ('Floor-footprint radius of the Meta Avatars SDK preset avatar, measured from its mesh (tools/embodiment/measure_ovr_body_footprint.py). '
                          '1) LOD00_combined_geometry is skinned at the rest (bind) pose by linear blend skinning (JOINTS_0, WEIGHTS_0, the skin inverse bind '
                          'matrices, the joints node world transforms; W_rest * inverseBind = I asserted to 1e-6); metres, +y up. 2) The circle is centred on the '
                          'root anchor floor point, the root_joint position projected on the floor (x, z). 3) TRUNK footprint: the vertices whose dominant skin joint '
                          'is not an arm joint (clavicle, shoulder, elbow, wrist, hand and finger joints and their twist/partial joints); the radius is the largest '
                          'horizontal distance from the centre to any such vertex, rounded to 0.1 mm. The arms are articulated and are what the hand anchors place '
                          'on furniture, so the ROOT clearance excludes them. The radius with the arms in the A rest pose is recorded as '
                          'radius_with_arms_rest_pose_m for the record only.'),
            'asset': meas['asset']['name'],
            'asset_sha256': meas['asset']['sha256'],
            'asset_sha256_decompressed_glb': m['sha_decompressed'],
            'source': 'ovr-avatar.measurement.json',
            'measured_on': meas.get('measured_on'),
            'radius_with_arms_rest_pose_m': round(m['with_arms_radius_m'], 4),
            'vertices': m['vertices'], 'trunk_vertices': m['trunk_vertices'], 'arm_vertices': m['arm_vertices'],
            'caveats': ('Preset 0 only, the dressed combined mesh (clothing and hair included), rest A pose. The radius is a property of the avatar mesh, '
                        'not of how a person stands: it does not follow limb motion. The registered radius is the TRUNK radius; the with-arms value is recorded here in the provenance only.'),
        },
    }
    text = json.dumps(body, indent=1, ensure_ascii=False) + '\n'
    if args.out:
        open(args.out, 'w').write(text)
    print(text)


if __name__ == '__main__':
    main()
