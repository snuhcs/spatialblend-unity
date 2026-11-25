# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Scene and source guards for the port of the v1 behaviour that drifted from main.

Text checks only: no Unity, no network.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SB = ROOT / "Assets/SpatialBlend"
SCENE = SB / "Samples/Scenes/FreeformCollaboration.unity"


def scene():
    return SCENE.read_text(encoding="utf-8")


class V1DriftPortGuards(unittest.TestCase):
    def test_the_dead_controller_button_mapper_is_gone_from_the_scene(self):
        # v1 deleted it: button 2 called ControlPanel.LoadGltfModel with no target.
        text = scene()
        self.assertNotIn("[BuildingBlock] Controller Buttons Mapper", text)
        self.assertNotIn("d376240d064f4484e81824d51391caf3", text)  # the mapper's script GUID

    def _guid(self, rel):
        meta = SB / (rel + ".meta")
        for line in meta.read_text().splitlines():
            if line.startswith("guid: "):
                return line.split()[1]
        raise AssertionError(rel)

    def test_the_vo_carousel_scripts_are_deleted_and_unreferenced(self):
        # As in v1: VOPanelController and MeshSpawner are gone, the
        # scenes no longer carry them, and ControlPanel has no VO canvas to toggle.
        for name, guid in (("VOPanelController", "fe2e1dbbbe5efcf4593c7449e287f736"),
                           ("MeshSpawner", "8b431fd6524239044a158cb60b172e4a")):
            self.assertEqual(list(SB.rglob(name + ".cs")), [], name)
            for scene_path in (SCENE, SB / "Samples/Scenes/Test.unity"):
                self.assertNotIn(guid, scene_path.read_text(encoding="utf-8"), f"{name} in {scene_path.name}")
        panel = (SB / "Samples/Scripts/UI/ControlPanel.cs").read_text(encoding="utf-8")
        self.assertNotIn("_VOPanelCanvas", panel)
        self.assertNotIn("_VOPanelCanvas", scene())

    def test_the_dead_scripts_v1_deleted_are_gone(self):
        # In v1 nothing referenced these three, and the commented populate call went with them.
        for name in ("SpatialAnchorSceneManager", "MeshInteractionController", "SpawnedModelTag"):
            self.assertEqual(list(SB.rglob(name + ".cs")), [], name)
        app = (SB / "Samples/Scripts/Freeform Collaboration/FreeformCollaborationApp.cs").read_text(encoding="utf-8")
        for needle in ("SpatialAnchorSceneManager", "PopulateSharedObjectsFromSpatialAnchors"):
            self.assertNotIn(needle, app)
        self.assertNotIn("_spatialAnchorSceneManager", scene())

    def test_table_and_globe_are_not_in_the_freeform_scene(self):
        # As in v1. The prefabs are not in the public repository (their art was
        # third-party); neither they nor a reference to their GUIDs may come back.
        for rel, guid in (("Samples/Objects/Table.prefab", "ffa66542d1097654ca1f6abb0b06ff76"),
                          ("Samples/Objects/Globe.prefab", "719c9ec43848b6e4c85c8642126aee12")):
            self.assertFalse((SB / rel).exists(), rel)
            self.assertNotIn(guid, scene(), rel)

    def test_the_avatar_spawner_has_no_entitlement_wait(self):
        # Every avatar is the preset (parity A7), so the entitlement wait and the Oculus id assignment reached
        # nothing; v1 main has neither.
        text = (SB / "Avatar/Scripts/SbAvatarSpawnerFusion.cs").read_text(encoding="utf-8")
        for needle in ("Entitlement", "PlatformInit", "PlatformInfo", "OculusId"):
            self.assertNotIn(needle, text)

    def test_no_meta_app_id_is_committed(self):
        # The app uses no Meta Platform service (preset avatars only, docs/required-credentials.md), so the public repository
        # names no Meta app: a developer who needs one sets their own in the Oculus Platform settings.
        text = (ROOT / "Assets/Resources/OculusPlatformSettings.asset").read_text(encoding="utf-8")
        self.assertRegex(text, r"(?m)^  ovrMobileAppID: ?$")
        self.assertRegex(text, r"(?m)^  ovrAppID: ?$")

    def test_the_avatar_fade_is_declared_at_one_and_a_half_seconds(self):
        # The user-study value, declared in the asset and the code default.
        asset = (ROOT / "Assets/Resources/SpatialBlendSettings.asset").read_text(encoding="utf-8")
        self.assertIn("  fadeAvatarDuration: 1.5\n", asset)
        code = (SB / "Framework/Scripts/SpatialBlendSettings.cs").read_text(encoding="utf-8")
        self.assertIn("public float fadeAvatarDuration = 1.5f;", code)

    def test_the_object_placement_motif_finds_its_ray_interactable_in_children(self):
        # As in v1: GetComponentInChildren, not GetComponentInParent.
        text = (SB / "Interaction/Scripts/Placement/ObjectPlacementMotif.cs").read_text(encoding="utf-8")
        self.assertIn("_rayInteractable = GetComponentInChildren<RayInteractable>();", text)
        self.assertNotIn("GetComponentInParent<RayInteractable>", text)

    def test_the_entity_does_not_auto_set_up_its_manipulation_motifs(self):
        # As in v1: the call is commented out (the prefabs carry their own motifs).
        text = (SB / "Framework/Scripts/SbEntity.cs").read_text(encoding="utf-8")
        body = text.split("private void SetupSourceEntity()", 1)[1].split("}", 1)[0]
        live = [line.strip() for line in body.splitlines()
                if line.strip() and not line.strip().startswith("//") and line.strip() != "{"]
        self.assertEqual(live, [], "SetupSourceEntity must not call SetupManipulationPanelMotifs")
        self.assertIn("// SetupManipulationPanelMotifs();", body)


class RoomLinkHasNoAddressGuards(unittest.TestCase):
    """No peer ever dials another peer's address; the core link rides the Photon session."""

    def read(self, rel):
        return (SB / rel).read_text(encoding="utf-8")

    def test_the_address_policy_and_its_ui_are_gone(self):
        self.assertEqual(list(SB.rglob("HostAddressPolicy.cs")), [])
        manager = self.read("Samples/Scripts/FusionConnectionManager.cs")
        for needle in ("ResolveHostIp", "ipify", "HostAddressPolicy", "AdvertisedAddress", "RequestConnect(",
                       "RpcConnectToHost", "UnityWebRequest", "System.Net"):
            self.assertNotIn(needle, manager, needle)
        panel = self.read("Samples/Scripts/UI/ControlPanel.cs")
        self.assertNotIn("AdvertisedAddress", panel)
        self.assertNotIn("AddressText", self.read("Samples/Scripts/UI/ControlPanelPresenters.cs"))
        settings = self.read("Framework/Scripts/SpatialBlendSettings.cs")
        for needle in ("hostAddressMode", "explicitHostAddress", "clientConnectAttempts"):
            self.assertNotIn(needle, settings, needle)

    def test_the_link_is_carried_as_fusion_reliable_data(self):
        manager = self.read("Samples/Scripts/FusionConnectionManager.cs")
        carrier = self.read("Samples/Scripts/FusionCoreLinkCarrier.cs")
        self.assertIn("SendReliableDataToPlayer", manager)
        self.assertIn("SendReliableDataToPlayer", carrier)
        self.assertIn("OnReliableDataReceived", carrier)
        self.assertIn("FusionCoreLinkReceiver", manager)
        self.assertIn("RequestHostTunneled()", manager)
        self.assertIn("RequestJoinTunneled()", manager)

    def test_the_player_cap_is_declared_and_passed_to_fusion(self):
        settings = self.read("Framework/Scripts/SpatialBlendSettings.cs")
        self.assertIn("public int maxPlayers = DefaultMaxPlayers;", settings)
        self.assertIn("DefaultMaxPlayers = 9;", settings)
        asset = (ROOT / "Assets/Resources/SpatialBlendSettings.asset").read_text(encoding="utf-8")
        self.assertIn("  maxPlayers: 9\n", asset)
        bootstrap = self.read("Samples/Scripts/SbFusionBootstrap.cs")
        self.assertIn("PlayerCount = ResolvePlayerCount(DeclaredSettings),", bootstrap)
        # The scene runs the subclass, not the SDK's own bootstrap.
        meta = (SB / "Samples/Scripts/SbFusionBootstrap.cs.meta").read_text()
        guid = [line.split()[1] for line in meta.splitlines() if line.startswith("guid: ")][0]
        self.assertIn(f"m_Script: {{fileID: 11500000, guid: {guid}, type: 3}}", scene())

    def test_the_dead_interaction_latency_setting_is_gone(self):
        # The 5000 ms policy lives in core (DefaultInteractionLatencyPolicy); the managed copy was dead.
        self.assertNotIn("maxInteractionLatencyMs", self.read("Framework/Scripts/SpatialBlendSettings.cs"))
        asset = (ROOT / "Assets/Resources/SpatialBlendSettings.asset").read_text(encoding="utf-8")
        self.assertNotIn("maxInteractionLatencyMs", asset)

    def test_the_direct_carrier_is_webrtc_pinned_and_declared(self):
        import json
        manifest = json.loads((ROOT / "Packages/manifest.json").read_text())
        self.assertEqual(manifest["dependencies"]["com.unity.webrtc"], "3.0.0")
        lock = json.loads((ROOT / "Packages/packages-lock.json").read_text())
        self.assertEqual(lock["dependencies"]["com.unity.webrtc"]["version"], "3.0.0")
        self.assertIn("Unity.WebRTC", (SB / "SpatialBlend.asmdef").read_text())
        settings = self.read("Framework/Scripts/SpatialBlendSettings.cs")
        for needle in ("public bool useWebRtc = true;", "public string[] stunServers = { DefaultStunServer };",
                       "DefaultWebRtcIceTimeoutSeconds = 5f;", "stun:stun.l.google.com:19302"):
            self.assertIn(needle, settings, needle)
        transport = self.read("Samples/Scripts/WebRtcPeerTransport.cs")
        self.assertIn("ordered = true", transport)
        # TURN credentials come from the Worker at run time (TurnIceProvider): none is written into the transport or the settings.
        self.assertNotIn("turn:", transport, "no TURN server URL is hardcoded: the Worker names them")
        self.assertNotIn("turns:", transport)
        self.assertNotIn("X-SpatialBlend-Client", transport)
        self.assertNotIn("X-SpatialBlend-Client", settings, "the client secret header is the provider's, and its value is never in source")

    def test_the_turn_client_secret_has_no_tracked_home(self):
        ignore = (ROOT / ".gitignore").read_text()
        self.assertIn("Assets/Resources/SpatialBlendTurnClientSecret*", ignore)
        settings = self.read("Framework/Scripts/SpatialBlendSettings.cs")
        self.assertNotIn("clientSecret", settings, "the secret is not a serialized setting")
        self.assertRegex(settings, r'DefaultTurnCredentialsUrl = "";', "no TURN Worker by default: you deploy your own")

    def test_the_sender_is_whoever_the_capability_token_was_issued_to(self):
        carrier = self.read("Samples/Scripts/FusionCoreLinkCarrier.cs")
        tokens = self.read("Samples/Scripts/LinkTokens.cs")
        manager = self.read("Samples/Scripts/FusionConnectionManager.cs")
        # The receiver resolves the token; nothing in the key, the body or the callback's PlayerRef names the sender.
        self.assertIn("_tokens.TryResolve(epoch, data.Array, data.Offset, data.Count, out int peer)", carrier)
        self.assertIn("var sender = PlayerRef.FromEncoded(peer);", carrier)
        self.assertNotIn("_authenticate", carrier)
        self.assertNotIn("player.RawEncoded ==", carrier)
        self.assertIn("RandomNumberGenerator.Create()", tokens)
        # The offerer of a token is Photon's RpcInfo.Source, not a parameter.
        self.assertIn("AcceptTokenOffer(info.Source, epoch, high, low)", manager)
        self.assertIn("[RpcTarget] PlayerRef target", manager)

    def test_the_binding_needs_the_tunnel_abi(self):
        api = self.read("Foundation/Internal/SpatialBlendAPI.cs")
        self.assertIn("internal const uint RequiredAbiMinor = 12;", api)
        tunnel = self.read("Foundation/Internal/SpatialBlendAPI.Tunnel.cs")
        for symbol in ("sb_host_space_tunneled", "sb_accept_tunnel", "sb_connect_space_tunneled",
                       "sb_tunnel_write", "sb_tunnel_read", "sb_tunnel_close"):
            self.assertIn(symbol, tunnel)


if __name__ == "__main__":
    unittest.main()
