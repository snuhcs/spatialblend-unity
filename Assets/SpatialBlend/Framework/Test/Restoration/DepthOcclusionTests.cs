// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Meta.XR.EnvironmentDepth;
using NUnit.Framework;
using Oculus.Avatar2;
using Meta.XR;
using SpatialBlend.Samples.FreeformCollaboration.Tutorial;
using SpatialBlend.Objects;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The Meta Depth API runs only when the OpenXR occlusion
    /// feature is on and an <see cref="EnvironmentDepthManager"/> is enabled, and it sets the global HARD_OCCLUSION /
    /// SOFT_OCCLUSION keyword that a shader must declare with <c>multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION</c> to read.
    /// These tests pin both halves: the feature and the one manager, and every virtual surface on a shader that reads the
    /// keyword. UI is excluded on purpose.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class DepthOcclusionTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";

        private const string ScenePermission = "com.oculus.permission.USE_SCENE";

        private static readonly Regex OcclusionPragma =
            new Regex(@"#pragma\s+(multi_compile|multi_compile_local|shader_feature)\w*\s+_\s+HARD_OCCLUSION\s+SOFT_OCCLUSION", RegexOptions.Compiled);

        private static readonly Regex Include = new Regex("#include\\s+\"([^\"]+)\"", RegexOptions.Compiled);

        // ---- the Depth API runs ----------------------------------------------------------------------------------

        [Test]
        public void SceneHasExactlyOneDepthManagerInSoftOcclusion()
        {
            // Two managers each fetch and preprocess the depth texture every frame (double cost), and the second trips the
            // single-instance assertion in Awake on a development build. The raycast building block's manager is the one.
            WithScene(scene =>
            {
                var managers = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<EnvironmentDepthManager>(true)).ToArray();
                Assert.That(managers, Has.Length.EqualTo(1));
                EnvironmentDepthManager manager = managers[0];
                Assert.That(manager.enabled, Is.True);
                Assert.That(manager.gameObject.activeInHierarchy, Is.True);
                Assert.That(manager.OcclusionShadersMode, Is.EqualTo(OcclusionShadersMode.SoftOcclusion),
                    "soft: hard cuts at the depth texture's resolution, which shimmers on an avatar's outline");
                Assert.That(manager.RemoveHands, Is.True, "the app draws its own hands and rays; a real hand must not hide them");
                Assert.That(manager.MaskMeshFilters, Is.Null.Or.Empty, "no surface is masked out of the occlusion");
            });
        }

        [Test]
        public void OpenXrOcclusionFeatureIsEnabledForQuest()
        {
            // Without 'Meta Quest: Occlusion' (XR_META_environment_depth) the OpenXR loader creates no occlusion subsystem,
            // EnvironmentDepthManager.IsSupported is false and the manager disables itself in OnEnable: no depth texture, no
            // keyword, no occlusion.
            string[] lines = File.ReadAllLines("Assets/XR/Settings/OpenXR Package Settings.asset");
            int at = Array.FindIndex(lines, l => l.Trim() == "m_Name: AROcclusionFeature Android");
            Assert.That(at, Is.GreaterThan(0));
            string enabled = lines.Skip(at).Take(6).First(l => l.Trim().StartsWith("m_enabled:", StringComparison.Ordinal));
            Assert.That(enabled.Trim(), Is.EqualTo("m_enabled: 1"));
            Assert.That(lines.Skip(at).Take(12).Any(l => l.Contains("com.unity.openxr.feature.arfoundation-meta-occlusion")), Is.True);

            // Meta's Project Setup Tool (ProjectSetupDepthAPI) and the feature's own validation rule require 'Meta Quest: Session'
            // with it; leaving it off is an OpenXR validation error.
            int session = Array.FindIndex(lines, l => l.Trim() == "m_Name: ARSessionFeature Android");
            Assert.That(session, Is.GreaterThan(0));
            Assert.That(lines.Skip(session).Take(6).First(l => l.Trim().StartsWith("m_enabled:", StringComparison.Ordinal)).Trim(), Is.EqualTo("m_enabled: 1"));
            // The Session feature depends on the OpenXR lifecycle feature, which Unity switches on with it.
            int lifecycle = Array.FindIndex(lines, l => l.Trim() == "m_Name: OpenXRLifeCycleFeature Android");
            Assert.That(lifecycle, Is.GreaterThan(0));
            Assert.That(lines.Skip(lifecycle).Take(6).First(l => l.Trim().StartsWith("m_enabled:", StringComparison.Ordinal)).Trim(), Is.EqualTo("m_enabled: 1"));
        }

        // ---- the permission --------------------------------------------------------------------------------------

        [Test]
        public void DepthStartsOnlyAfterTheScenePermission()
        {
            // The SDK's manager does not start the provider until Permission.HasUserAuthorizedPermission(USE_SCENE); it waits
            // in OnBeforeRender. The app requests it in SbPermissionManager's startup batch (OVRManager also asks, as before);
            // the depth code never asks itself (a second request replaces the first one's callbacks).
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.That(typeof(EnvironmentDepthManager).GetField("_hasPermission", instance), Is.Not.Null);
            Assert.That(typeof(EnvironmentDepthManager).GetMethod("OnBeforeRender", instance), Is.Not.Null);
            Assert.That(SbPermissionManager.RequestedPermissions, Does.Contain(ScenePermission));
            Assert.That(File.ReadAllText("Assets/Plugins/Android/AndroidManifest.xml"), Does.Contain("android:name=\"" + ScenePermission + "\""));
            WithScene(scene =>
            {
                Assert.That(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<SbPermissionManager>(true)).Count(), Is.EqualTo(1));
            });
        }

        // ---- the raycast -----------------------------------------------------------------------------------------

        [Test]
        public void DepthRaycastKeepsItsPriorityAndItsMrukFallback()
        {
            // With the Depth API running, IsDepthRaycastReady is true on a device (the scene has the raycast manager and the
            // depth manager), so depth is priority 1 as the utility documents; MRUK stays the fallback when depth is not ready,
            // misses, or hits a surface that is not ground (RaycastFloor) or wall (RaycastWall).
            WithScene(scene =>
            {
                var raycast = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<EnvironmentRaycastManager>(true)).ToArray();
                Assert.That(raycast, Has.Length.EqualTo(1));
                Assert.That(raycast[0].gameObject.activeInHierarchy, Is.True);
            });
            string utility = File.ReadAllText("Assets/SpatialBlend/Interaction/Scripts/Core/EnvironmentRaycastUtility.cs");
            Assert.That(utility, Does.Contain("return raycastMgr != null && raycastMgr.enabled && raycastMgr.gameObject.activeInHierarchy;"));
            Assert.That(utility, Does.Contain("// Priority 1: EnvironmentRaycastManager"));
            Assert.That(utility, Does.Contain("// Priority 2: MRUK"));
        }

        // ---- every virtual surface supports the keyword ----------------------------------------------------------

        [TestCase("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab")]
        [TestCase("Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab")]
        public void ObjectPrefabMaterialsSupportOcclusion(string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null, prefabPath);
            int checkedRenderers = 0;
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                // The hover glow around the whiteboard is selection feedback, like the outline: it shows only while the
                // board is pointed at, so it is not a surface that hides behind a wall.
                if (renderer.name == "_panelGlow") continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    Assert.That(material, Is.Not.Null, renderer.name + " has an empty material slot");
                    AssertSupportsOcclusion(material.shader, prefabPath + " / " + renderer.name + " / " + material.name);
                }
                checkedRenderers++;
            }
            Assert.That(checkedRenderers, Is.GreaterThan(0), prefabPath);
        }

        [Test]
        public void AvatarShaderConfigurationsUseTheOcclusionShader()
        {
            WithScene(scene =>
            {
                var managers = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<OvrAvatarShaderManagerSingle>(true)).ToArray();
                Assert.That(managers, Has.Length.EqualTo(1));
                var serialized = new SerializedObject(managers[0]);
                // Default: textured avatars. FastLoad: the avatar before its textures arrive (vertex colours); without
                // this one a remote avatar is not occluded for its first seconds.
                foreach (string field in new[] { "DefaultShaderConfigurationInitializer", "FastLoadConfigurationInitializer" })
                {
                    var configuration = serialized.FindProperty(field).objectReferenceValue as OvrAvatarShaderConfiguration;
                    Assert.That(configuration, Is.Not.Null, field);
                    Assert.That(configuration.Shader, Is.Not.Null, field);
                    Assert.That(configuration.Shader.name, Is.EqualTo("Avatar/Style2Meta_Occlusion"), field);
                    AssertSupportsOcclusion(configuration.Shader, field);
                }
            });
        }

        [Test]
        public void FastLoadAvatarConfigurationIsTheVertexMode()
        {
            string text = File.ReadAllText("Assets/SpatialBlend/Avatar/Scripts/MetaStyle2ShaderOcclusionConfigurationVertex.asset");
            string enabled = text.Substring(text.IndexOf("KeywordsToEnable:", StringComparison.Ordinal));
            enabled = enabled.Substring(0, enabled.IndexOf("NameFloatConstants:", StringComparison.Ordinal));
            Assert.That(enabled, Does.Contain("- MATERIAL_MODE_VERTEX"));
            Assert.That(enabled, Does.Not.Contain("- MATERIAL_MODE_TEXTURE"));
        }

        [Test]
        public void ScannedAndSharedObjectsLoadOnTheOcclusionShader()
        {
            Shader shader = Shader.Find(GltfOcclusionMaterial.ShaderName);
            Assert.That(shader, Is.Not.Null);
            AssertSupportsOcclusion(shader, GltfOcclusionMaterial.ShaderName);
            Assert.That(GltfOcclusionMaterial.CreateDefaultMaterial(shader).shader, Is.SameAs(shader));

            // Every runtime glTF import passes the occlusion material generator; a plain GltfImport would not occlude. Each
            // production file that creates a GltfImport must build its generator with GltfOcclusionMaterial.CreateGenerator and
            // pass it as the material generator on every such call.
            var offenders = new List<string>();
            int imports = 0;
            foreach (string file in Directory.GetFiles("Assets/SpatialBlend", "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/Test/") || normalized.Contains("/Editor/")) continue;
                string[] lines = File.ReadAllLines(file);
                bool generator = lines.Any(l => l.Contains("GltfOcclusionMaterial.CreateGenerator("));
                foreach (string line in lines.Where(l => l.Contains("new GltfImport(")))
                {
                    imports++;
                    if (!generator || !(line.Contains("materialGenerator:") || line.Contains("materials")) || line.Contains("new GltfImport()"))
                    {
                        offenders.Add(normalized + ": " + line.Trim());
                    }
                }
            }
            Assert.That(imports, Is.GreaterThan(0));
            Assert.That(offenders, Is.Empty, "a GltfImport without the occlusion material generator");
        }

        [Test]
        public void TutorialPracticePropsUseTheOcclusionMaterial()
        {
            var material = TutorialPracticeProps.OccludedMaterial;
            Assert.That(material, Is.Not.Null);
            AssertSupportsOcclusion(material.shader, TutorialPracticeProps.OccludedShaderName);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                TutorialPracticeProps.UseOccludedMaterial(cube);
                Assert.That(cube.GetComponent<Renderer>().sharedMaterial, Is.SameAs(material));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        [TestCase("Sb/Annotation/Line")]
        [TestCase("Sb/Annotation/Face")]
        [TestCase("Sb/OutlineGlow")]
        [TestCase("Sb/PlacementHologram")]
        public void AnnotationAndOutlineShadersSupportOcclusion(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            AssertSupportsOcclusion(shader, shaderName);
        }

        // The shaders found by name at runtime (Shader.Find) are in Always Included Shaders, or a device build strips them.
        [TestCase("f5fb6b6976a14d2d896425f2a1c39a45", "Sb/PlacementHologram")]
        public void ShadersFoundByNameAreAlwaysIncluded(string guid, string shaderName)
        {
            Assert.That(File.ReadAllText("ProjectSettings/GraphicsSettings.asset"), Does.Contain("guid: " + guid), shaderName);
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Does.EndWith(".shader"), shaderName);
        }

        // The scan preview has no outline glow, so nothing in the build finds Sb/OutlineGlow; only the
        // Local Simulator's legacy outline (Test scene, not in the build) still uses it, and it is not in Always Included Shaders.
        [Test]
        public void TheOutlineGlowShaderIsNoLongerAlwaysIncluded()
        {
            Assert.That(File.ReadAllText("ProjectSettings/GraphicsSettings.asset"), Does.Not.Contain("guid: e4dd5ee73ebf80945a50734c78c62d7e"));
            Assert.That(System.Type.GetType("SpatialBlend.Interactions.OutlineGlow, SpatialBlend"), Is.Null, "the unused Interaction copy is gone");
        }

        // The alpha a blend writes for a source alpha `a` over a destination alpha `dst`.
        private static float AlphaOver(string src, string dst, float a, float dstAlpha)
        {
            float F(string f) => f == "One" ? 1f : f == "Zero" ? 0f : f == "OneMinusSrcAlpha" ? 1f - a : f == "SrcAlpha" ? a : float.NaN;
            return F(src) * a + F(dst) * dstAlpha;
        }

        // On device, over the passthrough UNDERLAY the Quest compositor shows the passthrough by the eye buffer's ALPHA. A blend that
        // also blends alpha by itself ("Blend SrcAlpha OneMinusSrcAlpha") writes a * a, and a translucent surface turns almost
        // invisible on the headset (a 7.5% face fill became 0.6%). Every translucent shader that ships writes coverage instead:
        // separate alpha factors on every Blend (One OneMinusSrcAlpha for a blended pass, Zero One for an additive light pass).
        [TestCase("Assets/SpatialBlend/Interaction/Shaders/PlacementHologram.shader")]
        [TestCase("Assets/SpatialBlend/Interaction/Shaders/Annotation/AnnotationLine.shader")]
        [TestCase("Assets/SpatialBlend/Interaction/Shaders/Annotation/AnnotationFace.shader")]
        [TestCase("Assets/SpatialBlend/Interaction/Shaders/OutlineGlow.shader")]
        [TestCase("Assets/SpatialBlend/Avatar/Scripts/Style2Meta_Occlusion.shader")]
        [TestCase("Assets/SpatialBlend/Samples/Objects/Shaders/glTFPbrMetallicRoughnessOccluded.shader")]
        public void TranslucentShadersOverPassthroughWriteCoverageAlpha(string path)
        {
            var blends = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.StartsWith("Blend ") && !l.StartsWith("Blend Off")).ToList();
            Assert.That(blends, Is.Not.Empty, path);
            foreach (string blend in blends)
            {
                Assert.That(blend, Does.Contain(","), path + ": separate alpha factors (" + blend + ")");
                string[] colour = blend.Substring("Blend ".Length, blend.IndexOf(',') - "Blend ".Length).Trim().Split(' ');
                string alpha = blend.Substring(blend.IndexOf(',') + 1).Trim();
                if (colour[1].StartsWith("["))
                {
                    // A material-driven mode (glTF: opaque/cutout One Zero, blend SrcAlpha OneMinusSrcAlpha, premultiply One
                    // OneMinusSrcAlpha): the alpha destination is the colour's own, so an opaque or cutout mode writes its alpha as
                    // is (never blended over what is behind) and a translucent one accumulates coverage.
                    Assert.That(alpha, Is.EqualTo("One " + colour[1]), path + ": " + blend);
                    // The written alpha in each material mode, from the shader's own alpha factors with its [_DstBlend] resolved.
                    string[] af = alpha.Split(' ');
                    string Resolve(string f, string dst) => f == colour[1] ? dst : f;
                    Assert.That(AlphaOver(af[0], Resolve(af[1], "Zero"), 1f, 0.3f), Is.EqualTo(1f).Within(1e-6f),
                        path + ": opaque/cutout mode (dst Zero): alpha 1 over anything behind");
                    Assert.That(AlphaOver(af[0], Resolve(af[1], "OneMinusSrcAlpha"), 0.4f, 0f), Is.EqualTo(0.4f).Within(1e-6f),
                        path + ": blend mode: the coverage over passthrough, not a * a");
                }
                else if (colour[1] == "One")
                    Assert.That(alpha, Is.EqualTo("Zero One"), path + ": an additive light pass keeps the coverage (" + blend + ")");
                else
                {
                    Assert.That(alpha, Is.EqualTo("One OneMinusSrcAlpha"), path + ": a blended pass accumulates coverage (" + blend + ")");
                    Assert.That(colour[0], Is.Not.StartWith("["), path + ": no material-driven opaque mode in a fixed blend");
                }
            }
        }

        // ---- UI is excluded --------------------------------------------------------------------------------------

        [Test]
        public void UiCanvasesAreNotOccluded()
        {
            // Panels stay readable behind a wall or a table edge, and depth is unreliable inside 0.2 m of the headset where
            // a hand-held panel sits (Meta, Depth API overview). So no canvas prefab draws with an occlusion shader.
            var failures = new List<string>();
            int canvases = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/SpatialBlend" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponentInChildren<Canvas>(true) == null) continue;
                canvases++;
                foreach (Graphic graphic in prefab.GetComponentsInChildren<Graphic>(true))
                {
                    Material material = graphic.material;
                    if (material != null && material.shader != null && SupportsOcclusion(material.shader))
                    {
                        failures.Add(path + " / " + graphic.name + " / " + material.shader.name);
                    }
                }
            }
            Assert.That(canvases, Is.GreaterThan(0));
            Assert.That(failures, Is.Empty);
            Assert.That(SupportsOcclusion(Shader.Find("UI/Default")), Is.False);
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private static void AssertSupportsOcclusion(Shader shader, string what)
        {
            Assert.That(shader, Is.Not.Null, what);
            Assert.That(SupportsOcclusion(shader), Is.True,
                what + ": shader '" + shader.name + "' does not declare 'multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION', so the Depth API cannot occlude it");
        }

        /// <summary>True when the shader source, or a file it includes, declares the occlusion keyword pair as a multi_compile.</summary>
        private static bool SupportsOcclusion(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader", StringComparison.Ordinal)) return false;
            return FileDeclaresOcclusion(path, 0, new HashSet<string>());
        }

        private static bool FileDeclaresOcclusion(string assetPath, int depth, HashSet<string> seen)
        {
            if (depth > 3 || !seen.Add(assetPath)) return false;
            string full = ResolveOnDisk(assetPath);
            if (full == null || !File.Exists(full)) return false;
            string text = File.ReadAllText(full);
            if (OcclusionPragma.IsMatch(text)) return true;
            string directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
            foreach (Match match in Include.Matches(text))
            {
                string include = match.Groups[1].Value;
                string candidate = include.StartsWith("Assets/", StringComparison.Ordinal) || include.StartsWith("Packages/", StringComparison.Ordinal)
                    ? include
                    : directory + "/" + include;
                if (FileDeclaresOcclusion(candidate, depth + 1, seen)) return true;
            }
            return false;
        }

        /// <summary>The file for an asset path. A package asset lives in Library/PackageCache, not under Packages/.</summary>
        private static string ResolveOnDisk(string assetPath)
        {
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return Path.GetFullPath(assetPath);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package == null) return null;
            return Path.Combine(package.resolvedPath, assetPath.Substring(("Packages/" + package.name + "/").Length));
        }

        private static void WithScene(Action<Scene> body)
        {
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                body(scene);
            }
            finally
            {
                if (opened && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
#endif
