#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Metrics of a remote avatar between two different room layouts: semantic target transfer and locomotion.

Two inputs, one metric implementation:
  * paired frames (JSONL written by NativeAvatarSemanticTransferTests): one record per frame holding the acting user's own
    poses ("src", their room) and the observer's view of that avatar ("rcv", the observer's room);
  * device logcat (AVATARLOG lines of AvatarPoseLogger) from the two headsets: `--device ACTOR.log OBSERVER.log`.
    The lines of the acting user's `local` avatar and of the observer's `remote` avatar are paired by time.

Targets are inferred from the acting user's own motion, so no marker is typed during a run:
  reach  = the acting hand within REACH_RADIUS_M of an object's touch point (hold >= REACH_HOLD_S)
  look   = the acting head's gaze ray crosses an object's footprint (hold >= LOOK_HOLD_S)
Every threshold is derived from numbers already in the app (the header carries them), none is invented here; the three
constants below are the ones the app code has and are cited:
  STREAM_INTERVAL_S 0.08      SbAvatarSpawnerFusion.avatarUpdateIntervalInSec
  MOVE_START_MPS 0.3, MOVE_STOP_MPS 0.1, YAW_START_DPS 10, YAW_STOP_DPS 3
                              SbNetworkAvatarEntity hysteresis (the source sends nothing below the stop thresholds)
  driver bounds               EmbodiedDriverConfig 2 m/s, 150 deg/s (header "limits")
Where the solver defines no relation (hand reach, crouch height), the numbers are REPORTED, never judged.
"""
import argparse
import json
import math
import re
import sys

STREAM_INTERVAL_S = 0.08
MOVE_START_MPS, MOVE_STOP_MPS = 0.3, 0.1
YAW_START_DPS, YAW_STOP_DPS = 10.0, 3.0
# Definition of "touching" / "looking" used to infer the task from the acting user's own motion.
REACH_RADIUS_M = 0.10
REACH_HOLD_S = 0.5
LOOK_HOLD_S = 1.0
EPS_M = 1e-3
TS_JITTER_S = 0.002   # log timestamps are whole milliseconds: two of them make a step's dt uncertain by 2 ms
# The driver steps by Time.deltaTime, which Unity smooths, while the log carries wall-clock time: one frame's step can differ from
# cap * (wall dt) by the smoothing. The cap is a RATE, so it is judged over a window several frames long.
RATE_WINDOW_S = 0.3


def sub(a, b): return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
def norm(a): return math.sqrt(sum(x * x for x in a))
def dist(a, b): return norm(sub(a, b))
def plan(a): return [a[0], a[2]]


def angle_deg(a, b):
    na, nb = norm(a), norm(b)
    if na < 1e-9 or nb < 1e-9: return 180.0
    c = max(-1.0, min(1.0, sum(x * y for x, y in zip(a, b)) / (na * nb)))
    return math.degrees(math.acos(c))


def ray_crosses(o, d, poly):
    n = math.hypot(d[0], d[1])
    if n < 1e-9: return False
    d = [d[0] / n, d[1] / n]
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        e = [b[0] - a[0], b[1] - a[1]]
        den = d[0] * e[1] - d[1] * e[0]
        if abs(den) < 1e-9: continue
        ao = [a[0] - o[0], a[1] - o[1]]
        t = (ao[0] * e[1] - ao[1] * e[0]) / den
        u = (ao[0] * d[1] - ao[1] * d[0]) / den
        if t >= 0 and 0 <= u <= 1: return True
    return False


def gaze_error_plan(head, fwd, poly):
    """0 if the gaze ray crosses the footprint, else the least plan-view angle to a footprint vertex."""
    o, d = plan(head), plan(fwd)
    if ray_crosses(o, d, poly): return 0.0
    best = 180.0
    for p in poly:
        v = [p[0] - o[0], p[1] - o[1]]
        best = min(best, angle_deg([d[0], 0, d[1]], [v[0], 0, v[1]]))
    return best


def dist_point_poly(p, poly):
    best = 1e9
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        e = [b[0] - a[0], b[1] - a[1]]
        l2 = e[0] ** 2 + e[1] ** 2
        t = max(0.0, min(1.0, ((p[0] - a[0]) * e[0] + (p[1] - a[1]) * e[1]) / l2)) if l2 > 0 else 0.0
        best = min(best, math.hypot(p[0] - a[0] - e[0] * t, p[1] - a[1] - e[1] * t))
    return best


def yaw_of(fwd): return math.degrees(math.atan2(fwd[0], fwd[2]))
def dyaw(a, b): return (b - a + 180.0) % 360.0 - 180.0


def infer_events(frames, objects_src):
    """Runs of frames in which the acting user touches / looks at an object, from the acting user's own poses only."""
    events = []
    for kind in ("reach", "look"):
        for name, obj in objects_src.items():
            run = []
            for f in frames:
                s = f["src"]
                if kind == "reach":
                    hit = min(dist(s["rh"], obj["touch"]), dist(s["lh"], obj["touch"])) <= REACH_RADIUS_M
                else:
                    hit = ray_crosses(plan(s["head"]), plan(s["head_fwd"]), obj["poly"])
                if hit:
                    run.append(f)
                else:
                    _close(events, kind, name, run)
                    run = []
            _close(events, kind, name, run)
    return sorted(events, key=lambda e: e["t0"])


def _close(events, kind, name, run):
    if len(run) < 2: return
    need = REACH_HOLD_S if kind == "reach" else LOOK_HOLD_S
    if run[-1]["t"] - run[0]["t"] >= need:
        events.append({"kind": kind, "target": name, "t0": run[0]["t"], "t1": run[-1]["t"], "frames": run})


def semantic_metrics(frames, header):
    objs_s, objs_r = header["objects_src"], header["objects_rcv"]
    out = []
    for ev in infer_events(frames, objs_s):
        f = ev["frames"][(len(ev["frames"]) * 3) // 4]   # late in the hold: the observer has had time to settle; before the retraction
        so, ro = objs_s[ev["target"]], objs_r[ev["target"]]
        rec = {"event": ev["kind"], "target": ev["target"], "t0": round(ev["t0"], 3), "t1": round(ev["t1"], 3)}
        if ev["kind"] == "reach":
            hand_s = "rh" if dist(f["src"]["rh"], so["touch"]) <= dist(f["src"]["lh"], so["touch"]) else "lh"
            rec["source_hand_to_object_m"] = round(dist(f["src"][hand_s], so["touch"]), 3)
            rec["receiver_hand_to_counterpart_m"] = round(dist(f["rcv"][hand_s], ro["touch"]), 3)
            rec["source_body_to_footprint_m"] = round(dist_point_poly(plan(f["src"]["head"]), so["poly"]), 3)
            rec["receiver_body_to_footprint_m"] = round(dist_point_poly(plan(f["rcv"]["head"]), ro["poly"]), 3)
        rec["receiver_gaze_error_plan_deg"] = round(gaze_error_plan(f["rcv"]["head"], f["rcv"]["head_fwd"], ro["poly"]), 2)
        rec["receiver_gaze_error_3d_deg"] = round(angle_deg(f["rcv"]["head_fwd"], sub(ro["touch"], f["rcv"]["head"])), 2)
        rec["source_gaze_error_plan_deg"] = round(gaze_error_plan(f["src"]["head"], f["src"]["head_fwd"], so["poly"]), 2)
        out.append(rec)
    return out


def locomotion_metrics(frames, header):
    lim = header.get("limits", {"max_linear_mps": 2.0, "max_angular_dps": 150.0})
    steps, turns, jumps, stalls, speeds = [], [], [], [], []
    last_change_t, stall_run, move_since = frames[0]["t"], None, None
    stall_s = 2 * STREAM_INTERVAL_S
    first_move = None
    # Step bounds are judged on the observer's OWN samples (each with its own time): pairing a 20 Hz source log with a 60 Hz observer
    # log must not make two observer frames look like one.
    own = []
    for f in frames:
        rt = f.get("rt", f["t"])
        if not own or rt > own[-1][0]: own.append((rt, f))
    for (pt, p), (ct, c) in zip(own, own[1:]):
        dt = ct - pt
        if dt <= 0: continue
        d = math.hypot(c["rcv"]["head"][0] - p["rcv"]["head"][0], c["rcv"]["head"][2] - p["rcv"]["head"][2])
        y = abs(dyaw(yaw_of(p["rcv"]["head_fwd"]), yaw_of(c["rcv"]["head_fwd"])))
        steps.append(d); turns.append(y); speeds.append(d / dt)
        if first_move is None and d > EPS_M: first_move = pt
    # A jump is a window in which the body covers more than the driver's bound allows. The first move is the join placement (a
    # teleport by design). Single-frame steps are reported separately as max_body_step_m.
    n = len(own)
    for i in range(n):
        if first_move is None or own[i][0] <= first_move: continue
        j = i
        while j + 1 < n and own[j][0] - own[i][0] < RATE_WINDOW_S: j += 1
        w = own[j][0] - own[i][0]
        if w < RATE_WINDOW_S: break
        pi, pj = own[i][1]["rcv"], own[j][1]["rcv"]
        d = math.hypot(pj["head"][0] - pi["head"][0], pj["head"][2] - pi["head"][2])
        yaw = sum(abs(dyaw(yaw_of(own[k][1]["rcv"]["head_fwd"]), yaw_of(own[k + 1][1]["rcv"]["head_fwd"]))) for k in range(i, j))
        if d > lim["max_linear_mps"] * (w + TS_JITTER_S) + EPS_M or yaw > lim["max_angular_dps"] * (w + TS_JITTER_S) + 0.1:
            if not jumps or own[i][0] > jumps[-1]["t1"]:
                jumps.append({"t": round(own[i][0], 3), "t1": round(own[j][0], 3), "path_m": round(d, 3), "yaw_deg": round(yaw, 1)})
            else:
                jumps[-1]["t1"] = round(own[j][0], 3); jumps[-1]["path_m"] = max(jumps[-1]["path_m"], round(d, 3)); jumps[-1]["yaw_deg"] = max(jumps[-1]["yaw_deg"], round(yaw, 1))
    for p, c in zip(frames, frames[1:]):
        dt = c["t"] - p["t"]
        if dt <= 0: continue
        d = math.hypot(c["rcv"]["head"][0] - p["rcv"]["head"][0], c["rcv"]["head"][2] - p["rcv"]["head"][2])
        y = abs(dyaw(yaw_of(p["rcv"]["head_fwd"]), yaw_of(c["rcv"]["head_fwd"])))
        src_speed = math.hypot(c["src"]["root"][0] - p["src"]["root"][0], c["src"]["root"][2] - p["src"]["root"][2]) / dt
        src_yaw = abs(dyaw(yaw_of(p["src"]["head_fwd"]), yaw_of(c["src"]["head_fwd"]))) / dt
        src_moving = src_speed > MOVE_STOP_MPS or src_yaw > YAW_STOP_DPS
        if not src_moving: move_since = None
        elif move_since is None: move_since = p["t"]
        if d > EPS_M or y > 0.05:
            last_change_t = c["t"]; stall_run = None
        elif move_since is not None and c["t"] - max(last_change_t, move_since) > stall_s:
            # The observer's avatar did not move for longer than two stream intervals while the actor was moving.
            if stall_run is None:
                stall_run = {"t0": round(max(last_change_t, move_since), 3), "t1": round(c["t"], 3)}; stalls.append(stall_run)
            else:
                stall_run["t1"] = round(c["t"], 3)
    # Receiver update rate: distinct receiver head poses per second of the time the acting user is moving.
    changes, moving_time = 0, 0.0
    for p, c in zip(frames, frames[1:]):
        dt = c["t"] - p["t"]
        if dt <= 0: continue
        sp = math.hypot(c["src"]["root"][0] - p["src"]["root"][0], c["src"]["root"][2] - p["src"]["root"][2]) / dt
        sy = abs(dyaw(yaw_of(p["src"]["head_fwd"]), yaw_of(c["src"]["head_fwd"]))) / dt
        if sp > MOVE_STOP_MPS or sy > YAW_STOP_DPS:
            moving_time += dt
            if dist(p["rcv"]["head"], c["rcv"]["head"]) > EPS_M or abs(dyaw(yaw_of(p["rcv"]["head_fwd"]), yaw_of(c["rcv"]["head_fwd"]))) > 0.05:
                changes += 1
    span = moving_time
    # Target jumps: the solver's own proposal moving farther in one frame than the driver can follow. The driver bounds hide it
    # (the body walks there at the cap), so it is counted on the proposal, with the core's cause bits when the log has them.
    target_jumps = []
    for p, c in zip(frames, frames[1:]):
        dt = c["t"] - p["t"]
        if dt <= 0 or "proposal" not in p or "proposal" not in c: continue
        d = math.hypot(c["proposal"]["pos"][0] - p["proposal"]["pos"][0], c["proposal"]["pos"][2] - p["proposal"]["pos"][2])
        if d > lim["max_linear_mps"] * dt + EPS_M:
            target_jumps.append({"t": round(c["t"], 3), "jump_m": round(d, 2), "cause": c["proposal"].get("cause")})
    # Latency: from each onset of the acting user's motion (>= start threshold) to the first receiver movement.
    lat = []
    moving = False
    for p, c in zip(frames, frames[1:]):
        dt = c["t"] - p["t"]
        if dt <= 0: continue
        sp = math.hypot(c["src"]["root"][0] - p["src"]["root"][0], c["src"]["root"][2] - p["src"]["root"][2]) / dt
        if not moving and sp >= MOVE_START_MPS:
            moving = True
            for q, r in zip(frames, frames[1:]):
                if r["t"] >= c["t"] and dist(q["rcv"]["head"], r["rcv"]["head"]) > EPS_M:
                    lat.append(round(r["t"] - c["t"], 3)); break
        elif moving and sp < MOVE_STOP_MPS:
            moving = False
    return {
        "max_body_step_m": round(max(steps, default=0), 4), "max_yaw_step_deg": round(max(turns, default=0), 3),
        "max_speed_mps": round(max(speeds, default=0), 3), "driver_bound_mps": lim["max_linear_mps"],
        "jumps_over_driver_bound": jumps, "proposal_jumps_over_driver_bound": target_jumps, "stalls_longer_than_s": stall_s, "stalls": stalls,
        "receiver_updates_per_s": round(changes / span, 2) if span > 0 else 0,
        "motion_onset_to_receiver_motion_s": lat,
    }


def analyze_paired(path):
    header, frames = None, []
    for line in open(path):
        r = json.loads(line)
        if r.get("kind") == "header": header = r
        elif r.get("kind") == "frame": frames.append(r)
    if header is None or not frames: sys.exit("no header/frames in " + path)
    return {"direction": header["dir"], "frames": len(frames), "semantic": semantic_metrics(frames, header),
            "locomotion": locomotion_metrics(frames, header)}


# ---- device logs ----------------------------------------------------------------------------------------------------

LOG = re.compile(r"AVATARLOG (\{.*\})")


def read_device(path):
    objects, local, remote = None, [], []
    for line in open(path, errors="replace"):
        m = LOG.search(line)
        if not m: continue
        try: r = json.loads(m.group(1))
        except ValueError: continue
        if r.get("kind") == "objects": objects = r["objects"]
        elif r.get("kind") == "pose": (local if r["who"] == "local" else remote).append(r)
    return objects, local, remote


def pair_device(actor_path, observer_path, offset_ms=0.0):
    """Pair the actor's local avatar with the observer's remote avatar by time. offset_ms = observer clock - actor clock."""
    obj_a, local_a, _ = read_device(actor_path)
    obj_b, _, remote_b = read_device(observer_path)
    if not (obj_a and obj_b and local_a and remote_b): sys.exit("device logs lack objects/poses")
    t0 = local_a[0]["ts_ms"]
    frames = []
    j = 0
    for r in local_a:
        # The observer's latest pose at or before the observer-clock time of this sample: latency stays visible, never hidden.
        tb = r["ts_ms"] + offset_ms
        while j + 1 < len(remote_b) and remote_b[j + 1]["ts_ms"] <= tb: j += 1
        if remote_b[j]["ts_ms"] > tb: continue
        frames.append({"t": (r["ts_ms"] - t0) / 1000.0, "rt": (remote_b[j]["ts_ms"] - offset_ms - t0) / 1000.0,
                       "src": {k: r[k] for k in ("root", "head", "head_fwd", "lh", "rh")},
                       "rcv": {k: remote_b[j][k] for k in ("head", "head_fwd", "lh", "rh")}})
    header = {"dir": "device", "objects_src": obj_a, "objects_rcv": obj_b, "limits": {"max_linear_mps": 2.0, "max_angular_dps": 150.0}}
    return header, frames


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("frames", nargs="*", help="paired frames JSONL (from the automated scenario)")
    ap.add_argument("--device", nargs=2, metavar=("ACTOR_LOG", "OBSERVER_LOG"))
    ap.add_argument("--offset-ms", type=float, default=0.0, help="observer clock minus actor clock")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    results = []
    for p in a.frames: results.append(analyze_paired(p))
    if a.device:
        header, frames = pair_device(a.device[0], a.device[1], a.offset_ms)
        results.append({"direction": "device", "frames": len(frames), "semantic": semantic_metrics(frames, header),
                        "locomotion": locomotion_metrics(frames, header)})
    if a.json:
        print(json.dumps(results, indent=1)); return
    for r in results:
        print(f"== {r['direction']}  frames={r['frames']}")
        L = r["locomotion"]
        print(f"  locomotion: max step {L['max_body_step_m']} m/frame, max speed {L['max_speed_mps']} m/s (bound {L['driver_bound_mps']}), "
              f"body jumps over bound {len(L['jumps_over_driver_bound'])}, solver target jumps over bound {len(L['proposal_jumps_over_driver_bound'])}, stalls > {L['stalls_longer_than_s']} s: {len(L['stalls'])}, "
              f"receiver update rate {L['receiver_updates_per_s']}/s, onset latency {L['motion_onset_to_receiver_motion_s']}")
        for s in r["semantic"]:
            print("  " + ", ".join(f"{k}={v}" for k, v in s.items()))


if __name__ == "__main__":
    main()
