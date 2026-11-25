# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Source guards for the box annotation to region port (T5.2). None of these start Unity."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets/SpatialBlend"

# The annotation path: the box, its placement, its declaration, the scan binding.
ANNOTATION_PATH = (
    ASSETS / "Interaction/Scripts/Annotation/Motifs/BoxAnnotationInstance.cs",
    ASSETS / "Interaction/Scripts/Placement/BoxAnnotationPlacementMotif.cs",
    ASSETS / "Framework/Scripts/BoxAnnotationAuthoring.cs",
    ASSETS / "Freeform/Scanning/ScanBoxRegion.cs",
    ASSETS / "Samples/Scanning/Scripts/GltfScanResultSink.cs",
)
NEW_FILES = ANNOTATION_PATH[2:4] + (
    ASSETS / "Framework/Test/Restoration/ScanBoxRegionTests.cs",
    ASSETS / "Framework/Test/Restoration/BoxAnnotationAuthoringContractTests.cs",
)


def read(path):
    return path.read_text(encoding="utf-8")


def code_text(path):
    """Source with // and /// comments removed."""
    return "\n".join(line.split("//")[0] for line in read(path).splitlines())


class AnnotationPortGuards(unittest.TestCase):
    def test_no_annotation_file_is_an_affordance_volume(self):
        for path in ANNOTATION_PATH:
            self.assertTrue(path.is_file(), path.name)
            self.assertNotIn("AffordanceVolume", code_text(path), path.name)

    def test_the_box_is_declared_through_spatial_authoring_only(self):
        authoring = code_text(ASSETS / "Framework/Scripts/BoxAnnotationAuthoring.cs")
        self.assertIn("SbSpatialAuthoring", authoring)
        sink = code_text(ASSETS / "Samples/Scanning/Scripts/GltfScanResultSink.cs")
        self.assertIn("BoxAnnotationAuthoring.Declare", sink)
        self.assertNotIn("TODO(T5.2)", read(ASSETS / "Samples/Scanning/Scripts/GltfScanResultSink.cs"))
        self.assertNotIn("TODO(T5.2)", read(ASSETS / "Framework/Scripts/SessionManager.cs"))

    def test_the_box_declares_when_sized_and_withdraws_when_deleted(self):
        instance = code_text(ASSETS / "Interaction/Scripts/Annotation/Motifs/BoxAnnotationInstance.cs")
        self.assertRegex(instance, r"_collider\.size = size;\s*BoxAnnotationAuthoring\.Declare\(gameObject, _collider\);")
        self.assertRegex(instance, r"private void OnDestroy\(\)\s*\{[^}]*BoxAnnotationAuthoring\.Withdraw\(gameObject\);")
        self.assertIn("AddComponent<BoxAnnotationPlacementMotif>()", instance)

    def test_the_floor_polygon_motif_never_runs_on_a_box(self):
        context = code_text(ASSETS / "Interaction/Scripts/Manipulation/ManipulationContext.cs")
        method = context.index("private void ExecuteAffordanceVolumeMode()")
        guard = context.index("_targetType == ManipulationTargetType.BoxAnnotationInstance", method)
        motif = context.index("AddComponent<AffordanceAnnotationMotif>()", method)
        self.assertLess(guard, motif)

    def test_the_panel_flow_and_delete_button_are_ported(self):
        context = read(ASSETS / "Interaction/Scripts/Manipulation/ManipulationContext.cs")
        for needle in (
            # Reconstruct 3D opens the capture card on the box (scan UI Concept A), not the floating panel.
            "ScanCard.Open(_targetBoxAnnotation, ResolveCenterEyeAnchor());",
            "popup.OnDeleteSelected.AddListener(() => SetSelectedMode(3));",
            "private void ExecuteDeleteMode()",
        ):
            self.assertIn(needle, context)
        popup = read(ASSETS / "Interaction/Scripts/UI/ManipulationPopUp.cs")
        self.assertIn("public UnityEvent OnDeleteSelected;", popup)
        self.assertIn("_deleteToggle", popup)
        scene = read(ASSETS / "Samples/Scenes/FreeformCollaboration.unity")
        guid = re.search(r"guid: ([0-9a-f]{32})", read(ASSETS / "Samples/Scanning/Prefabs/PassthroughPanel.prefab.meta")).group(1)
        self.assertIn(f"_defaultPassthroughPanelPrefab: {{fileID: 270712986787701689, guid: {guid}, type: 3}}", scene)
        prefab = read(ASSETS / "Interaction/Prefabs/UI/ManipulationPopUpUI.prefab")
        self.assertIn("_deleteToggle:", prefab)

    def test_the_box_travels_as_the_second_content_block(self):
        entity = read(ASSETS / "Framework/Scripts/SbNativeEntity.cs")
        self.assertLess(
            entity.index("AddSerializationBlock(ObjectSerializer.CreateContentBlock(this));"),
            entity.index("AddSerializationBlock(ObjectSerializer.CreateBoxRegionBlock(this));"),
        )
        serializer = read(ASSETS / "Framework/Scripts/NativeContent/ObjectSerializer.cs")
        self.assertIn("HEADER_ID_BOX_REGION = 778", serializer)
        self.assertNotEqual(
            re.search(r"HEADER_ID_CONTENT = (\d+)", serializer).group(1),
            re.search(r"HEADER_ID_BOX_REGION = (\d+)", serializer).group(1),
        )

    def test_the_pure_region_does_not_depend_on_the_legacy_assembly(self):
        text = code_text(ASSETS / "Freeform/Scanning/ScanBoxRegion.cs")
        for forbidden in ("SbSpatialAuthoring", "SpatialBlendManager", "SpatialBlendSettings", "Debug.Log", "MonoBehaviour"):
            self.assertNotIn(forbidden, text)

    def test_the_new_fixtures_are_registered(self):
        freeform = read(ROOT / "tools/verify-freeform-results.py")
        foundation = read(ROOT / "tools/verify-foundation-results.py")
        sources = read(ROOT / "tools/run-foundation-tests.sh")
        self.assertIn('"ScanBoxRegionTests"', freeform)
        self.assertIn('"BoxAnnotationAuthoringContractTests"', freeform)
        self.assertIn('"ScanBoxRegionTests"', foundation)
        self.assertIn("ScanBoxRegionTests.cs", sources)
        # The contract fixture reads the SpatialBlend assembly, so the foundation project cannot compile it.
        self.assertNotIn("BoxAnnotationAuthoringContractTests", foundation)
        self.assertNotIn("BoxAnnotationAuthoringContractTests", sources)

    def test_new_files_are_lf_end_with_a_newline_and_have_a_meta(self):
        for path in NEW_FILES:
            data = path.read_bytes()
            self.assertNotIn(b"\r", data, path.name)
            self.assertTrue(data.endswith(b"\n"), path.name)
            meta = path.with_name(path.name + ".meta")
            self.assertTrue(meta.is_file(), meta.name)
            self.assertRegex(read(meta), r"guid: [0-9a-f]{32}")

    def test_ported_files_stay_lf(self):
        for path in ANNOTATION_PATH + (
            ASSETS / "Interaction/Scripts/Manipulation/ManipulationContext.cs",
            ASSETS / "Interaction/Scripts/UI/ManipulationPopUp.cs",
            ASSETS / "Interaction/Scripts/UI/BillboardToCamera.cs",
            ASSETS / "Interaction/Scripts/Core/InteractionModeManager.cs",
            ASSETS / "Framework/Test/Restoration/FreeformV1ParityTests.cs",
        ):
            self.assertNotIn(b"\r", path.read_bytes(), path.name)

    def test_the_parity_matrix_has_the_annotation_rows_active(self):
        parity = ROOT / "docs/freeform-v1-parity.md"
        if not parity.is_file():
            self.skipTest("the parity record (docs/freeform-v1-parity.md) is a private design note, absent from the public tree")
        doc = read(parity)
        for row in ("S1", "S2", "AV12"):
            self.assertRegex(doc, rf"\| {row} \|.*\| Active \|")
        tests = read(ASSETS / "Framework/Test/Restoration/FreeformV1ParityTests.cs")
        for name in ("S2_ScanBoxBecomesAnExplicitRegion", "AV12_AnnotationBecomesARegion"):
            at = tests.index(f"public void {name}()")
            self.assertNotIn("[Explicit", tests[max(0, at - 120):at], name)


if __name__ == "__main__":
    unittest.main()
