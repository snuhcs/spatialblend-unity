#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Regenerate the Meta/OVR avatar embodiment-registration data from the preset avatar GLB (lane G, embodiment registration).

usage: python3 -I tools/embodiment/ovr_registration_artefacts.py --glb 0_quest.glb --measurement MEASUREMENT.json --out-dir DIR [--body BODY.json] [--check]

Inputs
  --glb           a Meta Avatars SDK preset avatar (PresetAvatars_Quest/0_quest.glb; decompressed, or zstd-compressed with the
                  `zstandard` module installed). It is read only and is NEVER copied into the repository (Meta SDK licence:
                  SDK content is not redistributed).
  --measurement   the measurement artefact (ovr-avatar.measurement.json): its `anchors[].offset_position` are the MEASURED numbers in
                  the bone's native local frame (procedure and asset sha256 recorded there). Everything else is derived below.

  --body          optional: the `body` block (floor-footprint radius with provenance) written by tools/embodiment/measure_ovr_body_footprint.py.
                  It is copied into the measurement artefact's registration block, where the Unity loader reads it (core spec section 13).

Outputs (in --out-dir)
  ovr-avatar.embodiment.json   the structure in the core's schema (skeleton: the 64 joints of ovrAvatar2JointType and their parents;
                               anchors, bones, families). No body number.
  ovr-avatar.chain.json        each joint's rest position + rotation in the core's canonical axes, rest_pose "A", provenance, no limits.
  ovr-avatar.measurement.json  the input artefact plus a `registration` block: each anchor's offset in the registered bone frame.

Derivation (checked numerically by --check and at every run)
  Native frame: glTF, right-handed, +x left, +y up, +z forward. Canonical: z up, x forward, y left. c = B n with B rows
  (0,0,1; 1,0,0; 0,1,0) (the same B is `native_basis_rows` in both artefacts).
  A joint's rest transform is its glTF node world transform (translation, rotation, scale composed down the tree; the rotation is the
  polar factor of the 3x3). Canonical rest: p_c = B (p_n - p_n(Root)), R_c = B R_n B^T. The joint frame is the asset's native bone frame
  carried into the canonical axes: canonical local coordinates are B times the native local coordinates.
  Offset: the measured offset_n is a point in the bone's native local frame. Its canonical-local coordinates are offset_c = B offset_n;
  check: R_c offset_c = B R_n B^T B offset_n = B (R_n offset_n), the canonical image of the native world displacement.
  Anchor frame: the core reads an anchor's yaw/pitch/roll from the rotation of `bone * offset`, with x forward at yaw 0, but a bone's
  carried frame is not body-aligned (the Hips and Head rest quaternions are 120 degrees about (1,1,1)). offset.rotation = R_c_rest^-1, so
  the anchor frame at rest is the canonical axes and the anchor's yaw is the body's heading.
  Hand anchors: the palm offsets were measured with the arm raised rigidly at the shoulder, forearm forward (+z), palm down (-y), not in the
  A rest pose. Their anchor frame is the body-heading frame at THAT pose: offset.rotation = (B R_n_meas B^T)^-1, R_n_meas the wrist's world
  rotation after the measurement pose (re-derived here exactly as the measurement tool did: minimal rotation of the elbow->wrist direction onto +z,
  then a roll about +z putting the palm normal on -y). Head and Hips were measured at their rest orientation (rest_pose below).
  The driver must publish bone rotation R_c = B R_n B^T where R_n is the bone's current world rotation in the native frame, i.e.
  R_c = (B Delta B^T) R_c_rest with Delta = R_n R_n_rest^-1 the world-frame delta.

Joint names/parents: the table below is the MotionGen lane's (spatialblend-motion-generator tools/anim-reference/ovr_skeleton_from_glb.py),
the SDK enum order of ovrAvatar2JointType (Scripts/CAPI/OvrAvatarAPI_Types.cs, 64 joints); the parent of an enum joint is its nearest
ancestor in the asset that is itself an enum joint (identical in 132 preset avatars, lane C).
"""
import argparse, copy, hashlib, json, os, struct, sys
import numpy as np

B = np.array([[0, 0, 1], [1, 0, 0], [0, 1, 0]], float)   # native -> canonical, c = B n
BASIS_ROWS = [0, 0, 1, 1, 0, 0, 0, 1, 0]

ENUM = ["Root", "Hips", "LeftLegUpper", "LeftLegLower", "LeftFootAnkle", "LeftFootBall", "RightLegUpper", "RightLegLower",
        "RightFootAnkle", "RightFootBall", "SpineLower", "SpineMiddle", "SpineUpper", "Chest", "Neck", "Head", "LeftShoulder",
        "LeftArmUpper", "LeftArmLower", "LeftHandWrist", "RightShoulder", "RightArmUpper", "RightArmLower", "RightHandWrist"]
FINGERS = [("Thumb", ["Trapezium", "Meta", "Proximal", "Distal"], ["Meta", "00", "01", "02"]),
           ("Index", ["Meta", "Proximal", "Intermediate", "Distal"], ["Meta", "00", "01", "02"]),
           ("Middle", ["Meta", "Proximal", "Intermediate", "Distal"], ["Meta", "00", "01", "02"]),
           ("Ring", ["Meta", "Proximal", "Intermediate", "Distal"], ["Meta", "00", "01", "02"]),
           ("Pinky", ["Meta", "Proximal", "Intermediate", "Distal"], ["Meta", "00", "01", "02"])]
ORDER = ENUM + ["%sHand%s%s" % (s, f, e) for s in ("Left", "Right") for f, enums, _ in FINGERS for e in enums]
assert len(ORDER) == 64

# anchor -> (bone, families, measured anchor name in the measurement artefact or None for a pure frame alignment)
ANCHORS = [("root", None, ["Spacing"], None),
           ("head", "Head", ["Facing"], None),
           ("pelvis", "Hips", ["AffordancePose"], "pelvis"),
           ("hand_l", "LeftHandWrist", ["AffordancePose"], "hand_l"),
           ("hand_r", "RightHandWrist", ["AffordancePose"], "hand_r")]


# Anchors the entity reads and writes through its own tracked-input channel (the Meta avatar's retarget head), which is not a bone: the
# loader registers them as legacy anchors (no bone, no offset), so a bone sampled in one frame and a target written to an input in
# another never close a loop. The measurement artefact still carries their frame-alignment entry.
ENTITY_INPUT = ("head",)


def asset_name(enum_name):
    fixed = {"Root": "root_joint", "Hips": "pelvis_joint", "SpineLower": "spineLower_joint", "SpineMiddle": "spineMiddle_joint",
             "SpineUpper": "spineUpper_joint", "Chest": "chest_joint", "Neck": "neck_joint", "Head": "head_joint"}
    if enum_name in fixed:
        return fixed[enum_name]
    for S, s in (("Left", "left"), ("Right", "right")):
        if enum_name.startswith(S):
            r = enum_name[len(S):]
            body = {"LegUpper": "hip", "LegLower": "knee", "FootAnkle": "footAnkle", "FootBall": "footBall", "Shoulder": "clavicle",
                    "ArmUpper": "shoulder", "ArmLower": "elbow", "HandWrist": "handWrist"}
            if r in body:
                return "%s_%s_joint" % (body[r], s)
            for f, enums, assets in FINGERS:
                if r.startswith("Hand" + f):
                    a = assets[enums.index(r[len("Hand" + f):])]
                    return "hand%s%s_%s_joint" % (f, "Meta" if a == "Meta" else "_" + a, s)
    raise KeyError(enum_name)


def load_glb(path):
    raw = open(path, 'rb').read()
    if raw[:4] == b"\x28\xb5\x2f\xfd":
        import zstandard
        raw = zstandard.ZstdDecompressor().stream_reader(open(path, 'rb')).read()
    assert raw[:4] == b'glTF'
    off, j = 12, None
    while off < len(raw):
        length, kind = struct.unpack('<II', raw[off:off + 8])
        if kind == 0x4E4F534A:
            j = json.loads(raw[off + 8:off + 8 + length])
        off += 8 + length
    return j, hashlib.sha256(raw).hexdigest()


def quat_to_mat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def mat_to_quat(R):
    t = np.trace(R)
    if t > 0:
        s = np.sqrt(t + 1) * 2; w = s / 4; x = (R[2, 1] - R[1, 2]) / s; y = (R[0, 2] - R[2, 0]) / s; z = (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = np.sqrt(1 + R[0, 0] - R[1, 1] - R[2, 2]) * 2; w = (R[2, 1] - R[1, 2]) / s; x = s / 4; y = (R[0, 1] + R[1, 0]) / s; z = (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = np.sqrt(1 + R[1, 1] - R[0, 0] - R[2, 2]) * 2; w = (R[0, 2] - R[2, 0]) / s; x = (R[0, 1] + R[1, 0]) / s; y = s / 4; z = (R[1, 2] + R[2, 1]) / s
    else:
        s = np.sqrt(1 + R[2, 2] - R[0, 0] - R[1, 1]) * 2; w = (R[1, 0] - R[0, 1]) / s; x = (R[0, 2] + R[2, 0]) / s; y = (R[1, 2] + R[2, 1]) / s; z = s / 4
    q = np.array([x, y, z, w]); q /= np.linalg.norm(q)
    return q if q[3] >= 0 else -q


def polar_rotation(M):
    u, _s, vt = np.linalg.svd(M); R = u @ vt
    assert np.linalg.det(R) > 0, 'improper rotation'
    return R


def rest_data(j):
    nodes = j['nodes']
    parent = {c: i for i, n in enumerate(nodes) for c in n.get('children', [])}
    cache = {}

    def world(i):
        if i in cache: return cache[i]
        n = nodes[i]
        M = np.eye(4)
        M[:3, :3] = quat_to_mat(n.get('rotation', [0, 0, 0, 1])) * np.array(n.get('scale', [1, 1, 1]))[None, :]
        M[:3, 3] = n.get('translation', [0, 0, 0])
        cache[i] = (world(parent[i]) @ M) if i in parent else M
        return cache[i]

    by_name = {n['name']: i for i, n in enumerate(nodes)}
    idx = {n: by_name[asset_name(n)] for n in ORDER}
    enum_of = {v: k for k, v in idx.items()}
    parents = []
    for n in ORDER:
        a = parent.get(idx[n])
        while a is not None and a not in enum_of:
            a = parent.get(a)
        parents.append(-1 if a is None else ORDER.index(enum_of[a]))
    assert all(p < i for i, p in enumerate(parents))
    root_p = world(idx[ORDER[0]])[:3, 3].copy()
    assert not np.any(root_p), 'the first root must be at the origin'
    rest = {}
    for n in ORDER:
        M = world(idx[n])
        Rn = polar_rotation(M[:3, :3])
        rest[n] = (M[:3, 3] - root_p, Rn, B @ (M[:3, 3] - root_p), B @ Rn @ B.T)
    return parents, rest, (lambda asset: world(by_name[asset]))


def f(v): return [float(x) for x in v]

def rot_axis(a, th):
    a = np.asarray(a, float); a = a / np.linalg.norm(a)
    K = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) + np.sin(th) * K + (1 - np.cos(th)) * K @ K


def rot_between(u, v):
    u = u / np.linalg.norm(u); v = v / np.linalg.norm(v)
    ax = np.cross(u, v); sn = np.linalg.norm(ax)
    return np.eye(3) if sn < 1e-12 else rot_axis(ax, np.arctan2(sn, u @ v))


def measurement_pose_wrist(world_of, side):
    """The wrist's NATIVE world rotation after the palm measurement pose, and the pose's residuals (as the measurement tool computes them)."""
    pos = lambda n: world_of("%s_%s_joint" % (n, side))[:3, 3]
    wr, el = pos('handWrist'), pos('elbow')
    Zd = np.array([0., 0, 1])
    R1 = rot_between(wr - el, Zd)
    mid, i0, pk = pos('handMiddle_00'), pos('handIndex_00'), pos('handPinky_00')
    n = np.cross(i0 - pk, mid - wr); n /= np.linalg.norm(n)
    if n[0] * (-1.0 if side == 'left' else 1.0) < 0: n = -n          # palm side is medial
    n1 = R1 @ n
    R = rot_axis(Zd, -np.pi / 2 - np.arctan2(n1[1], n1[0])) @ R1      # roll about +z: palm normal onto -y
    rn = polar_rotation(world_of("handWrist_%s_joint" % side)[:3, :3])
    resid = {"forearm_dir": f(R @ (wr - el) / np.linalg.norm(wr - el)), "palm_normal": f(R @ n)}
    return R @ rn, resid



def build(glb_path, meas_path, body_path=None):
    j, sha_dec = load_glb(glb_path)
    meas = json.load(open(meas_path))
    assert sha_dec == meas['asset']['sha256_decompressed_glb'], 'the GLB is not the asset the measurement was taken on'
    asset_sha = meas['asset']['sha256']
    parents, rest, world_of = rest_data(j)
    conv = ("c = B n with B rows (0,0,1; 1,0,0; 0,1,0) (glTF/OVR preset avatars, right-handed, "
            "x left, y up, z forward -> core canonical z up, x forward, y left). Positions p_c = B (p_n - p_n(Root)); rotations R_c = B R_n B^T "
            "(a proper rotation; the joint frame is the asset's native bone frame carried into the canonical axes, i.e. canonical local "
            "coordinates are B times the native local coordinates). p_n and R_n are the joint's node world transform from the GLB node hierarchy "
            "(translation, rotation, scale composed down the tree); R_n is the rotation after removing scale (polar factor; scales are within 1e-6 of 1).")
    joints = [{"name": n, "rest": {"position": f(rest[n][2]), "rotation": f(mat_to_quat(rest[n][3]))}} for n in ORDER]
    chain = {"format": "spatialblend.embodiment-chain-measurement", "schema_version": "1.0.0", "native_basis_rows": BASIS_ROWS,
             "chain": {"rest_pose": "A",
                       "provenance": {
                           "procedure": ("Rest (bind) pose of the Meta Avatars SDK preset avatar read from the GLB node hierarchy: the 64 joints of "
                                         "ovrAvatar2JointType in enum order, parent = nearest ancestor that is itself one of the 64 (identical parent table in 132 preset "
                                         "avatars, lane C); world rest transform of each joint node (checked: W_rest * inverseBindMatrix = I to 1e-6 on all 148 skin joints, "
                                         "measurement artefact bone_frame). " + conv + " Rest pose label A: the measured arms hang ~47 deg below horizontal "
                                         "(measurement artefact assumptions). No joint limits: the SDK declares none. Script: tools/embodiment/ovr_registration_artefacts.py."),
                           "asset": meas['asset']['name'], "asset_file": meas['asset']['file'], "asset_sha256": asset_sha,
                           "asset_sha256_decompressed_glb": sha_dec, "sdk_package": meas['asset']['sdk_package'],
                           "sdk_version": meas['asset']['sdk_version'], "measured_on": meas['measured_on'],
                           "licence_note": "numbers derived from a Meta SDK content file; whether they may be recorded is an open question"},
                       "joints": joints}}
    structural = {"format": "spatialblend.embodiment", "schema_version": "1.0.0", "id": "ovr-avatar-preset", "units": "m",
                  "frame": "z-up, right-handed",
                  "bone_frame": ("Each bone's frame is the Meta avatar asset's native bone frame carried into the core's canonical axes: canonical local "
                                 "coordinates are B times the native local coordinates, B rows (0,0,1; 1,0,0; 0,1,0) (glTF x left, y up, z forward -> x forward, y left, "
                                 "z up). The published bone rotation is B R_n B^T, R_n the bone's world rotation in the asset's native frame (rest rotations: "
                                 "ovr-avatar.chain.json). Anchor offsets carry a rotation that aligns the anchor frame with the canonical axes at rest. "
                                 "Joint names are ovrAvatar2JointType."),
                  "skeleton": {"joints": [{"name": n, "parent": p} for n, p in zip(ORDER, parents)]},
                  "anchors": []}
    reg = []
    by_name = {a['name']: a for a in meas['anchors']}
    CAVEATS = ("Measured on preset 0_quest.glb only; the seat offset varies up -0.165..-0.288 m and forward -0.011..+0.182 m across the 33 Quest presets; "
               "weak proxy (clothed mesh, no ischial anatomy); the palm points are within 2 mm across presets.")
    for name, bone, fams, mname in ANCHORS:
        a = {"name": name, "bone": bone, "families": fams}
        if bone is not None:
            a["offset_from"] = "measurement"
            _pn, _Rn, _pc, Rc = rest[bone]
            ref_name, Rref = "rest", Rc
            if mname in ('hand_l', 'hand_r'):
                Rn_meas, resid = measurement_pose_wrist(world_of, 'left' if mname == 'hand_l' else 'right')
                Rref = B @ Rn_meas @ B.T
                ref_name = "measurement"
                assert abs(resid['forearm_dir'][2] - 1) < 1e-6 and resid['palm_normal'][1] < -0.99, resid
            q_off = mat_to_quat(Rref.T)
            if mname is None:
                pos = np.zeros(3)
                proc = ("Frame alignment only, no body measurement: offset.position is zero (the head anchor IS the Head joint) and offset.rotation is the "
                        "inverse of the Head joint's canonical rest rotation (ovr-avatar.chain.json), so that the anchor frame equals the model-space canonical "
                        "axes (x forward, y left, z up) at rest and the core's yaw/pitch/roll of bone * offset is the body's heading. The Head joint's own frame is "
                        "not body-aligned (rest quaternion x,y,z,w = " + ", ".join("%.6f" % v for v in mat_to_quat(Rc)) + ").")
                extra = {}
            else:
                native = np.array(by_name[mname]['offset_position'], float)
                pos = B @ native
                proc = (meas['procedure'].split(' | REGISTRATION:')[0] +
                        " REGISTRATION CONVERSION: the measured offset_position is in the bone's NATIVE local frame; the registered offset is in the carried "
                        "canonical bone frame, offset_c = B * offset_n with B rows (0,0,1; 1,0,0; 0,1,0). Check: the world displacement R_c * offset_c = "
                        "B R_n B^T B offset_n = B (R_n offset_n). offset.rotation = inverse of the bone's canonical rest rotation, so the anchor frame is the "
                        "canonical axes at the reference pose (needed because the core reads the anchor's yaw from x of bone*offset and the bone frame is not body-aligned). "
                        "For the HAND anchors the reference pose is the MEASUREMENT pose, not the A rest pose: the arm was raised rigidly at the shoulder, forearm "
                        "forward, palm down, and offset.rotation = (B R_n_meas B^T)^-1 with R_n_meas the wrist's world rotation after that pose (re-derived by this script as the "
                        "measurement tool did), so the hand anchor frame is the body-heading frame when the hand is held as measured.")
                src = by_name[mname]
                extra = {"offset_native_bone_local_m": src['offset_position'], "patch_mm": src['patch_mm'],
                         "sensitivity_native_bone_local_m": src['sensitivity'], "n_patch_vertices_10mm": src['n_patch_vertices_10mm'],
                         "cross_preset_spread_33_Quest_presets_10mm_bone_local": src['cross_preset_spread_33_Quest_presets_10mm_bone_local']}
                if name == 'pelvis':
                    extra["caveat"] = ("WEAK SEAT: clothed-surface lowest-patch proxy, no ischial anatomy; the 'forward' term varies -0.011..+0.182 m across 33 presets "
                                       "(see assumptions); 'up' is the more stable term.")
            reg.append({"name": name, "bone": bone, "offset": {"position": f(pos), "rotation": f(q_off)},
                        "provenance": dict({"procedure": proc, "asset": meas['asset']['name'], "asset_sha256": asset_sha,
                                            "asset_sha256_decompressed_glb": sha_dec, "source": "ovr-avatar.measurement.json",
                                            "measured_on": meas['measured_on'], "caveats": CAVEATS,
                                            "reference_pose": ref_name, "reference_bone_rotation_canonical": f(mat_to_quat(Rref))}, **extra)})
        if name in ENTITY_INPUT:
            # Registered as a legacy anchor by the loader; its structure member names the channel, not a measurement.
            a.pop("offset_from", None)
            a["driven_by"] = "entity_input"
        structural["anchors"].append(a)
    reg_proc = ("Offsets are registered in the bone frame the driver publishes: the asset's native bone frame carried into the core's canonical axes with B rows "
                "(0,0,1; 1,0,0; 0,1,0) (c = B n). Position offset_c = B * offset_native (from anchors[].offset_position, bone-local native). Rotation offset = "
                "inverse of the bone's canonical reference-pose rotation (head, pelvis: the rest rotation of ovr-avatar.chain.json; hands: the wrist's rotation at the palm measurement pose, see anchors[].provenance), so that the anchor frame at the reference pose is the canonical axes. The driver "
                "must publish bone rotation = B R_n B^T where R_n is the bone's current world rotation in the asset's native frame (= the canonical rest rotation "
                "composed with the animated delta: R_c = (B Delta B^T) R_c_rest, Delta = R_n R_n_rest^-1 world-frame delta). Self-check: tools/embodiment/"
                "ovr_registration_artefacts.py (composed anchor displacement equals B times the native displacement for random pose deltas, max error < 1e-12 m; "
                "anchor frame at rest equals the identity).")
    out_meas = copy.deepcopy(meas)
    out_meas['procedure'] = meas['procedure'].split(' | REGISTRATION:')[0] + " | REGISTRATION: " + reg_proc
    out_meas['registration'] = {"native_basis_rows": BASIS_ROWS, "procedure": reg_proc, "anchors": reg}
    out_meas.pop('body', None)
    if body_path:
        body = json.load(open(body_path))
        assert body['floor_footprint_radius_m'] > 0 and body['provenance']['asset_sha256'] == asset_sha, 'the body block is not for this asset'
        out_meas['registration']['body'] = body
    return structural, chain, out_meas, rest, by_name


def self_check(rest, reg_anchors, by_name):
    rng = np.random.default_rng(7)
    worst, worst_rest = 0.0, 0.0
    for a in reg_anchors:
        pn, Rn, pc, Rc = rest[a['bone']]
        q = a['offset']['rotation']
        Rref = quat_to_mat(a['provenance']['reference_bone_rotation_canonical'])
        if a['provenance']['reference_pose'] == 'rest':
            assert np.abs(Rref - Rc).max() < 1e-12
        worst_rest = max(worst_rest, np.abs(Rref @ quat_to_mat(q) - np.eye(3)).max())
        mname = {'pelvis': 'pelvis', 'hand_l': 'hand_l', 'hand_r': 'hand_r'}.get(a['name'])
        if not mname: continue
        for _ in range(8):
            Q, _r = np.linalg.qr(rng.normal(size=(3, 3))); Q *= np.sign(np.linalg.det(Q))
            Rn_cur = Q @ Rn
            disp_native = Rn_cur @ np.array(by_name[mname]['offset_position'], float)
            worst = max(worst, np.abs((B @ Rn_cur @ B.T) @ np.array(a['offset']['position']) - B @ disp_native).max())
    assert worst < 1e-12 and worst_rest < 1e-12, (worst, worst_rest)
    return worst, worst_rest


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--glb', required=True); ap.add_argument('--measurement', required=True)
    ap.add_argument('--out-dir', required=True); ap.add_argument('--check', action='store_true')
    ap.add_argument('--body')
    args = ap.parse_args()
    structural, chain, out_meas, rest, by_name = build(args.glb, args.measurement, args.body)
    worst, worst_rest = self_check(rest, out_meas['registration']['anchors'], by_name)
    print('self-check: displacement error %.2e m, rest anchor-frame error %.2e' % (worst, worst_rest))
    if args.check:
        return
    os.makedirs(args.out_dir, exist_ok=True)
    for name, doc in (('ovr-avatar.embodiment.json', structural), ('ovr-avatar.chain.json', chain), ('ovr-avatar.measurement.json', out_meas)):
        with open(os.path.join(args.out_dir, name), 'w') as fh:
            json.dump(doc, fh, indent=1, ensure_ascii=False); fh.write('\n')
    print('wrote', args.out_dir)


if __name__ == '__main__':
    main()
