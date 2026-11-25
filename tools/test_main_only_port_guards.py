# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""Guards for the port of v1 main-only Freeform content (parity rows E1-E5 and D1-D5).

Nothing here starts Unity, opens a socket or reads data outside the repo. The checks are on text: asset GUIDs,
scene and ProjectSettings references, runtime source hooks, and the content that was deliberately not ported.
"""
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
SB = ASSETS / "SpatialBlend"
SCENE = SB / "Samples/Scenes/FreeformCollaboration.unity"
SFX_DIR = SB / "Samples/Resources/SFX"
INDICATORS = SB / "Samples/Scanning/Indicators"
SHADERS = SB / "Samples/Objects/Shaders"

# GUIDs are the ones v1 main carries. The three clips below have no .meta on main, so they got
# fresh GUIDs when they were ported; those are pinned here too so a later import cannot reassign them.
MAIN_GUIDS = {
    INDICATORS / "ScanPendingIndicator.prefab": "8d87d84733fae4868845895147b512fe",
    INDICATORS / "SharedObjectPendingIndicator.prefab": "aed90781036974ce99efdb9a4275a5de",
    SHADERS / "glTFPbrMetallicRoughnessOccluded.shader": "bf9c190b791c4c3b8745a02c83395800",
    SHADERS / "ShaderVariants.shadervariants": "0459652879068b34ca0ed3f2d7b8c03c",
    SHADERS / "glTFIncludes/glTFUnityStandardCoreForwardOccluded.cginc": "40349e14f0cb43bd8554e8b60dae81d3",
    ASSETS / "Resources/FreeformCollaboration_Splash.png": "0b8da14815c244240b8a8ce285062336",
    ASSETS / "Resources/FreeformCollaboration_Icon.png": "c5952c2707c6e7740b4fac7699857468",
    SFX_DIR / "RemoteAvatarArrived.wav": "bf40fa47236a92646924c8a1ff31fa7d",
    SFX_DIR / "RemoteObjectArrived.wav": "a9523377c2b557246a88eab1935f0a20",
    SFX_DIR / "ScanComplete.wav": "17a51b583e1ab3e4c802fff33a995a90",
    SFX_DIR / "ScanInProgress.wav": "185840ce04e4a0f418e8c4a1ff3dbb47",
    SFX_DIR / "ScanTrigger.wav": "c5cec1509f3a5a240b47e5a28bc6f018",
    SFX_DIR / "AnnotationCreated.wav": "d36f932535f14675b111f6cf53dec86d",
    SFX_DIR / "AnnotationPointPlaced.wav": "99663617c1bd4fb5a92ff0bba38c485e",
    SFX_DIR / "ObjectPlaced.wav": "2def787b63604dceb756cc128981250b",
}
SFX_NAMES = {
    "AnnotationCreated", "AnnotationPointPlaced", "ObjectPlaced", "RemoteAvatarArrived",
    "RemoteObjectArrived", "ScanComplete", "ScanInProgress", "ScanTrigger",
}
# The pending indicators are the repository's own (SpatialBlend.Editor.PendingIndicatorPrefabs); v1's Asset Store portals
# (Inguz "Beautiful Portals VFX", GUIDs below) cannot be redistributed and must not come back.
SCAN_INDICATOR = "8d87d84733fae4868845895147b512fe"
SHARED_INDICATOR = "aed90781036974ce99efdb9a4275a5de"
V1_PORTALS = ("17a2fbd8a68c544ebbf38b3979bddc90", "f9d3042601b9a4afcaa25e1eb372b61f")
INDICATOR_SCRIPT = "5c2b2fca4a8143099ddf40f5cd0f341a"
SPLASH = "0b8da14815c244240b8a8ce285062336"
ICON = "c5952c2707c6e7740b4fac7699857468"
GLTF_SHADER = "bf9c190b791c4c3b8745a02c83395800"
SHADER_VARIANTS = "0459652879068b34ca0ed3f2d7b8c03c"
GUID_REF = re.compile(r"guid: ([0-9a-f]{32})")


def read(path):
    return path.read_text(encoding="utf-8")


def guid_of(asset):
    meta = asset.with_name(asset.name + ".meta")
    match = re.search(r"^guid: ([0-9a-f]{32})$", read(meta), re.M)
    return match.group(1) if match else None


def project_guids():
    guids = {}
    for meta in ASSETS.rglob("*.meta"):
        try:
            match = re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(errors="replace")[:2000], re.M)
        except OSError:
            continue
        if match:
            guids[match.group(1)] = meta
    return guids


def runtime_sources():
    for path in SB.rglob("*.cs"):
        if "Test" in path.parts or "Editor" in path.parts:
            continue
        yield path


def tracked(*pathspec):
    out = subprocess.run(["git", "ls-files", "--", *pathspec], cwd=ROOT, capture_output=True, text=True)
    return [line for line in out.stdout.splitlines() if line]


class PortedAssetGuards(unittest.TestCase):
    def test_every_ported_asset_keeps_its_guid(self):
        wrong = []
        for asset, guid in MAIN_GUIDS.items():
            self.assertTrue(asset.is_file(), f"missing {asset.relative_to(ROOT)}")
            if guid_of(asset) != guid:
                wrong.append(asset.relative_to(ROOT).as_posix())
        self.assertEqual(wrong, [])

    def test_no_ported_guid_is_shared_by_two_assets(self):
        seen = {}
        clashes = []
        for meta in ASSETS.rglob("*.meta"):
            match = re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(errors="replace")[:2000], re.M)
            if not match:
                continue
            if match.group(1) in seen:
                clashes.append((seen[match.group(1)].relative_to(ROOT).as_posix(), meta.relative_to(ROOT).as_posix()))
            seen[match.group(1)] = meta
        self.assertEqual(clashes, [])

    def test_the_indicators_are_self_contained(self):
        """Every reference in the indicator prefabs and material resolves to a project asset or a Unity built-in."""
        known = project_guids()
        missing = []
        for path in INDICATORS.rglob("*"):
            if path.suffix not in (".prefab", ".mat"):
                continue
            for guid in set(GUID_REF.findall(read(path))):
                if not guid.startswith("0000000000000000") and guid not in known:
                    missing.append(f"{path.name}:{guid}")
        self.assertEqual(missing, [])
        self.assertEqual(sorted(p.name for p in INDICATORS.glob("*.prefab")), ["ScanPendingIndicator.prefab", "SharedObjectPendingIndicator.prefab"])
        for prefab in INDICATORS.glob("*.prefab"):
            self.assertIn("ParticleSystem:", read(prefab), prefab.name)

    def test_no_third_party_asset_store_package_is_in_the_repository(self):
        for folder in ("Beyond Limits Studio", "Dark UI", "NOT_Lonely", "Viverna_Assets", "Inguz Media Studio", "MRUKSamples", "MRMotifs"):
            # A developer may import an Asset Store package locally (git-ignored); none is ever tracked.
            self.assertEqual(tracked(f"Assets/{folder}"), [], folder)
        known = project_guids()
        for guid in V1_PORTALS:
            self.assertNotIn(guid, known)
            self.assertNotIn(guid, read(SCENE))


class SfxGuards(unittest.TestCase):
    def test_every_resources_load_of_a_clip_resolves(self):
        loads = set()
        for path in runtime_sources():
            loads.update(re.findall(r'Resources\.Load<AudioClip>\("SFX/([A-Za-z0-9_]+)"\)', read(path)))
        self.assertEqual(loads, SFX_NAMES)
        for name in sorted(loads):
            wav = SFX_DIR / f"{name}.wav"
            self.assertTrue(wav.is_file(), f"SFX/{name} does not resolve")
            self.assertGreater(wav.stat().st_size, 1000)
            self.assertTrue(wav.with_name(wav.name + ".meta").is_file())

    def test_resources_folders_are_force_tracked(self):
        """**/Resources/ is ignored in .gitignore, so these files only survive because they are tracked."""
        tracked_files = set(tracked("Assets/SpatialBlend/Samples/Resources", "Assets/Resources/FreeformCollaboration_*"))
        for name in SFX_NAMES:
            self.assertIn(f"Assets/SpatialBlend/Samples/Resources/SFX/{name}.wav", tracked_files)
            self.assertIn(f"Assets/SpatialBlend/Samples/Resources/SFX/{name}.wav.meta", tracked_files)
        self.assertIn("Assets/Resources/FreeformCollaboration_Splash.png", tracked_files)
        self.assertIn("Assets/Resources/FreeformCollaboration_Icon.png", tracked_files)

    def test_annotation_and_placement_motifs_play_their_clips(self):
        box = read(SB / "Interaction/Scripts/Annotation/Motifs/BoxAnnotationMotif.cs")
        self.assertEqual(box.count("PlaySfxAt(_pointPlacedClip"), 3)
        self.assertIn("PlaySfxAt(_annotationCreatedClip, boxPose.position)", box)
        for name in ("Placement/PanelPlacementMotif.cs", "Placement/ObjectPlacementMotif.cs"):
            motif = read(SB / "Interaction/Scripts" / name)
            self.assertIn('Resources.Load<AudioClip>("SFX/ObjectPlaced")', motif)
            self.assertIn("AudioSource.PlayClipAtPoint(_objectPlacedClip", motif)


class EffectGuards(unittest.TestCase):
    def test_scene_carries_one_indicator_manager_with_both_portals(self):
        scene = read(SCENE)
        docs = re.split(r"(?m)^--- !u!(\d+) &(\d+)\n", scene)
        ids = docs[2::3]
        self.assertEqual(len(ids), len(set(ids)), "duplicate fileIDs in the Freeform scene")
        managers = [docs[i + 2] for i in range(1, len(docs), 3)
                    if f"m_Script: {{fileID: 11500000, guid: {INDICATOR_SCRIPT}, type: 3}}" in docs[i + 2]]
        self.assertEqual(len(managers), 1)
        manager = managers[0]
        self.assertRegex(manager, rf"_sam3dPendingIndicatorPrefab: \{{fileID: 958579975441436448, guid: {SCAN_INDICATOR}")
        self.assertRegex(manager, rf"_sharedObjectPendingIndicatorPrefab: \{{fileID: 3523563567641294576, guid: {SHARED_INDICATOR}")
        # The manager's transform is a scene root, so the scene needs no parent that v2 removed.
        self.assertIn("  m_Father: {fileID: 0}", docs[docs.index("1386318049") + 1])
        self.assertIn("- {fileID: 1386318049}", scene.split("SceneRoots:")[1])

    def test_remote_content_load_shows_the_shared_object_portal(self):
        entity = read(SB / "Framework/Scripts/SbNativeEntity.cs")
        self.assertIn("FindAnyObjectByType<Sam3dPendingIndicatorManager>()", entity)
        # The portal is cleared even when the load throws or is retired.
        self.assertIn("RunWithSharedObjectPortal(", entity)
        manager = read(SB / "Samples/Scanning/Scripts/Sam3dPendingIndicatorManager.cs")
        # The portal is cleared in a finally, and only by the token that spawned it.
        self.assertRegex(manager, r"finally\s*\{\s*if \(token != null\) OnSharedObjectLoadCompleted")
        self.assertIn("ReferenceEquals(pendingFader, token)", manager)


class OcclusionShaderGuards(unittest.TestCase):
    def test_only_metallic_roughness_materials_are_swapped(self):
        helper = read(SB / "Framework/Scripts/NativeContent/GltfOcclusionMaterial.cs")
        self.assertIn('SourceShaderName = "glTF/PbrMetallicRoughness"', helper)
        self.assertIn("Overrides(material.shader)", helper)

    def test_occluded_shader_has_warmup_variants(self):
        variants = read(SHADERS / "ShaderVariants.shadervariants")
        self.assertIn(f"guid: {GLTF_SHADER}", variants)
        self.assertIn("HARD_OCCLUSION", variants)
        self.assertIn("SOFT_OCCLUSION", variants)

    def test_simple_switch_is_explained(self):
        include = read(SHADERS / "glTFIncludes/glTFUnityStandardCoreForwardOccluded.cginc")
        self.assertIn("UNITY_NO_FULL_STANDARD_SHADER", include)

    def test_gltf_shader_name_matches_the_loader(self):
        shader = read(SHADERS / "glTFPbrMetallicRoughnessOccluded.shader")
        helper = read(SB / "Framework/Scripts/NativeContent/GltfOcclusionMaterial.cs")
        name = re.search(r'Shader "([^"]+)"', shader).group(1)
        self.assertIn(f'ShaderName = "{name}"', helper)

    def test_both_gltf_import_paths_use_the_occlusion_generator(self):
        runtime = read(SB / "Framework/Scripts/NativeContent/GltfRuntimeLoader.cs")
        loader = read(SB / "Samples/Objects/Scripts/GltfLoader.cs")
        self.assertIn("GltfOcclusionMaterial.CreateGenerator()", runtime)
        self.assertEqual(loader.count("GltfOcclusionMaterial.CreateGenerator()"), 2)
        for text in (runtime, loader):
            self.assertNotIn("new GltfImport()", text)

    def test_shader_is_in_the_player_build(self):
        graphics = read(ROOT / "ProjectSettings/GraphicsSettings.asset")
        self.assertIn(f"guid: {GLTF_SHADER}", graphics.split("m_PreloadedShaders")[0])
        self.assertIn(f"guid: {SHADER_VARIANTS}", graphics.split("m_PreloadedShaders")[1])

    def test_shader_includes_resolve(self):
        text = read(SHADERS / "glTFPbrMetallicRoughnessOccluded.shader")
        for include in re.findall(r'#include "(Assets/[^"]+)"', text):
            self.assertTrue((ROOT / include).is_file(), include)

    def test_avatar_occlusion_shaders_were_never_dropped(self):
        """The v1 avatar occlusion shaders are present (row E3)."""
        avatar = SB / "Avatar/Scripts"
        for name in ("Style2Meta_Occlusion.shader", "Style2MetaAvatarCore_Occlusion.hlsl",
                     "MetaStyle2ShaderOcclusionConfiguration.asset", "LocalAvatarAppFunctions.hlsl"):
            self.assertTrue((avatar / name).is_file(), name)
        self.assertIn("_AlphaFade", read(avatar / "MetaAvatarEntity.cs"))


# Engine types a field may be declared as. Types the project derives from these (MonoBehaviour, ScriptableObject and
# so on) are found by reading the class declarations, so only the engine roots are listed here.
UNITY_OBJECT_ROOTS = {
    "Object", "Component", "Behaviour", "MonoBehaviour", "ScriptableObject", "GameObject", "Transform", "RectTransform",
    "Camera", "Light", "Renderer", "MeshRenderer", "SkinnedMeshRenderer", "LineRenderer", "MeshFilter", "Collider",
    "BoxCollider", "Rigidbody", "Animator", "AudioSource", "AudioClip", "AudioListener", "Material", "Shader", "Texture",
    "Texture2D", "RenderTexture", "Sprite", "Mesh", "Canvas", "CanvasGroup", "Graphic", "Image", "RawImage", "Text",
    "Button", "Toggle", "Slider", "Selectable", "TMP_Text", "TextMeshProUGUI", "ParticleSystem",
}
FIELD_DECL = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|readonly|new)\s+)*"
    r"([A-Za-z_][A-Za-z0-9_.]*)\s+(_?[A-Za-z][A-Za-z0-9_]*)\s*(?:=[^;]*)?;", re.M)
CLASS_DECL = re.compile(r"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]*>)?\s*:\s*([^{\n]+)")


def unity_types(sources):
    """Engine roots plus every project class that derives from one, to a fixed point."""
    known = set(UNITY_OBJECT_ROOTS)
    bases = {}
    for text in sources:
        for name, base_list in CLASS_DECL.findall(text):
            bases.setdefault(name, set()).update(b.strip().split("<")[0].split(".")[-1] for b in base_list.split(","))
    changed = True
    while changed:
        changed = False
        for name, parents in bases.items():
            if name not in known and parents & known:
                known.add(name)
                changed = True
    return known


# Reviewed v1 sites left as they are. Each reads a Unity object only to format a log message, or reads a plain
# accessor of a manipulation context that outlives the call. A new site is not allowed without an entry here.
NULL_CONDITIONAL_ALLOWED = {
    "SbNetworkEntity.cs": {"newObject"},            # log message
    "SbNetworkAvatarEntity.cs": {"newObject"},      # log message
    "Interaction.cs": {"sourceObject"},               # ToString
    "AnnotationMotifBase.cs": {"_centerEyeAnchor", "_raycastAnchor"},  # log message
    "ManipulationContext.cs": {"_targetObject"},      # log messages and a parent lookup
    "ManipulationPopUp.cs": {"_context"},             # read-only accessors
    "ManipulationPanelMotif.cs": {"_context"},        # read-only accessor
    "AffordanceAnnotationMotif.cs": {"_context"},     # read-only accessor
}


class UnityObjectNullGuards(unittest.TestCase):
    """?? = and ?. skip Unity's overloaded null check, so a destroyed object is treated as alive."""

    def sources(self):
        files = []
        for root in (SB, ASSETS / "Samples/Scripts"):
            if root.is_dir():
                files.extend(p for p in root.rglob("*.cs") if "Test" not in p.parts)
        return files

    def test_no_null_conditional_on_unity_object_fields(self):
        files = self.sources()
        texts = {p: read(p) for p in files}
        types = unity_types(texts.values())
        hits = []
        for path, text in texts.items():
            fields = {name for typ, name in FIELD_DECL.findall(text) if typ.split(".")[-1] in types}
            for number, line in enumerate(text.splitlines(), 1):
                code = line.split("//")[0]
                for name in fields - NULL_CONDITIONAL_ALLOWED.get(path.name, set()):
                    if re.search(rf"(?<![A-Za-z0-9_.]){re.escape(name)}\s*(\?\.|\?\?=)", code):
                        hits.append(f"{path.relative_to(ROOT).as_posix()}:{number}: {line.strip()}")
        self.assertEqual(hits, [], "?. or ??= on a UnityEngine.Object field; use an explicit == null check")

    def test_known_types_include_project_components(self):
        types = unity_types(read(p) for p in self.sources())
        for name in ("SpatialBlendManager", "SpatialBlendLocalWorld", "SpatialBlendObject", "ChessContentView", "AudioClip"):
            self.assertIn(name, types)


class QuestBrandingGuards(unittest.TestCase):
    def test_openxr_settings_carry_the_system_splash(self):
        settings = read(ASSETS / "XR/Settings/OpenXR Package Settings.asset")
        self.assertIn(f"systemSplashScreen: {{fileID: 2800000, guid: {SPLASH}, type: 3}}", settings)

    def test_postprocessor_resets_the_background_after_ovr(self):
        source = read(SB / "Editor/SbSplashBackground.cs")
        self.assertIn("IPostGenerateGradleAndroidProject", source)
        self.assertIn('Value = "passthrough-contextual"', source)
        self.assertIn("vr_splash.png", source)
        self.assertIn("Assets/Resources/FreeformCollaboration_Splash.png", source)
        order = int(re.search(r"callbackOrder => (\d+)", source).group(1))
        self.assertGreater(order, 99999)  # OVRGradleGeneration


    def test_player_settings_use_the_splash_and_icon(self):
        settings = read(ROOT / "ProjectSettings/ProjectSettings.asset")
        self.assertIn(f"m_VirtualRealitySplashScreen: {{fileID: 2800000, guid: {SPLASH}, type: 3}}", settings)
        self.assertIn(f"androidSplashScreen: {{fileID: 2800000, guid: {SPLASH}, type: 3}}", settings)
        self.assertIn(f"m_Icon: {{fileID: 2800000, guid: {ICON}, type: 3}}", settings)
        self.assertEqual(guid_of(ASSETS / "Resources/FreeformCollaboration_Splash.png"), SPLASH)

    def test_manifest_asks_for_the_os_splash(self):
        manifest = read(ASSETS / "Plugins/Android/AndroidManifest.xml")
        self.assertIn('<meta-data android:name="com.oculus.ossplash" android:value="true" />', manifest)
        # The permissions the APK check requires are still there.
        for permission in ("android.permission.CAMERA", "android.permission.RECORD_AUDIO",
                           "horizonos.permission.HEADSET_CAMERA", "com.oculus.permission.USE_SCENE"):
            self.assertIn(permission, manifest)


class DroppedContentGuards(unittest.TestCase):
    """Rows D1-D5 record content that was reviewed and left on main. These keep it from coming back by accident."""

    def test_box_annotation_shader_stays_out(self):
        self.assertFalse((SB / "Interaction/Shaders/BoxAnnotation.shader").exists())
        for path in runtime_sources():
            self.assertNotIn("Sb/BoxAnnotation", read(path))

    def test_input_handler_stays_on_both(self):
        """Legacy UnityEngine.Input callers remain (CameraController, MRUK samples), so Input System only would throw."""
        settings = read(ROOT / "ProjectSettings/ProjectSettings.asset")
        self.assertRegex(settings, r"(?m)^  activeInputHandler: 2$")

    def test_the_local_simulator_is_not_in_the_repository(self):
        # The old local simulator (Local Simulator scripts, the Tangible scenes) stayed in the private repository.
        self.assertEqual(tracked("Assets/SpatialBlend/Samples/Scripts/Local Simulator"), [])
        self.assertEqual([p for p in tracked("Assets/SpatialBlend/Samples/Scenes") if "Tangible" in p], [])

    def test_no_dataset_or_run_output_is_tracked_under_test_data(self):
        self.assertEqual(tracked("Assets/SpatialBlend/Samples/Objects/TestData"), [])
        ignore = read(ROOT / ".gitignore")
        for pattern in ("*.json", "*.glb", "*.meta"):
            self.assertIn(f"Assets/SpatialBlend/Samples/Objects/TestData/{pattern}", ignore)

    def test_no_photon_settings_are_tracked_by_this_port(self):
        self.assertEqual([p for p in tracked("Assets") if "PhotonAppSettings" in p], [])

    def test_gizmo_ready_to_share_stays_out_of_the_v2_core(self):
        for path in runtime_sources():
            text = read(path)
            self.assertNotIn("MarkReadyToShare", text, path.name)
            self.assertNotIn("IsReadyToShare", text, path.name)


class TextGuards(unittest.TestCase):
    def test_new_text_files_use_lf(self):
        files = [
            ROOT / "tools/test_main_only_port_guards.py",
            SB / "Framework/Scripts/NativeContent/GltfOcclusionMaterial.cs",
            SB / "Editor/SbSplashBackground.cs",
            SB / "Framework/Test/Restoration/PresentationPortContractTests.cs",
            ROOT / "docs/freeform-v1-parity.md",
        ]
        # The parity record is a private design note, absent from the public tree.
        files = [path for path in files if path.is_file() or path.name != "freeform-v1-parity.md"]
        for path in files:
            self.assertTrue(path.is_file(), path)
            self.assertNotIn(b"\r", path.read_bytes(), path.name)


if __name__ == "__main__":
    unittest.main()
