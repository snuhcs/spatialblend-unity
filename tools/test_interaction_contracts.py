# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""stdlib-only static checks for T1.3b, T2.1b and T3.8 (containment transfer, T2.6); no Unity process is launched."""
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Assets" / "SpatialBlend"
ENTITY = ASSETS / "Framework" / "Scripts" / "SbNetworkAvatarEntity.cs"
ZONE = ASSETS / "Avatar" / "Scripts" / "DeclaredRegionZone.cs"
AUTHORING = ASSETS / "Framework" / "Scripts" / "SbSpatialAuthoring.cs"
CONTAINMENT = ASSETS / "Framework" / "Scripts" / "SpatialBlendManager.ContainmentTransfer.cs"
BINDING = ASSETS / "Foundation" / "Internal" / "SpatialBlendAPI.ContainmentTransfer.cs"
SPATIAL = ASSETS / "Framework" / "Scripts" / "SpatialBlendManager.SpatialHierarchy.cs"
RESTORATION = ASSETS / "Framework" / "Test" / "Restoration"
NEW_FIXTURES = {
    "ContainmentInteractionMapperTests": RESTORATION / "ContainmentInteractionMapperTests.cs",
    "ContainmentTransferBindingTests": RESTORATION / "ContainmentTransferBindingTests.cs",
    "ManagerContainmentContractTests": RESTORATION / "ManagerContainmentContractTests.cs",
    "ContinuousCompensationTests": RESTORATION / "ContinuousCompensationTests.cs",
    "DeclaredRegionZoneTests": RESTORATION / "DeclaredRegionZoneTests.cs",
    "ZoneBasedDeclaredRegionContractTests": RESTORATION / "ZoneBasedDeclaredRegionContractTests.cs",
}


def code_of(path):
    return path.read_text(encoding="utf-8")


class ZoneBasedContractTests(unittest.TestCase):
    def test_zone_based_sources_never_read_the_affordance_volume(self):
        for path in (ENTITY, ZONE, AUTHORING):
            text = code_of(path)
            for token in ("AffordanceVolume", "PolygonCollider2D", "AffordancePolygonContainment"):
                self.assertNotIn(token, text, f"{path.name} mentions {token}")

    def test_zone_based_trigger_reads_the_declared_regions(self):
        text = code_of(ENTITY)
        # The core's value, for the local avatar too; no point test in the app.
        self.assertIn("IsTransferInteracting", text)
        self.assertNotIn("SbSpatialAuthoring.ActiveRegions", text)
        self.assertIn("DeclaredRegionZone.Switch", text)
        self.assertNotIn("FindObjectsByType<Affordance", text)

    def test_zone_based_ignores_the_core_event_before_touching_display_state(self):
        text = code_of(ENTITY)
        handler = text.index("private void OnAvatarInteraction(")
        guard = text.index("if (IsZoneBasedMode)", handler)
        apply = text.index("SetInteractionState", handler)
        self.assertLess(guard, apply)

    def test_zone_has_no_dwell_or_margin_of_its_own(self):
        for line in code_of(ZONE).splitlines():
            stripped = line.strip()
            if stripped.startswith("//"):
                continue
            for word in ("dwell", "Dwell", "margin", "Time."):
                self.assertNotIn(word, stripped, line)

    def test_region_shares_the_compiler_resolver(self):
        self.assertIn("SpatialAuthoringCompiler.TryResolveRegionPolygon", code_of(AUTHORING))
        compiler = code_of(ASSETS / "Framework" / "Scripts" / "SpatialAuthoringCompiler.cs")
        self.assertEqual(compiler.count("TryResolveRegionPolygon("), 2)

    def test_removed_zone_scan_and_hysteresis_files_are_gone(self):
        for old in (
            ASSETS / "Avatar" / "Scripts" / "AffordancePolygonContainment.cs",
            ASSETS / "Foundation" / "InteractionHysteresis.cs",
            RESTORATION / "InteractionHysteresisTests.cs",
        ):
            self.assertFalse(old.exists(), old.name)


class DefaultModeContractTests(unittest.TestCase):
    def test_manager_holds_no_app_level_hysteresis_and_takes_the_interaction_value_from_the_proposal_set(self):
        text = code_of(CONTAINMENT)
        for token in ("InteractionHysteresis", "EnterR", "ExitR", "SetRelationPhaseConfig", "InteractionPhaseMapper"):
            self.assertNotIn(token, text)
        self.assertIn("ContainmentInteractionMapper", text)
        # ABI 4.3: the value is the TRANSFER flag AND the root's ARRIVED, read from the set.
        self.assertIn("ObserveTransferState", text)
        self.assertIn("TransferArrived", text)
        for token in ("ContainmentEvent", "ApplyContainmentEvent", "_containedActors", "_containedUnmappedActors"):
            self.assertNotIn(token, text)
        # ABI 4 has no per-actor containment state to read or poll.
        for token in ("ReadActorContainment", "PollContainment", "ActorContainment"):
            self.assertNotIn(token, text)

    def test_no_affordance_pose_binding_on_the_root(self):
        text = code_of(CONTAINMENT)
        for token in ("RegisterRegionInteraction", "BindRegionInteractions", "BindScoped",
                      "SbKernelType.AffordancePose", "PollAffordancePoseRelations"):
            self.assertNotIn(token, text)
        for path in ASSETS.rglob("*.cs"):
            if "Test" in path.parts or "Photon" in path.parts:
                continue
            self.assertNotIn("RegisterRegionInteraction", code_of(path), path.name)
        self.assertFalse((ASSETS / "Framework" / "Scripts" / "SpatialBlendManager.RelationEvents.cs").exists())
        self.assertFalse((ASSETS / "Foundation" / "InteractionPhaseMapper.cs").exists())

    def test_mapper_has_no_thresholds(self):
        text = code_of(ASSETS / "Foundation" / "ContainmentInteractionMapper.cs")
        self.assertNotIn("Proximity", text)
        self.assertNotIn("1e-3", text)
        self.assertNotIn("Time.", text)

    def test_core_phase_is_not_reconfigured_by_the_app(self):
        for path in ASSETS.rglob("*.cs"):
            if "Test" in path.parts or "Photon" in path.parts:
                continue
            self.assertNotIn("sb_set_relation_phase_config", code_of(path), path.name)


class ContainmentTransferContractTests(unittest.TestCase):
    def test_authoring_writes_the_region_element_local_and_flags_it(self):
        text = code_of(SPATIAL)
        # Every path that writes a region (apply, link, refits, trims, restores) goes through one helper. The number of
        # such paths grew with the room rules, so the guard pins the helper, not a call count: outside it no region
        # polygon is written element-local and nothing is flagged; a direct call only retires a region (flag false)
        # or writes the unflagged footprint fallback.
        self.assertGreater(text.count("WriteAuthoredRegion("), 0)
        self.assertEqual(re.findall(r"SetSpatialHierarchyLocalBoundary\([^)]*\)", text),
                         ["SetSpatialHierarchyLocalBoundary(nativeSpace, element, footprint)"])
        manager_sources = [p for p in (ASSETS / "Framework" / "Scripts").glob("SpatialBlendManager*.cs") if p != CONTAINMENT]
        for path in manager_sources:
            self.assertNotRegex(code_of(path), r"SetSpatialHierarchyContainmentTransfer\([^)]*,\s*true\)", path.name)
        manager = code_of(CONTAINMENT)
        self.assertEqual(manager.count("SetSpatialHierarchyContainmentTransfer(nativeSpace, element, true)"), 1)
        self.assertIn("SetSpatialHierarchyLocalBoundary", manager)
        self.assertIn("SetSpatialHierarchyContainmentTransfer", manager)
        self.assertLess(manager.index("SetSpatialHierarchyLocalBoundary(nativeSpace, element, polygon))"),
                        manager.index("SetSpatialHierarchyContainmentTransfer(nativeSpace, element, true)"),
                        "The flag needs the Containment definition, so the polygon goes first.")
        # The footprint polygon fallback is element-local as well, and is never flagged.
        self.assertIn("SetSpatialHierarchyLocalBoundary(nativeSpace, element, footprint)", text)

    def test_binding_declares_every_header_entry_for_both_libraries(self):
        text = code_of(BINDING)
        for entry in ("sb_get_containment_transfer_feature", "sb_get_containment_transfer_field_count",
                      "sb_get_containment_transfer_field_offset", "sb_set_element_local_polygon",
                      "sb_set_element_containment_transfer", "sb_get_source_transfer"):
            self.assertEqual(len(re.findall(r"extern [^;]*" + entry + r"\(", text)), 2, entry)
        self.assertEqual(text.count("RELEASE_LIBRARY"), 6)
        self.assertEqual(text.count("DEBUG_LIBRARY"), 6)

    def test_binding_has_no_contained_event_left(self):
        # ABI 4.3 removed the event: no entry point, delegate, type, token-fenced registration or teardown for it.
        api = code_of(ASSETS / "Foundation" / "Internal" / "SpatialBlendAPI.cs")
        binding = code_of(BINDING)
        for token in ("sb_register_on_containment_event", "OnContainmentEventNativeDelegate", "ContainmentEvent",
                      "DropContainmentCallback", "ReleaseContainmentCallbackNative", "ForgetContainmentCallbacks",
                      "ContainmentCallbackCount", "containmentCallbackSpaceByToken"):
            self.assertNotIn(token, api, token)
            self.assertNotIn(token, binding, token)

    def test_facing_is_global_and_the_root_is_the_drivers(self):
        # There is no app-side ContainedFacingPolicy (a scope override).
        self.assertFalse((ASSETS / "Foundation" / "ContainedRootPolicy.cs").exists())
        driver = code_of(ASSETS / "Foundation" / "Realization" / "EmbodiedDriver.cs")
        self.assertIn("class EmbodiedDriver", driver)
        self.assertNotIn("Lock", driver)  # containment is followed as a target, there is no lock
        manager = code_of(ASSETS / "Framework" / "Scripts" / "SpatialBlendManager.cs")
        self.assertNotIn("ContainedFacingPolicy", manager)
        self.assertNotIn("IsEmbodiedDriven", manager)  # no raw-apply path: the root is the driver's
        entity = code_of(ASSETS / "Framework" / "Scripts" / "SbNetworkAvatarEntity.cs")
        self.assertNotIn("NavMeshAgent>", entity)  # the root is written by the driver, never steered by an agent

    def test_continuous_compensation_is_ported_from_v1(self):
        meta = code_of(ASSETS / "Avatar" / "Scripts" / "MetaAvatarEntity.cs")
        self.assertIn("public void StartContinuousCompensation()", meta)
        self.assertIn("public void StopContinuousCompensation()", meta)
        self.assertIn("_compensation.Apply()", meta)
        behaviour = code_of(ASSETS / "Avatar" / "Scripts" / "SbAvatarBehaviourFusion.cs")
        self.assertIn("StartContinuousCompensation()", behaviour)
        self.assertIn("StopContinuousCompensation()", behaviour)
        self.assertNotIn("CompensateForFrames", behaviour)
        self.assertNotIn("UnityEngine.XR", code_of(ASSETS / "Avatar" / "Scripts" / "ContinuousCompensation.cs"))


class FixtureRegistrationTests(unittest.TestCase):
    def test_new_fixtures_are_registered_in_the_result_verifiers(self):
        freeform = (ROOT / "tools" / "verify-freeform-results.py").read_text(encoding="utf-8")
        foundation = (ROOT / "tools" / "verify-foundation-results.py").read_text(encoding="utf-8")
        runner = (ROOT / "tools" / "run-foundation-tests.sh").read_text(encoding="utf-8")
        for name, path in NEW_FIXTURES.items():
            self.assertTrue(path.exists(), name)
            self.assertTrue(path.with_name(path.name + ".meta").exists(), name + " meta")
            self.assertRegex(code_of(path), rf"class {name}\b")
            self.assertIn(f'"{name}"', freeform, name)
        for name in ("ContainmentInteractionMapperTests", "DeclaredRegionZoneTests"):
            self.assertIn(f'"{name}"', foundation, name)
            self.assertIn(f"{name}.cs", runner, name)
        self.assertIn("DeclaredRegionZone.cs", runner)

    def test_native_case_names_are_registered(self):
        names = (
            "ContainmentTransferBindingsMatchTheCoreLayoutAndOffsets",
            "TransferFlagRaisesStartThenTerminateWhenTheClientAvatarEntersAndLeavesTheObjectRegion",
            "IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw0",
            "IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw90",
            "IdenticalChessRemoteAvatarKeepsItsObjectRelativeTransformWhenTheReceiverPlacesItsCopyAtYaw200",
            "AvatarCrossingTheRegionEdgeIsFollowedWithinTheDriversBoundsAndTheTransferFlagSwitchesAtTheEdge",
            "ReceiverWithoutTheObjectFallsBackToTheSceneLevelRootAndRaisesNoInteraction",
            "ChessAnchorLeavingTheRegionHandsBackToSceneLevelRelations",
        )
        verify = (ROOT / "tools" / "freeform-native-verification" / "verify-results.py").read_text(encoding="utf-8")
        listed = (ROOT / "tools" / "test_freeform_native_verification.py").read_text(encoding="utf-8")
        source = (ROOT / "tools" / "freeform-native-verification" / "NativeManagerIntegrationTests.cs").read_text(encoding="utf-8")
        for name in names:
            self.assertIn(f'"{name}"', verify, name)
            self.assertIn(f'"{name}"', listed, name)
            self.assertRegex(source, rf"public void {name}\(\)", name)
        for gone in ("ReceivedAffordancePosePhaseRaisesStartThenTerminateWhenClientAvatarEntersAndLeavesTheRegion",
                     "ChessAnchorPoseIsPreservedObjectRelativeWhenTheReceiverPlacesItsCopyAtYaw0"):
            self.assertNotIn(gone, verify)
            self.assertNotIn(gone, source)
        self.assertNotIn("PollSpacingRelations", source)
        self.assertNotIn("PollAffordancePoseRelations", source)

    def test_touched_files_use_lf(self):
        paths = [ENTITY, ZONE, AUTHORING, CONTAINMENT, BINDING, SPATIAL,
                 ASSETS / "Foundation" / "ContainmentInteractionMapper.cs",
                 ASSETS / "Avatar" / "Scripts" / "ContinuousCompensation.cs",
                 ASSETS / "Avatar" / "Scripts" / "MetaAvatarEntity.cs",
                 ASSETS / "Avatar" / "Scripts" / "SbAvatarBehaviourFusion.cs",
                 ASSETS / "Framework" / "Scripts" / "SpatialAuthoringCompiler.cs",
                 RESTORATION / "FreeformV1ParityTests.cs",
                 ROOT / "tools" / "freeform-native-verification" / "NativeManagerIntegrationTests.cs",
                 ROOT / "docs" / "freeform-v1-parity.md",
                 *NEW_FIXTURES.values()]
        # The parity record is a private design note, absent from the public tree.
        paths = [path for path in paths if path.is_file() or path.name != "freeform-v1-parity.md"]
        for path in paths:
            self.assertNotIn(b"\r", path.read_bytes(), path.name)


if __name__ == "__main__":
    unittest.main()
