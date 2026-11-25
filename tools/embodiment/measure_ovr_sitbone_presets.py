#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Measure the Meta Avatars SDK seat ("sit-bone") contact offset for EVERY shipped Quest preset avatar (embodiment registration, `pelvis` anchor).

usage: python3 -I tools/embodiment/measure_ovr_sitbone_presets.py --presets-dir DIR --measurement MEASUREMENT.json --out OUT.json [--patch-mm 10]

Inputs
  --presets-dir  PresetAvatars_Quest/ holding N_quest.glb, N = 0..32 (zstd-compressed or plain GLB; see measure_ovr_body_footprint.read_glb). Read only, never copied.
  --measurement  ovr-avatar.measurement.json: the full text of the procedure is its pelvis anchor provenance (registration.anchors[pelvis].provenance.procedure)
                 and its top-level `procedure`/`assumptions`. Its pelvis offset / cross_preset_spread are used for the self-check (reported, not forced).
  --out          the per-preset artefact (Assets/.../Embodiment/Resources/Embodiment/ovr-avatar.sitbone-presets.json).

Procedure (SEAT, identical to the preset-0 measurement; code shared with measure_ovr_body_footprint.py: GLB reader, LOD00_combined_geometry, linear blend skinning
with W_rest * inverseBind = I asserted; to 1e-5 here because float32 residuals of presets 1,4,15,19 are 1.1-1.4e-6)
  hip_left/right_joint rotated -90 deg about world +x about the joint position, knee_left/right_joint +90 deg about +x about their (posed) position, every other
  joint at rest (descendants follow their parent). Candidates = vertices whose dominant skin joint is pelvis_joint, spineLower_joint, hip_left/right_joint or
  legUpper_twist_01..03_left/right_joint. Split by sign of x relative to pelvis_joint; per side a horizontal plane through the lowest candidate, patch = candidates
  within patch_mm, point = patch centroid; anchor = mean of the two side points with the lateral component set to 0, expressed relative to pelvis_joint in the
  pelvis_joint LOCAL native frame (rest orientation; local +x = up, +y = forward, +z = left). Registered canonical offset = B * native, B rows (0,0,1; 1,0,0; 0,1,0).
Caveats: clothed-surface lowest-patch proxy, no ischial anatomy; 'forward' is weakly supported (3..13 vertices per side). A preset that cannot be measured is listed
under `failures` with the reason and never given a default; the script exits non-zero then. Floats are rounded to 1e-4 m; the output is deterministic.
"""
import argparse, hashlib, json, os, sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure_ovr_body_footprint import read_glb, accessor, dequantize_positions, node_worlds, MESH  # noqa: E402

B = np.array([[0, 0, 1], [1, 0, 0], [0, 1, 0]], float)
SEAT_JOINTS = {'pelvis_joint', 'spineLower_joint', 'hip_left_joint', 'hip_right_joint'} | {
    'legUpper_twist_0%d_%s_joint' % (k, s) for k in (1, 2, 3) for s in ('left', 'right')}
BIND_TOL = 1e-5   # float32 storage: presets 1,4,15,19 have residual 1.1-1.4e-6 (> the 1e-6 used for preset 0), still ~5 orders below any measured offset
PATCHES = (5, 10, 20)
N_PRESETS = 33


def rot_about_x(deg, centre):
    t = np.radians(deg)
    R = np.array([[1, 0, 0], [0, np.cos(t), -np.sin(t)], [0, np.sin(t), np.cos(t)]])
    M = np.eye(4)
    M[:3, :3] = R
    M[:3, 3] = centre - R @ centre
    return M


def measure_glb(path, patches=PATCHES):
    j, binary, sha_dec = read_glb(path)
    world = node_worlds(j)
    nodes = j['nodes']
    parent = {c: i for i, n in enumerate(nodes) for c in n.get('children', [])}
    mesh_node = next(i for i, n in enumerate(nodes) if n.get('name') == MESH)
    skin = j['skins'][nodes[mesh_node]['skin']] if 'skin' in nodes[mesh_node] else j['skins'][0]
    prim = j['meshes'][nodes[mesh_node]['mesh']]['primitives'][0]
    pos = dequantize_positions(j, accessor(j, binary, prim['attributes']['POSITION']))
    joints = accessor(j, binary, prim['attributes']['JOINTS_0']).astype(np.int64)
    weights = accessor(j, binary, prim['attributes']['WEIGHTS_0'])
    ibm = np.transpose(accessor(j, binary, skin['inverseBindMatrices']).reshape(-1, 4, 4), (0, 2, 1))
    sj = skin['joints']
    name = {n: nodes[n].get('name', '') for n in sj}
    by_name = {v: k for k, v in name.items()}
    sw = np.stack([world(n) for n in sj])
    resid = np.abs(sw @ ibm - np.eye(4)).max()
    assert resid < BIND_TOL, 'not in bind pose at rest (%g)' % resid
    # posed delta (world) per skin joint: hips -90, knees +90 about +x, descendants inherit
    D = {}

    def delta(n):
        if n in D:
            return D[n]
        p = parent.get(n)
        d = delta(p) if p is not None else np.eye(4)
        nm = nodes[n].get('name', '')
        if nm in ('hip_left_joint', 'hip_right_joint'):
            d = rot_about_x(-90, world(n)[:3, 3]) @ d
        elif nm in ('knee_left_joint', 'knee_right_joint'):
            d = rot_about_x(90, (d @ world(n))[:3, 3]) @ d
        D[n] = d
        return d

    Dm = np.stack([delta(n) for n in sj])
    P = np.concatenate([pos, np.ones((len(pos), 1))], axis=1)
    mw_inv = np.linalg.inv(world(mesh_node))
    skinned = np.zeros((len(pos), 3))
    for k in range(4):
        M = Dm[joints[:, k]] @ sw[joints[:, k]] @ ibm[joints[:, k]] @ mw_inv
        skinned += weights[:, k:k + 1] * np.einsum('nij,nj->ni', M, P)[:, :3]
    pose = {}
    for s in ('left', 'right'):
        h, kn, an = (Dm[sj.index(by_name[x + s + '_joint'])] @ sw[sj.index(by_name[x + s + '_joint'])] for x in ('hip_', 'knee_', 'footAnkle_'))
        for lab, a, b in (('thigh_dir', h, kn), ('shank_dir', kn, an)):
            v = b[:3, 3] - a[:3, 3]
            pose.setdefault(lab, {})[s] = [round(float(x), 4) for x in v / np.linalg.norm(v)]
    assert abs(pose['thigh_dir']['left'][2] - 1) < 0.01 and pose['shank_dir']['left'][1] < -0.99, 'pose check failed %s' % pose
    dom = joints[np.arange(len(pos)), np.argmax(weights, axis=1)]
    dname = np.array([name[sj[d]] for d in dom])
    cand = np.isin(dname, sorted(SEAT_JOINTS))
    pel = by_name['pelvis_joint']
    pw = world(pel)
    pelpos, Rp = pw[:3, 3], pw[:3, :3]
    side_left = skinned[:, 0] - pelpos[0] > 0          # +x is subject-left
    res = {'sensitivity': {}, 'n': {}}
    for mm in patches:
        pts, counts = [], {}
        for lab, sel in (('left', side_left), ('right', ~side_left)):
            idx = np.where(cand & sel)[0]
            assert len(idx) > 0, 'no %s candidates' % lab
            y = skinned[idx, 1]
            patch = idx[y <= y.min() + mm / 1000.0]
            pts.append(skinned[patch].mean(axis=0))
            counts[lab] = int(len(patch))
        mid = (pts[0] + pts[1]) / 2.0
        loc = Rp.T @ (mid - pelpos)
        loc[2] = 0.0
        res['sensitivity'][mm] = loc
        res['n'][mm] = counts
    res.update(sha_dec=sha_dec, pose=pose, candidates=int(cand.sum()), vertices=int(len(pos)), resid=float(resid))
    return res


def r4(v):
    return [round(float(x), 4) + 0.0 for x in v]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--presets-dir', required=True)
    ap.add_argument('--measurement', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--patch-mm', type=int, default=10)
    a = ap.parse_args()
    meas = json.load(open(a.measurement))
    anchor = next(x for x in meas['registration']['anchors'] if x['name'] == 'pelvis')['provenance']
    pm = a.patch_mm
    patches = tuple(sorted(set(PATCHES) | {pm}))
    presets, failures = [], []
    for n in range(N_PRESETS):
        f = os.path.join(a.presets_dir, '%d_quest.glb' % n)
        try:
            sha_file = hashlib.sha256(open(f, 'rb').read()).hexdigest()
            r = measure_glb(f, patches)
            nat = r['sensitivity'][pm]
            assert np.all(np.isfinite(nat))
            presets.append({
                'preset': n, 'file': '%d_quest.glb' % n, 'sha256': sha_file, 'sha256_decompressed_glb': r['sha_dec'],
                'patch_mm': pm,
                'offset_native_bone_local_m': r4(nat),
                'offset_registered_canonical_m': r4(B @ nat),
                'sensitivity_native_bone_local_m': {str(m): r4(r['sensitivity'][m]) for m in PATCHES},
                'n_patch_vertices': {'left': r['n'][pm]['left'], 'right': r['n'][pm]['right']},
                'pose_check': r['pose'], 'candidate_vertices': r['candidates'], 'vertices': r['vertices'],
            })
        except Exception as e:  # loud, never defaulted
            failures.append({'preset': n, 'reason': '%s: %s' % (type(e).__name__, e)})
            print('FAILED preset %d: %s' % (n, e), file=sys.stderr)
    out = {
        'format': 'spatialblend.embodiment-sitbone-presets', 'schema_version': '1.0.0',
        'procedure': ('SEAT procedure of ovr-avatar.measurement.json registration.anchors[pelvis].provenance.procedure, applied to each of the 33 Quest presets '
                      '(tools/embodiment/measure_ovr_sitbone_presets.py); LOD00_combined_geometry, patch %d mm.' % pm),
        'units': 'metres', 'bone_frame': 'pelvis_joint (SDK Hips) LOCAL native frame, rest orientation: +x up, +y forward, +z left',
        'native_basis_rows': B.astype(int).tolist(),
        'source_measurement': 'ovr-avatar.measurement.json',
        'anchor': 'pelvis', 'bone': 'Hips',
        'presets': presets, 'failures': failures,
    }
    if presets:
        arr = np.array([p['offset_native_bone_local_m'] for p in presets])
        out['spread'] = {'min': r4(arr.min(0)), 'max': r4(arr.max(0)), 'mean': r4(arr.mean(0))}
    ok = not failures and len(presets) == N_PRESETS
    # self-check against the preset-0 artefact (reported; the output is written first so a mismatch is inspectable)
    open(a.out, 'w').write(json.dumps(out, indent=1, ensure_ascii=False) + '\n')
    assert ok, 'unmeasurable presets: %s' % [f['preset'] for f in failures]
    if pm == 10:
        reg = np.array(anchor['offset_native_bone_local_m'])
        d0 = np.abs(np.array(presets[0]['offset_native_bone_local_m']) - reg).max()
        print('preset 0 vs registered: max abs diff %.4f m' % d0)
        sp = anchor['cross_preset_spread_33_Quest_presets_10mm_bone_local']
        dmin = np.abs(np.array(out['spread']['min']) - sp['min']).max()
        dmax = np.abs(np.array(out['spread']['max']) - sp['max']).max()
        print('spread min diff %.4f, max diff %.4f' % (dmin, dmax))
        assert d0 <= 1e-3, 'preset 0 does not reproduce the registered offset'
        assert dmin <= 1e-3 and dmax <= 1e-3, 'spread differs from the artefact cross_preset_spread'
    print(json.dumps(out['spread']))


if __name__ == '__main__':
    main()
