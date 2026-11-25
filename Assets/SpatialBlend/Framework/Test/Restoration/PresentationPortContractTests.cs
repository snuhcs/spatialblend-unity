// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SpatialBlend.Objects;
using SpatialBlend.Samples.Scanning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using SpatialBlend.Samples.FreeformCollaboration.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T6 port of v1 main-only Freeform content (parity rows E1 to E4). The effects, clips, shader and
    /// branding load as assets in the real project; none of it needs the core, Fusion or a device.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class PresentationPortContractTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private const string PortalFolder = "Assets/SpatialBlend/Samples/Scanning/Indicators/";
        private const string GltfShaderGuid = "bf9c190b791c4c3b8745a02c83395800";
        private const string ShaderVariantsGuid = "0459652879068b34ca0ed3f2d7b8c03c";
        private const string SplashPath = "Assets/Resources/FreeformCollaboration_Splash.png";
        private const string IconPath = "Assets/Resources/FreeformCollaboration_Icon.png";

        private static readonly string[] ClipNames =
        {
            "AnnotationCreated", "AnnotationPointPlaced", "ObjectPlaced", "RemoteAvatarArrived",
            "RemoteObjectArrived", "ScanComplete", "ScanInProgress", "ScanTrigger",
        };

        // E1: the Freeform scene carries the indicator manager. Its two indicators are the repository's own particle prefabs
        //     (SpatialBlend.Editor.PendingIndicatorPrefabs), one for the scan the user starts and one for an object that arrives
        //     from another participant.
        [Test]
        public void SceneCarriesTheIndicatorManagerWithBothPortals()
        {
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                var managers = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Sam3dPendingIndicatorManager>(true))
                    .ToArray();
                Assert.That(managers.Length, Is.EqualTo(1));
                var serialized = new SerializedObject(managers[0]);
                var scanPortal = serialized.FindProperty("_sam3dPendingIndicatorPrefab").objectReferenceValue as GameObject;
                var sharedPortal = serialized.FindProperty("_sharedObjectPendingIndicatorPrefab").objectReferenceValue as GameObject;
                Assert.That(scanPortal, Is.Not.Null);
                Assert.That(sharedPortal, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(scanPortal), Is.EqualTo(PortalFolder + "ScanPendingIndicator.prefab"));
                Assert.That(AssetDatabase.GetAssetPath(sharedPortal), Is.EqualTo(PortalFolder + "SharedObjectPendingIndicator.prefab"));
                Assert.That(scanPortal.GetComponentsInChildren<ParticleSystem>(true), Is.Not.Empty);
                Assert.That(sharedPortal.GetComponentsInChildren<ParticleSystem>(true), Is.Not.Empty);
            }
            finally
            {
                if (opened && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        // E2: every clip a runtime script loads from Resources resolves. A missing clip is silent at run time.
        [Test]
        public void EveryReferencedSfxClipResolves()
        {
            foreach (string name in ClipNames)
            {
                AudioClip clip = Resources.Load<AudioClip>("SFX/" + name);
                Assert.That(clip, Is.Not.Null, "SFX/" + name);
                Assert.That(clip.length, Is.GreaterThan(0.05f), "SFX/" + name);
            }
        }

        // E3: imported GLB materials take the occlusion shader, and the shader is in the player build.
        [Test]
        public void GltfOcclusionShaderCompilesAndOverridesImportedMaterials()
        {
            Shader shader = Shader.Find(GltfOcclusionMaterial.ShaderName);
            Assert.That(shader, Is.Not.Null);
            Assert.That(shader.isSupported, Is.True);
            Assert.That(ShaderUtil.GetShaderMessages(shader)
                .Where(m => m.severity.ToString() == "Error"), Is.Empty);

            // The default material is shared, so it is not destroyed here.
            Material material = GltfOcclusionMaterial.CreateDefaultMaterial(shader);
            Assert.That(material.shader, Is.SameAs(shader));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader)), Is.EqualTo(GltfShaderGuid));

            string graphics = File.ReadAllText("ProjectSettings/GraphicsSettings.asset");
            Assert.That(graphics.Split(new[] { "m_PreloadedShaders" }, System.StringSplitOptions.None)[0],
                Does.Contain("guid: " + GltfShaderGuid));
            Assert.That(graphics, Does.Contain("guid: " + ShaderVariantsGuid));
        }

        // E3: a missing shader falls back to the glTFast materials instead of failing the import.
        [Test]
        public void MissingOcclusionShaderFallsBackToDefaultMaterials()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Shader not found"));
            Material material = GltfOcclusionMaterial.CreateDefaultMaterial(null);
            Assert.That(material, Is.Not.Null);
            Assert.That(material.shader.name, Does.Not.Contain("Occluded"));
        }

        // ---- Portal re-entry and failure (E1) ----

        private GameObject _managerObject;

        [TearDown]
        public void TearDownPortals()
        {
            foreach (IndicatorFader fader in UnityEngine.Object.FindObjectsByType<IndicatorFader>(FindObjectsSortMode.None))
            {
                UnityEngine.Object.DestroyImmediate(fader.gameObject);
            }
            if (_managerObject != null) UnityEngine.Object.DestroyImmediate(_managerObject);
            _managerObject = null;
        }

        private Sam3dPendingIndicatorManager NewManager()
        {
            _managerObject = new GameObject("portal-manager");
            var manager = _managerObject.AddComponent<Sam3dPendingIndicatorManager>();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty("_sharedObjectPendingIndicatorPrefab").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<GameObject>(PortalFolder + "SharedObjectPendingIndicator.prefab");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return manager;
        }

        private static readonly Vector3 Here = new Vector3(1f, 2f, 3f);

        [Test]
        public void ThrowingLoadSpawnsThenClearsThePortal()
        {
            var manager = NewManager();
            bool shownDuringLoad = false;
            Task<bool> task = manager.RunWithSharedObjectPortal(7, "owner", Here, Quaternion.identity, () =>
            {
                shownDuringLoad = manager.HasSharedObjectPendingIndicator(7);
                return Task.FromException<bool>(new InvalidOperationException("load failed"));
            });
            Assert.That(shownDuringLoad, Is.True);
            Assert.That(task.IsFaulted, Is.True);
            Assert.That(manager.HasSharedObjectPendingIndicator(7), Is.False);
        }

        [Test]
        public void FailedLoadSpawnsThenClearsThePortal()
        {
            var manager = NewManager();
            bool shownDuringLoad = false;
            Task<bool> task = manager.RunWithSharedObjectPortal(8, "owner", Here, Quaternion.identity, () =>
            {
                shownDuringLoad = manager.HasSharedObjectPendingIndicator(8);
                return Task.FromResult(false);
            });
            Assert.That(shownDuringLoad, Is.True);
            Assert.That(task.Result, Is.False);
            Assert.That(manager.HasSharedObjectPendingIndicator(8), Is.False);
        }

        [Test]
        public void StaleLoadCompletionDoesNotFadeTheNewerPortal()
        {
            var manager = NewManager();
            var first = new TaskCompletionSource<bool>();
            var second = new TaskCompletionSource<bool>();
            Task<bool> staleLoad = manager.RunWithSharedObjectPortal(9, "owner", Here, Quaternion.identity, () => first.Task);
            Task<bool> currentLoad = manager.RunWithSharedObjectPortal(9, "owner", Here, Quaternion.identity, () => second.Task);
            Assert.That(manager.HasSharedObjectPendingIndicator(9), Is.True);

            first.SetResult(true); // the older load finishes while the newer one is still running
            Assert.That(staleLoad.IsCompleted, Is.True);
            Assert.That(manager.HasSharedObjectPendingIndicator(9), Is.True,
                "A stale completion cleared the portal that belongs to the newer load.");

            second.SetResult(true);
            Assert.That(currentLoad.IsCompleted, Is.True);
            Assert.That(manager.HasSharedObjectPendingIndicator(9), Is.False);
        }

        [Test]
        public void SpawnReturnsTheTokenAndATokenCompletesOnlyItsOwnEntry()
        {
            var manager = NewManager();
            IndicatorFader older = manager.SpawnSharedObjectPendingIndicator(11, Here);
            IndicatorFader newer = manager.SpawnSharedObjectPendingIndicator(11, Here);
            Assert.That(newer, Is.Not.Null);
            manager.OnSharedObjectLoadCompleted(11, older);
            Assert.That(manager.HasSharedObjectPendingIndicator(11), Is.True);
            manager.OnSharedObjectLoadCompleted(11, newer);
            Assert.That(manager.HasSharedObjectPendingIndicator(11), Is.False);
            Assert.That(manager.SpawnSharedObjectPendingIndicator(0, Here), Is.Null);
        }

        // ---- A real GLB through GltfRuntimeLoader (E3) ----

        /// <summary>
        /// A minimal textured binary glTF built in memory (one quad, one glTF/PbrMetallicRoughness material whose
        /// baseColorTexture is a 2 x 2 PNG): the shape of a scan result, with no third-party model in the repository.
        /// </summary>
        internal static byte[] TexturedQuadGlb()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels32(new[] { new Color32(200, 40, 40, 255), new Color32(40, 200, 40, 255),
                new Color32(40, 40, 200, 255), new Color32(220, 220, 40, 255) });
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);

            float[] positions = { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f };
            float[] uvs = { 0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f };
            ushort[] indices = { 0, 1, 2, 0, 2, 3 };
            var bin = new MemoryStream();
            var writer = new BinaryWriter(bin);
            foreach (float f in positions) writer.Write(f);
            foreach (float f in uvs) writer.Write(f);
            foreach (ushort i in indices) writer.Write(i);
            int indexEnd = (int)bin.Length;
            while (bin.Length % 4 != 0) writer.Write((byte)0);
            int pngOffset = (int)bin.Length;
            writer.Write(png);
            while (bin.Length % 4 != 0) writer.Write((byte)0);
            byte[] binChunk = bin.ToArray();

            string json =
                "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":2,\"material\":0}]}]," +
                "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0,\"roughnessFactor\":1}}]," +
                "\"textures\":[{\"source\":0}],\"images\":[{\"bufferView\":3,\"mimeType\":\"image/png\"}]," +
                "\"buffers\":[{\"byteLength\":" + binChunk.Length + "}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":48},{\"buffer\":0,\"byteOffset\":48,\"byteLength\":32}," +
                "{\"buffer\":0,\"byteOffset\":80,\"byteLength\":" + (indexEnd - 80) + "},{\"buffer\":0,\"byteOffset\":" + pngOffset +
                ",\"byteLength\":" + png.Length + "}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}," +
                "{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"}," +
                "{\"bufferView\":2,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]}";
            byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            int jsonPadded = (jsonBytes.Length + 3) / 4 * 4;

            var glb = new MemoryStream();
            var w = new BinaryWriter(glb);
            w.Write(0x46546C67u);                                   // "glTF"
            w.Write(2u);
            w.Write((uint)(12 + 8 + jsonPadded + 8 + binChunk.Length));
            w.Write((uint)jsonPadded);
            w.Write(0x4E4F534Au);                                   // "JSON"
            w.Write(jsonBytes);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) w.Write((byte)0x20);
            w.Write((uint)binChunk.Length);
            w.Write(0x004E4942u);                                   // "BIN\0"
            w.Write(binChunk);
            return glb.ToArray();
        }

        [UnityTest]
        public IEnumerator ImportedTexturedGlbUsesTheOcclusionShaderAndKeepsItsBaseColorTexture()
        {
            byte[] glb = TexturedQuadGlb();
            var target = new GameObject("glb-target");
            try
            {
                Task<bool> load = GltfRuntimeLoader.LoadModelFromBytes(target, glb);
                // A wall-clock deadline: EditMode frames can run much faster than glTFast's texture decode.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!load.IsCompleted && clock.Elapsed.TotalSeconds < 90) yield return null;
                Assert.That(load.IsCompleted, Is.True, "The GLB import did not complete in 90 s.");
                Assert.That(load.Result, Is.True, "The GLB import returned false.");

                var materials = target.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials).Where(m => m != null).ToArray();
                Assert.That(materials, Is.Not.Empty);
                foreach (Material material in materials)
                {
                    Assert.That(material.shader.name, Is.EqualTo(GltfOcclusionMaterial.ShaderName));
                }
                Assert.That(materials.Any(m => m.HasProperty("baseColorTexture") && m.GetTexture("baseColorTexture") != null),
                    Is.True, "baseColorTexture did not survive the shader switch.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        // An unlit or specular-glossiness material switched to the metallic-roughness occlusion shader would lose
        // its colour and render black, so only glTF/PbrMetallicRoughness is overridden.
        [Test]
        public void OnlyMetallicRoughnessMaterialsAreOverridden()
        {
            Shader metallic = Shader.Find("glTF/PbrMetallicRoughness");
            Shader unlit = Shader.Find("glTF/Unlit");
            Shader specGloss = Shader.Find("glTF/PbrSpecularGlossiness");
            // A null shader would make Overrides(null) false and hide a missing shader, so the lookups are asserted.
            Assert.That(metallic, Is.Not.Null, "glTF/PbrMetallicRoughness not found");
            Assert.That(unlit, Is.Not.Null, "glTF/Unlit not found");
            Assert.That(specGloss, Is.Not.Null, "glTF/PbrSpecularGlossiness not found");
            Assert.That(GltfOcclusionMaterial.Overrides(metallic), Is.True);
            Assert.That(GltfOcclusionMaterial.Overrides(unlit), Is.False);
            Assert.That(GltfOcclusionMaterial.Overrides(specGloss), Is.False);
            Assert.That(GltfOcclusionMaterial.Overrides(null), Is.False);
        }

        // E4: the OS splash background survives OVRManifestPreprocessor, which rewrites it from a gitignored asset.
        [Test]
        public void SbSplashBackgroundPostprocessorSetsPassthroughContextual()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SpatialBlend.Editor.SbSplashBackground")).FirstOrDefault(x => x != null);
            Assert.That(type, Is.Not.Null);
            var apply = type.GetMethod("Apply", new[] { typeof(System.Xml.XmlDocument) });
            const string ns = "http://schemas.android.com/apk/res/android";
            var manifest = new System.Xml.XmlDocument();
            manifest.LoadXml("<manifest xmlns:android=\"" + ns + "\"><application>"
                + "<meta-data android:name=\"com.oculus.ossplash.background\" android:value=\"black\" />"
                + "</application></manifest>");
            Assert.That((bool)apply.Invoke(null, new object[] { manifest }), Is.True);
            var node = (System.Xml.XmlElement)manifest.SelectSingleNode("/manifest/application/meta-data");
            Assert.That(node.GetAttribute("value", ns), Is.EqualTo("passthrough-contextual"));
            Assert.That((bool)apply.Invoke(null, new object[] { manifest }), Is.False, "already set: no change");

            var missing = new System.Xml.XmlDocument();
            missing.LoadXml("<manifest xmlns:android=\"" + ns + "\"><application /></manifest>");
            Assert.That((bool)apply.Invoke(null, new object[] { missing }), Is.True);
            Assert.That(missing.SelectNodes("/manifest/application/meta-data").Count, Is.EqualTo(1));
        }

        [Test]
        public void OpenXrSettingsCarryTheSystemSplash()
        {
            string settings = File.ReadAllText("Assets/XR/Settings/OpenXR Package Settings.asset");
            Assert.That(settings, Does.Contain("systemSplashScreen: {fileID: 2800000, guid: "
                + AssetDatabase.AssetPathToGUID(SplashPath)));
        }

        // E4: the Quest splash and the app icon are Freeform's own images.
        [Test]
        public void PlayerSettingsUseTheFreeformSplashAndIcon()
        {
            Texture2D splash = AssetDatabase.LoadAssetAtPath<Texture2D>(SplashPath);
            Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            Assert.That(splash, Is.Not.Null);
            Assert.That(icon, Is.Not.Null);

            string settings = File.ReadAllText("ProjectSettings/ProjectSettings.asset");
            string splashGuid = AssetDatabase.AssetPathToGUID(SplashPath);
            string iconGuid = AssetDatabase.AssetPathToGUID(IconPath);
            Assert.That(settings, Does.Contain("m_VirtualRealitySplashScreen: {fileID: 2800000, guid: " + splashGuid));
            Assert.That(settings, Does.Contain("androidSplashScreen: {fileID: 2800000, guid: " + splashGuid));
            Assert.That(settings, Does.Contain("m_Icon: {fileID: 2800000, guid: " + iconGuid));

            Texture2D[] defaultIcons = PlayerSettings.GetIcons(
                UnityEditor.Build.NamedBuildTarget.Unknown, IconKind.Application);
            Assert.That(defaultIcons, Does.Contain(icon));
        }
    }
}
#endif
