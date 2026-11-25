# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Synthetic checks of analyze_avatar_log.py: a perfect mapping scores zero error, a teleport and a stall are found."""
import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location("analyze", pathlib.Path(__file__).parent / "avatar-multiuser" / "analyze_avatar_log.py")
an = importlib.util.module_from_spec(spec)
spec.loader.exec_module(an)

POLY = [[2.0, 3.0], [3.0, 3.0], [3.0, 4.0], [2.0, 4.0]]
OBJ = {"table": {"poly": POLY, "touch": [2.5, 0.75, 3.5]}}
HEADER = {"dir": "t", "objects_src": OBJ, "objects_rcv": OBJ, "limits": {"max_linear_mps": 2.0, "max_angular_dps": 150.0}}


def frame(t, root, hand, fwd=(0, 0, 1), rcv_head=None):
    head = [root[0], 1.58, root[2]]
    return {"t": t, "src": {"root": root, "head": head, "head_fwd": list(fwd), "lh": head, "rh": hand},
            "rcv": {"head": rcv_head or head, "head_fwd": list(fwd), "lh": head, "rh": hand}}


class Analyze(unittest.TestCase):
    def frames(self):
        out = []
        for i in range(40):
            out.append(frame(i * 0.1, [2.5, 0, 2.4], [2.5, 0.75, 3.5], (0, 0, 1)))
        return out

    def test_identical_copy_has_zero_error(self):
        sem = an.semantic_metrics(self.frames(), HEADER)
        reach = [s for s in sem if s["event"] == "reach"]
        self.assertEqual(len(reach), 1)
        self.assertEqual(reach[0]["receiver_hand_to_counterpart_m"], 0.0)
        self.assertEqual(reach[0]["receiver_gaze_error_plan_deg"], 0.0)

    def test_teleport_is_a_jump_and_a_freeze_is_a_stall(self):
        fr = []
        for i in range(60):
            x = 1.0 + 0.012 * i if i < 30 else 1.36 + 0.012 * (i - 30)   # source walks 0.12 m/s... above the stop threshold
            src_root = [x * 5, 0, 1.0]
            rcv = [x * 5, 1.58, 1.0]
            if i == 20: rcv = [x * 5 + 3.0, 1.58, 1.0]                      # teleport, then back on the next frame
            if 35 <= i < 55: rcv = [fr[34]["rcv"]["head"][0], 1.58, 1.0]    # frozen while the source keeps walking
            fr.append(frame(i * 0.1, src_root, [0, 0, 0], rcv_head=rcv))
        loc = an.locomotion_metrics(fr, HEADER)
        self.assertGreaterEqual(len(loc["jumps_over_driver_bound"]), 1)
        self.assertGreaterEqual(len(loc["stalls"]), 1)


if __name__ == "__main__":
    unittest.main()
