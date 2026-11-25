# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Static guard for T3.6 (parity row AV13): no runtime C# reads AffordanceVolume.

An object's geometry is declared by SbSpatialAuthoring. The AffordanceVolume type is kept only so old
content still loads, and the Editor-only AffordanceVolumeMigration converts it. Placement, ZoneBased, the
manager's registration and annotation read the authored region instead. This test does not start Unity.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets" / "SpatialBlend"
TYPE_DEFINITION = ASSETS / "Framework" / "Scripts" / "AffordanceVolume.cs"
MIGRATION_HELPER = ASSETS / "Editor" / "AffordanceVolumeMigration.cs"

# The type, its nested enum and the old visualizer. UI names that only echo the old mode label
# (ManipulationPopUp.OnAffordanceVolumeSelected) are not the type and do not match.
RETIRED_NAMES = re.compile(r"\bAffordanceVolume(Type|Visualizer)?\b")
RETIRED_ENTRY_POINTS = re.compile(r"\b(RegisterVolume|OnAffordanceVolumeShare|CreateAffordanceVolume)\b")
SCRIPT_GUID = re.compile(r"guid:\s*([0-9a-f]{32})").search(
    TYPE_DEFINITION.with_name(TYPE_DEFINITION.name + ".meta").read_text()).group(1)
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)


def code_of(path):
    """C# without comments, so a note about the old volume is not a reference."""
    text = BLOCK_COMMENT.sub("", path.read_text(encoding="utf-8", errors="replace"))
    return "\n".join(re.sub(r"//.*", "", line) for line in text.splitlines())


def runtime_sources():
    for path in sorted(ASSETS.rglob("*.cs")):
        # Editor tools and EditMode fixtures may name the type: the helper converts it and the
        # fixtures prove the runtime ignores it. Player scripts are the surface this guard covers.
        if "Editor" in path.parts or "Test" in path.parts or "Tests" in path.parts:
            continue
        if path == TYPE_DEFINITION:
            continue
        yield path


class NoRuntimeAffordanceVolumeTests(unittest.TestCase):
    def test_runtime_sources_do_not_reference_the_volume(self):
        hits = []
        for path in runtime_sources():
            for number, line in enumerate(code_of(path).splitlines(), 1):
                if RETIRED_NAMES.search(line) or RETIRED_ENTRY_POINTS.search(line):
                    hits.append(f"{path.relative_to(ROOT).as_posix()}:{number}:{line.strip()}")
        self.assertEqual(hits, [], "runtime code references AffordanceVolume:\n" + "\n".join(hits))

    def test_the_guard_sees_the_runtime_tree(self):
        sources = [path.name for path in runtime_sources()]
        for name in ("SpatialBlendManager.cs", "PlacementMotif.cs", "SbNetworkAvatarEntity.cs", "SbEntity.cs"):
            self.assertIn(name, sources)
        self.assertNotIn("AffordanceVolumeMigration.cs", sources)

    def test_the_editor_migration_helper_exists_and_is_editor_only(self):
        self.assertTrue(MIGRATION_HELPER.is_file())
        self.assertIn("Editor", MIGRATION_HELPER.relative_to(ASSETS).parts)
        text = code_of(MIGRATION_HELPER)
        self.assertIn("SbSpatialAuthoring", text)
        self.assertIn("declaredExtent", text)
        # No runtime assembly can call it, because it lives in the Editor assembly.
        self.assertIn('"Editor"', (ASSETS / "Editor" / "SpatialBlend.Editor.asmdef").read_text())

    def test_the_type_is_kept_only_for_old_content_and_nothing_looks_it_up(self):
        self.assertTrue(TYPE_DEFINITION.is_file())
        for path in runtime_sources():
            self.assertNotIn("GetComponent<AffordanceVolume", code_of(path), path.name)

    def test_no_prefab_scene_or_asset_carries_the_volume_component(self):
        carrying = []
        for pattern in ("*.prefab", "*.unity", "*.asset"):
            for path in ASSETS.rglob(pattern):
                if SCRIPT_GUID in path.read_text(encoding="utf-8", errors="replace"):
                    carrying.append(path.relative_to(ROOT).as_posix())
        self.assertEqual(carrying, [], "content still carries an AffordanceVolume component")

    def test_placement_fits_the_authored_region_through_the_one_room_rule(self):
        # Since the simple room a drop no longer snaps against the walkable region in the motif: it asks the
        # app's one placement rule (SbApp -> RoomFit -> RegionPull, whose test is WalkablePlacementSnap.Satisfies
        # against the registered room region with obstacles as holes). Never the NavMesh, never a volume.
        placement = code_of(ASSETS / "Interaction" / "Scripts" / "Placement" / "PlacementMotif.cs")
        self.assertIn("SbSpatialAuthoring", placement)
        self.assertIn("TryPullPlacement(", placement)
        self.assertIn("TryPlaceDrop(", placement)
        self.assertNotIn("NavMesh", placement)
        self.assertNotIn("AffordanceVolume", placement)
        app = code_of(ASSETS / "Framework" / "Scripts" / "SbApp.cs")
        self.assertIn("RoomFit.TryPull(", app)
        self.assertIn("RoomFit.Place(", app)
        self.assertIn("WalkablePlacementSnap.Satisfies", code_of(ASSETS / "Foundation" / "Runtime" / "RegionPull.cs"))

    def test_entity_no_longer_creates_a_volume_at_runtime(self):
        entity = code_of(ASSETS / "Framework" / "Scripts" / "SbEntity.cs")
        self.assertNotIn("defaultAffordanceType", entity)
        manager = code_of(ASSETS / "Framework" / "Scripts" / "SpatialBlendManager.cs")
        self.assertIn("RegisterAffordances(entity)", manager)


if __name__ == "__main__":
    unittest.main()
