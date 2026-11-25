# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""The repository's own generated assets reproduce byte for byte from their generators (so their origin is the generator):
the sound effects (tools/make_sfx.py) and the whiteboard grab glow (tools/make_panel_glow.py)."""
import hashlib
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SFX = ROOT / "Assets/SpatialBlend/Samples/Resources/SFX"
GLOW = ROOT / "Assets/SpatialBlend/Samples/Objects/Whiteboard/Textures/panelEdgeGlow.png"


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class GeneratedAssets(unittest.TestCase):
    def test_the_sound_effects_reproduce_from_the_synthesizer(self):
        with tempfile.TemporaryDirectory() as out:
            subprocess.run([sys.executable, "-I", str(ROOT / "tools/make_sfx.py"), out], check=True, capture_output=True)
            made = sorted(p.name for p in Path(out).glob("*.wav"))
            self.assertEqual(made, sorted(p.name for p in SFX.glob("*.wav")))
            for name in made:
                self.assertEqual(sha(Path(out) / name), sha(SFX / name), name)

    def test_the_grab_glow_reproduces_from_its_generator(self):
        with tempfile.TemporaryDirectory() as out:
            target = Path(out) / "glow.png"
            subprocess.run([sys.executable, "-I", str(ROOT / "tools/make_panel_glow.py"), str(target)], check=True, capture_output=True)
            self.assertEqual(sha(target), sha(GLOW))


if __name__ == "__main__":
    unittest.main()
