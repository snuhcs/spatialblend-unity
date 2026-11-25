# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Guards for the lobby tutorial's bundled assets: the recorded hashes match the files, the synthetic ones reproduce byte for byte
from tools/make_tutorial_assets.py, no narration audio is bundled (the lines are subtitles), and the spoken lines in the
ASSETS.md table are the lines in TutorialScript.cs and ArrivalNarration.cs."""
import hashlib
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TUTORIAL = ROOT / "Assets/SpatialBlend/Samples/Tutorial"
RESOURCES = TUTORIAL / "Resources/TutorialAssets"
ASSETS_MD = TUTORIAL / "ASSETS.md"
SCRIPT = ROOT / "Assets/SpatialBlend/Samples/Scripts/Tutorial/TutorialScript.cs"

# The steps' clips in script order, then the lead-in of the scan task (modeClip: "..." in TutorialScript.cs, said when the user starts it in Interaction).
STEP_CLIPS = ["t1_1", "t1_2_chess", "t1_2_board", "t2_1", "t2_1_shared", "t2_2", "t2_2b", "t3_1", "t3_2", "t3_3", "t3_4", "t3_5", "t3_6", "t3_7", "t3_8_partner", "t4_1", "t4_2"]
LEAD_IN_CLIPS = ["t3_0"]
CLIPS = STEP_CLIPS + LEAD_IN_CLIPS
ARRIVAL = [
    ("a_person", "Someone joined."),
    ("a_object", "A new object arrived. Hover it to place it."),
    ("a_objects", "New objects arrived. Hover one to place it."),
    ("a_person_object", "Someone joined, and a new object arrived. Hover it to place it."),
    ("a_person_objects", "Someone joined, and new objects arrived. Hover one to place it."),
]
NARRATION = ROOT / "Assets/SpatialBlend/Samples/Scripts/Freeform Collaboration/ArrivalNarration.cs"

# S("clip", "label", TutorialSignal.X, "spoken line") in TutorialScript.cs
STEP = re.compile(r'S\("(t\w+)",\s*"(?:[^"\\]|\\.)*",\s*TutorialSignal\.\w+,\s*"((?:[^"\\]|\\.)*)"\s*[,)]')
LEAD_IN = re.compile(r'modeClip: "(t\w+)", modeVoice: "((?:[^"\\]|\\.)*)"')


# A rewritten line whose new clip is not delivered yet (TutorialScript.PendingNarration): its file is absent and its row says pending.
PENDING_LIST = re.compile(r'PendingNarration = new(?:\[\])? *\{([^}]*)\}|PendingNarration = new string\[0\]')


def pending_clips():
    m = PENDING_LIST.search(SCRIPT.read_text(encoding="utf-8"))
    assert m, "TutorialScript.PendingNarration not found"
    return re.findall(r'"(t\w+)"', m.group(1) or "")


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class TutorialAssets(unittest.TestCase):
    def setUp(self):
        self.md = ASSETS_MD.read_text(encoding="utf-8")

    def test_every_asset_hash_in_assets_md_matches_the_file(self):
        for name in ("sample_object.bytes", "sample_photo.bytes", "sample_mask.bytes", "sample_shared.bytes"):
            row = [line for line in self.md.splitlines() if f"`{name}`" in line]
            self.assertEqual(len(row), 1, name)
            self.assertIn(sha(RESOURCES / name), row[0], name)

    def test_the_synthetic_assets_reproduce_from_the_generator(self):
        with tempfile.TemporaryDirectory() as out:
            subprocess.run([sys.executable, "-I", str(ROOT / "tools/make_tutorial_assets.py"), out], check=True, capture_output=True)
            self.assertEqual(sha(Path(out) / "sample_object.glb"), sha(RESOURCES / "sample_object.bytes"))
            self.assertEqual(sha(Path(out) / "sample_photo.png"), sha(RESOURCES / "sample_photo.bytes"))
            self.assertEqual(sha(Path(out) / "sample_shared.glb"), sha(RESOURCES / "sample_shared.bytes"))

    def test_the_sample_model_is_a_glb_and_the_photo_a_png(self):
        self.assertEqual((RESOURCES / "sample_object.bytes").read_bytes()[:4], b"glTF")
        self.assertEqual((RESOURCES / "sample_shared.bytes").read_bytes()[:4], b"glTF")
        self.assertNotEqual(sha(RESOURCES / "sample_shared.bytes"), sha(RESOURCES / "sample_object.bytes"), "the shared object is not the vase")
        self.assertEqual((RESOURCES / "sample_photo.bytes").read_bytes()[:8], b"\x89PNG\r\n\x1a\n")

    def test_no_narration_audio_is_bundled_and_the_subtitle_fallback_is_recorded(self):
        for folder in ("Audio", "Arrival"):
            self.assertFalse([p for p in (RESOURCES / folder).glob("*") if p.suffix.lower() in (".mp3", ".wav", ".ogg")] if (RESOURCES / folder).exists() else [],
                             folder + ": no narration audio ships with the repository")
        out = subprocess.run(["git", "-C", str(ROOT), "ls-files", "Assets/SpatialBlend/Samples/Tutorial"], capture_output=True, text=True, check=True).stdout
        self.assertFalse([p for p in out.split("\n") if p.lower().endswith((".mp3", ".wav", ".ogg", ".mp3.meta"))], "no narration audio is tracked")
        for needle in ("no audio is bundled", "subtitles", "TutorialNarrator.ReadingSeconds", "TutorialAssets.HasNarration"):
            self.assertIn(needle, self.md)

    def test_the_synthetic_assets_are_recorded_as_cc0_with_their_source(self):
        self.assertIn("CC0 1.0", self.md)
        self.assertIn("tools/make_tutorial_assets.py", self.md)

    def test_the_spoken_text_in_the_table_is_the_text_in_the_script(self):
        text = SCRIPT.read_text(encoding="utf-8")
        steps = STEP.findall(text)
        self.assertEqual([clip for clip, _ in steps], STEP_CLIPS)
        lead_ins = LEAD_IN.findall(text)
        self.assertEqual([clip for clip, _ in lead_ins], LEAD_IN_CLIPS)
        for clip, voice in steps + lead_ins:
            rows = [line for line in self.md.splitlines() if f"`Audio/{clip}`" in line]
            self.assertEqual(len(rows), 1, clip)
            row = rows[0]
            self.assertTrue(row.rstrip().endswith(voice.replace('\\"', '"') + " |"), clip)

    def test_the_lines_are_short_second_person_english(self):
        text = SCRIPT.read_text(encoding="utf-8")
        for _, voice in STEP.findall(text) + LEAD_IN.findall(text):
            self.assertLess(len(voice), 150, voice)
            self.assertTrue(voice.isascii(), voice)

    def test_resources_are_force_tracked_in_git(self):
        out = subprocess.run(["git", "-C", str(ROOT), "ls-files", "Assets/SpatialBlend/Samples/Tutorial"], capture_output=True, text=True, check=True).stdout
        tracked = set(out.split("\n"))
        self.assertIn("Assets/SpatialBlend/Samples/Tutorial/Resources/TutorialAssets/sample_object.bytes", tracked)
        self.assertIn("Assets/SpatialBlend/Samples/Tutorial/Resources/TutorialAssets/sample_mask.bytes", tracked)
        self.assertIn("Assets/SpatialBlend/Samples/Tutorial/Resources/TutorialAssets/sample_shared.bytes", tracked)

    def test_the_sample_mask_is_one_byte_per_photo_pixel_and_its_source_is_recorded(self):
        mask = (RESOURCES / "sample_mask.bytes").read_bytes()
        self.assertEqual(len(mask), 640 * 480)
        self.assertEqual(set(mask), {0, 1})
        self.assertEqual(sum(mask), 34870)
        for needle in ("SAM3", "2026-10-23", "(300, 200)", "(340, 300)", "ReconstructionService/Segment"):
            self.assertIn(needle, self.md)

    def test_the_arrival_lines_in_the_table_are_the_narration_lines(self):
        narration = NARRATION.read_text(encoding="utf-8")
        for name, voice in ARRIVAL:
            row = [line for line in self.md.splitlines() if f"`Arrival/{name}`" in line]
            self.assertEqual(len(row), 1, name)
            self.assertTrue(row[0].rstrip().endswith(voice + " |"), name)
            self.assertIn(f'"{voice}"', narration, name)

if __name__ == "__main__":
    unittest.main()
