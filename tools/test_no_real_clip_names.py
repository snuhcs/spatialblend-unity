# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""T6.0 guard: no real dataset clip / scene names in tracked files.

Real clip ids and per-clip receipts live in the private store; the repo holds
hashed ids (``h_<12 hex>``, see ``clip_ids.py`` in spatialblend-eval) and aggregates.
The patterns are STRUCTURAL (the shape of COUCH / TRUMANS / PROX / HM3D ids),
so this file names no real clip.  Add a path to ALLOWLIST only when the
dataset's own scene list is needed by the code itself.
"""
import re
import subprocess
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

PATTERNS = {
    # No left word-boundary: names glued after "_" or a letter (e.g. k1_<id>) must still match.
    # COUCH: s<session>u<user>_<object>_<take>, bare <object><n>_sit<nn>, archive member names
    "couch": r"s\d+u\d+_[a-z0-9]+(?:_[a-z0-9]+)*"
             r"|(?:chair|sofa|bed|table|stool)\d*_(?:sit|sitfree|lie|stand)[a-z]*\d+"
             r"|session\d+_subject\d+_\w+|subject\d+_(?:bvh|npz)\b",
    # TRUMANS: sequence ids are YYYY-MM-DD@HH-MM-SS (the "_" separated dashed form too).  The compact
    # YYYYMMDD_HHMMSS form is a training-run / log timestamp, not a clip id, and does not match.
    "trumans": r"(?<!\d)20\d{2}-\d{2}-\d{2}[_@]\d{2}-\d{2}(?:-\d{2})?(?!\d)",
    # PROX: MPH<n><suffix>, N<d><Name>, Basement<Name> scene names
    "prox": r"(?<![A-Za-z0-9])(?:MPH\d+[A-Za-z]*|N\d[A-Z][a-z]+(?:[A-Z][a-z]+)*|Basement[A-Z][A-Za-z]+)",
    # HM3D: <5 digits>_<n>_<n> navmesh ids, <5 digits>-<hash> scene ids
    "hm3d": r"(?<!\d)\d{5}_\d+_\d+(?!\d)|(?<!\d)\d{5}-[A-Za-z0-9]{8,13}(?![A-Za-z0-9])",
}
COMPILED = {k: re.compile(v) for k, v in PATTERNS.items()}

# Files that legitimately carry a dataset's own scene list (loader needs it).
ALLOWLIST = {
    "tools/test_no_real_clip_names.py",
}
MAX_BYTES = 3_000_000
# Lockfiles: their contents are integrity hashes, not names.  (No third_party skip: this repo has no
# vendored third-party tree.)
SKIP_NAMES = {"package-lock.json", "packages-lock.json", "yarn.lock", "pnpm-lock.yaml", "poetry.lock",
              "uv.lock", "Cargo.lock", "Pipfile.lock", "requirements.lock", "bun.lockb"}
# HM3D scene ids are public dataset identifiers (code, docs, examples, tests).  Allowed everywhere except
# where per-scene RESULTS or selections live (same policy as spatialblend-motion-generator).  This repo
# currently carries no HM3D ids; the exemption is kept for consistency.
HM3D_FORBIDDEN_PREFIXES = ("results/", "config/", "docs/results/")


def _allowed(rel, kind):
    return kind == "hm3d" and not rel.startswith(HM3D_FORBIDDEN_PREFIXES)


def _skipped(rel):
    return Path(rel).name in SKIP_NAMES or rel.endswith(".lock")


def _tracked():
    out = subprocess.run(["git", "-C", str(REPO), "ls-files", "-z"], capture_output=True, check=True).stdout
    return [p for p in out.decode().split("\0") if p]


def scan_text(text):
    return sorted({(k, m.group(0)) for k, rx in COMPILED.items() for m in rx.finditer(text)})


class NoRealClipNamesTests(unittest.TestCase):
    def test_patterns_catch_the_shapes_without_naming_real_clips(self):
        # shapes assembled from parts so this file stays clean
        self.assertTrue(scan_text("s" + "2u1_" + "obj3_take02"))
        self.assertTrue(scan_text("k1_" + "s" + "2u1_" + "obj3_take02"))
        self.assertTrue(scan_text("MPH" + "11 and N" + "0Sofa"))
        self.assertTrue(scan_text("0" * 5 + "-" + "A" * 11))
        self.assertTrue(scan_text("0" * 5 + "_5_50"))
        self.assertTrue(scan_text("2023-01-02@03-04-05"))
        # compact training-run timestamps are not TRUMANS ids
        self.assertFalse(scan_text("20260514_174116 run_20260408_143022"))
        self.assertFalse(scan_text("chair_sit sofa_sit h_0123456789ab synthetic_clip_01"))

    def test_policy_helpers(self):
        self.assertTrue(_skipped("Packages/packages-lock.json"))
        self.assertTrue(_skipped("env/x.lock"))
        self.assertFalse(_skipped("docs/a.md"))
        self.assertTrue(_allowed("tools/x.py", "hm3d"))
        self.assertFalse(_allowed("results/x.json", "hm3d"))
        self.assertFalse(_allowed("config/x.json", "hm3d"))
        self.assertFalse(_allowed("docs/results/x.md", "hm3d"))
        self.assertFalse(_allowed("docs/a.md", "couch"))

    def test_no_real_clip_names_in_tracked_files(self):
        hits = []
        for rel in _tracked():
            if rel in ALLOWLIST or _skipped(rel):
                continue
            for k, _ in scan_text(rel):
                if _allowed(rel, k):
                    continue
                hits.append("{}: path matches {}".format(rel, k))
            path = REPO / rel
            try:
                if not path.is_file() or path.stat().st_size > MAX_BYTES:
                    continue
                text = path.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue
            for kind, _ in scan_text(text):
                if _allowed(rel, kind):
                    continue
                hits.append("{}: {}".format(rel, kind))   # matching text deliberately not echoed
        self.assertFalse(hits, "real dataset clip/scene names in tracked files (move to the private store):\n"
                         + "\n".join(hits[:50]))


if __name__ == "__main__":
    unittest.main()
