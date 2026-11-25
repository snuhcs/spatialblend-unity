// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GLTFast;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Objects;
using SpatialBlend.Samples.Objects;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A scanned object is normalised, scaled to the user's box and placed as v1 did (v1
    /// GltfLoader.NormalizeModel and ApplyTransform). The paper recovers metric scale from the box
    /// the user draws (SpatialBlend section 6, footnote). The fixture is one synthetic triangle in a GLB, and
    /// the expected numbers are worked out by hand in each test, not read back from the code under test.
    ///
    /// The triangle's bounds in the file are x in [0.4, 0.6], y in [-0.1, 0.3], z in [-0.35, -0.25]. glTFast
    /// mirrors X into Unity's left-handed frame, so the Unity mesh bounds are centre (-0.5, 0.1, -0.3) and size
    /// (0.2, 0.4, 0.1). The longest side is 0.4, so v1 normalises by 1 / 0.4 = 2.5 and puts the geometry at
    /// -centre * 2.5 = (1.25, -0.25, 0.75). The normalised height is 0.4 * 2.5 = 1.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class ReconstructedObjectFitTests
    {
        private const float Tolerance = 1e-4f;
        private string _directory;
        private GameObject _loaderHost;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "scan-object-fit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _loaderHost = new GameObject("scan-loader");
        }

        [TearDown]
        public void TearDown()
        {
            if (_loaderHost != null) UnityEngine.Object.DestroyImmediate(_loaderHost);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        // normalisation

        [UnityTest]
        public IEnumerator NormalisationCentresTheMeshAndMakesTheLongestSideOne()
        {
            GameObject root = null;
            yield return Await(LoadAsSource("normalised"), r => root = r);
            try
            {
                ScanObjectFit.LiftWorldNode(root);
                ScanObjectFit.Normalization info = ScanObjectFit.Normalize(root);

                Assert.That(info.MaxExtent, Is.EqualTo(0.4f).Within(Tolerance));
                AssertVector(info.MeshCenter, new Vector3(-0.5f, 0.1f, -0.3f));
                Transform geometry = root.transform.Find(ScanObjectFit.GeometryNodeName);
                Assert.That(geometry, Is.Not.Null, "The world node's children moved up to the root.");
                AssertVector(geometry.localPosition, new Vector3(1.25f, -0.25f, 0.75f));
                AssertVector(geometry.localScale, Vector3.one * 2.5f);
                Assert.That(ScanObjectFit.MeshWorldHeight(root), Is.EqualTo(1f).Within(Tolerance));
                Bounds bounds = root.GetComponentInChildren<Renderer>().bounds;
                AssertVector(bounds.center, Vector3.zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // box-fit scale

        [UnityTest]
        public IEnumerator TheBoxFixesTheMetricScaleAndThePlace()
        {
            // As v1: fullScale = box height / normalised height = 0.6 / 1. The root sits at the box
            // centre, unrotated in the Editor. The source then sees the geometry as a
            // preview, 0.3 of that size and lifted; the root,
            // the shared scale and so the peers' copy stay at the box-fit size.
            GameObject root = null;
            yield return Await(LoadAsSource("boxed"), r => root = r);
            try
            {
                var box = new ScanRegionHint(new Vector3(1f, 0.3f, 2f), new Vector3(0.4f, 0.6f, 1f));
                GltfLoader loader = _loaderHost.AddComponent<GltfLoader>();

                loader.SetupLoadedObject(root, true, ModelPath("boxed"), box, useServerPose: false);

                AssertVector(root.transform.position, new Vector3(1f, 0.3f, 2f));
                AssertVector(root.transform.localScale, Vector3.one * 0.6f);
                Assert.That(Quaternion.Angle(root.transform.rotation, Quaternion.identity), Is.LessThan(0.01f));
                Bounds bounds = root.GetComponentInChildren<Renderer>().bounds;
                Assert.That(bounds.size.y, Is.EqualTo(0.18f).Within(Tolerance), "The preview is 0.3 of the box height.");
                // box top 0.6 + gap 0.2 * 0.6 + half the preview 0.09
                AssertVector(bounds.center, new Vector3(1f, 0.81f, 2f));
                Assert.That(root.GetComponent<SbNativeEntity>().TryGetSharedScale(out Vector3 shared), Is.True,
                    "The source tells its peers the scale.");
                AssertVector(shared, Vector3.one * 0.6f);
                var collider = root.GetComponent<BoxCollider>();
                AssertVector(collider.center, new Vector3(0f, 0.85f, 0f)); // (0.81 - 0.3) / 0.6 in the root's frame
                AssertVector(collider.size, new Vector3(0.15f, 0.3f, 0.075f));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // The AFV mismatch race: the box rides each call. Two scans whose loads are in
        // flight together, fitted by ONE loader in the opposite order to their loads, each keep their own box.
        [UnityTest]
        public IEnumerator TwoLoadsInFlightWithDifferentBoxesEachKeepTheirOwnPlaceScaleAndSharedScale()
        {
            Task<GameObject> firstLoad = LoadAsSource("first");
            Task<GameObject> secondLoad = LoadAsSource("second");
            while (!firstLoad.IsCompleted || !secondLoad.IsCompleted) yield return null;
            GameObject first = firstLoad.Result, second = secondLoad.Result;
            try
            {
                var firstBox = new ScanRegionHint(new Vector3(1f, 0.3f, 2f), new Vector3(0.4f, 0.6f, 1f));
                var secondBox = new ScanRegionHint(new Vector3(-2f, 0.5f, 0.5f), new Vector3(0.2f, 1.0f, 0.2f));
                GltfLoader loader = _loaderHost.AddComponent<GltfLoader>();

                loader.SetupLoadedObject(second, true, ModelPath("second"), secondBox, useServerPose: false);
                loader.SetupLoadedObject(first, true, ModelPath("first"), firstBox, useServerPose: false);

                AssertVector(first.transform.position, new Vector3(1f, 0.3f, 2f));
                AssertVector(second.transform.position, new Vector3(-2f, 0.5f, 0.5f));
                AssertVector(first.transform.localScale, Vector3.one * 0.6f);
                AssertVector(second.transform.localScale, Vector3.one * 1.0f);
                Assert.That(first.GetComponent<SbNativeEntity>().TryGetSharedScale(out Vector3 firstShared), Is.True);
                Assert.That(second.GetComponent<SbNativeEntity>().TryGetSharedScale(out Vector3 secondShared), Is.True);
                AssertVector(firstShared, Vector3.one * 0.6f);
                AssertVector(secondShared, Vector3.one * 1.0f);
                // Each preview is 0.3 of its own box height, so a mix-up of the boxes would show here.
                Assert.That(first.GetComponentInChildren<Renderer>().bounds.size.y, Is.EqualTo(0.18f).Within(Tolerance));
                Assert.That(second.GetComponentInChildren<Renderer>().bounds.size.y, Is.EqualTo(0.3f).Within(Tolerance));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void ADegenerateHeightKeepsScaleOne()
        {
            // As v1 ApplyTransform: below 0.0001 the scale is 1.
            Assert.That(ScanObjectFit.BoxFitScale(0.6f, 0f), Is.EqualTo(1f));
            Assert.That(ScanObjectFit.BoxFitScale(0.6f, 0.00005f), Is.EqualTo(1f));
            Assert.That(ScanObjectFit.BoxFitScale(0.6f, 0.5f), Is.EqualTo(1.2f).Within(Tolerance));
        }

        // server pose from the info files

        [UnityTest]
        public IEnumerator WithoutABoxTheServerPoseFromTheInfoFilesPlacesTheObject()
        {
            // obj_2_pt3d: scale 2, translation (0.5, 0, 1). cam c2w: a quarter turn about Y, translation (1, 2, 3).
            // The handedness flip negates X of the translation: (-0.5, 0, 1). The quarter turn takes it to
            // (1, 0, 0.5), and the camera translation makes the object origin (2, 2, 3.5), scale 2.
            // v1 ApplyTransform composes that with the inverse normalisation. The Unity mesh centre is
            // (-0.5, 0.1, -0.3), and the quarter turn takes it to (-0.3, 0.1, 0.5), so the root is at
            // (2, 2, 3.5) + 2 * (-0.3, 0.1, 0.5) = (1.4, 2.2, 4.5), with scale 2 * 0.4 = 0.8 and the
            // camera's quarter turn.
            GameObject root = null;
            yield return Await(LoadAsSource("posed"), r => root = r);
            try
            {
                File.WriteAllText(Path.Combine(_directory, "posed_objinfo.json"),
                    "{\"filename\":\"reconstruction\",\"obj_2_pt3d_transform\":{\"scale\":[2,2,2],\"matrix\":" +
                    "[2,0,0,0.5, 0,2,0,0, 0,0,2,1, 0,0,0,1]}}");
                File.WriteAllText(Path.Combine(_directory, "posed_caminfo.json"),
                    "{\"c2w\":{\"matrix\":[0,0,1,1, 0,1,0,2, -1,0,0,3, 0,0,0,1]}}");
                GltfLoader loader = _loaderHost.AddComponent<GltfLoader>();

                loader.SetupLoadedObject(root, true, ModelPath("posed"), null, useServerPose: true);

                AssertVector(root.transform.position, new Vector3(1.4f, 2.2f, 4.5f));
                AssertVector(root.transform.localScale, Vector3.one * 0.8f);
                Assert.That(Quaternion.Angle(root.transform.rotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.01f));
                // The normalised mesh lands where the server's mesh was: its centre is the root's origin.
                Bounds bounds = root.GetComponentInChildren<Renderer>().bounds;
                AssertVector(bounds.center, new Vector3(1.4f, 2.2f, 4.5f));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void TheInfoFilesAreTheOnesTheSessionLibraryWrites()
        {
            // The library writes "{prefix}_objmodel.glb" and "{prefix}_objinfo.json" (spatialblend_session.h).
            // SessionManager copies the camera pose to "{prefix}_caminfo.json". The v2 loader read
            // "reconstruction_info*.json", which nothing writes.
            string model = Path.Combine(_directory, "sam3d_client_20261011_0000000000_objmodel.glb");
            Assert.That(ScanObjectFit.ObjectInfoPath(model),
                Is.EqualTo(Path.Combine(_directory, "sam3d_client_20261011_0000000000_objinfo.json")));
            Assert.That(ScanObjectFit.CameraInfoPath(model),
                Is.EqualTo(Path.Combine(_directory, "sam3d_client_20261011_0000000000_caminfo.json")));
            var result = new ScanReconstructionResult { OutputPrefix = Path.Combine(_directory, "p"), ObjectName = "p" };
            Assert.That(ScanObjectFit.ObjectInfoPath(result.ModelPath),
                Is.EqualTo(Path.Combine(_directory, "p" + ScanResponseParser.InfoSuffix)));
        }

        [Test]
        public void AMissingInfoFileIsNotAPose()
        {
            Assert.That(ScanObjectFit.TryReadObjectToWorld(Path.Combine(_directory, "none_caminfo.json"),
                Path.Combine(_directory, "none_objinfo.json"), out _), Is.False);
        }

        // shared scale

        [Test]
        public void TheSharedScaleCrossesInTheV1SyncPayload()
        {
            var go = new GameObject("shared-scale-source");
            IntPtr buffer = IntPtr.Zero;
            try
            {
                var entity = go.AddComponent<SbNativeEntity>();
                entity.SetSharedScale(new Vector3(0.6f, 0.6f, 0.6f));
                ulong length = entity.GetSyncSerializedLength();
                int nameBytes = Encoding.UTF8.GetByteCount(go.name);
                Assert.That(length, Is.EqualTo((ulong)(4 + nameBytes + 4 + 12 + SbNativeEntity.UprightExtensionLength)),
                    "[len][name][type][x][y][z], the v1 layout, then the upright extension");
                buffer = Marshal.AllocHGlobal((int)length);
                entity.PrepareSync(buffer);
                int offset = 4 + nameBytes + 4;
                Assert.That(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer, offset)), Is.EqualTo(0.6f));
                Assert.That(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer, offset + 4)), Is.EqualTo(0.6f));
                Assert.That(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer, offset + 8)), Is.EqualTo(0.6f));
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // The upright correction rides after the v1 fields, tagged; a v1 payload (shorter, or untagged) reads as none.
        [Test]
        public void TheUprightCorrectionCrossesAfterTheV1FieldsAndAV1PayloadHasNone()
        {
            var go = new GameObject("upright-source");
            IntPtr buffer = IntPtr.Zero;
            try
            {
                var entity = go.AddComponent<SbNativeEntity>();
                entity.SetSharedScale(Vector3.one * 0.6f);
                Quaternion upright = Quaternion.Euler(-90f, 0f, 0f);
                entity.SetUprightRotation(upright);
                ulong length = entity.GetSyncSerializedLength();
                buffer = Marshal.AllocHGlobal((int)length);
                entity.PrepareSync(buffer);
                int v1 = 4 + Encoding.UTF8.GetByteCount(go.name) + 4 + 12;
                Quaternion? read = SbNativeEntity.ReadUprightExtension(buffer, v1, length);
                Assert.That(read.HasValue, Is.True);
                Assert.That(Quaternion.Angle(read.Value, upright), Is.LessThan(0.01f));
                Assert.That(SbNativeEntity.ReadUprightExtension(buffer, v1, (ulong)v1), Is.Null, "a v1 payload ends at the scale");
                Assert.That(SbNativeEntity.ReadUprightExtension(buffer, v1, 0), Is.Null, "an unknown length reads the v1 layout only");
                Marshal.WriteInt32(buffer, v1, 0);
                Assert.That(SbNativeEntity.ReadUprightExtension(buffer, v1, length), Is.Null, "untagged bytes are not an extension");
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // A Z-up model the source stands upright (server pose) arrives upright on the peer, with the same footprint.
        [UnityTest]
        public IEnumerator AZUpModelTheSourceStandsUpArrivesUprightWithTheSameFootprint()
        {
            GameObject source = null;
            yield return Await(LoadAsSource("zup"), r => source = r);
            var peer = new GameObject("zup-peer");
            peer.AddComponent<BoxCollider>();
            var peerEntity = peer.AddComponent<SbNativeEntity>();
            try
            {
                var box = new ScanRegionHint(new Vector3(1f, 0.3f, 2f), new Vector3(0.4f, 0.6f, 1f));
                GltfLoader loader = _loaderHost.AddComponent<GltfLoader>();
                ScanObjectFit.LiftWorldNode(source);
                ScanObjectFit.Normalize(source);
                ScanObjectFit.FitCollider(source);
                MethodInfo applyBox = typeof(GltfLoader).GetMethod("ApplyBox", BindingFlags.Static | BindingFlags.NonPublic);
                Quaternion serverRotation = Quaternion.Euler(-90f, 35f, 0f);
                applyBox.Invoke(null, new object[] { source, source.GetComponent<SbNativeEntity>(), box, serverRotation });

                Assert.That(Quaternion.Angle(source.transform.rotation, Quaternion.Euler(0f, source.transform.eulerAngles.y, 0f)),
                    Is.LessThan(0.01f), "the source's root (its frame) is level");
                var sourceEntity = source.GetComponent<SbNativeEntity>();
                Assert.That(sourceEntity.TryGetUprightRotation(out Quaternion upright), Is.True);
                Assert.That(sourceEntity.TryGetSharedScale(out Vector3 scale), Is.True);

                // The peer: the same content, the told scale and correction, at the source's root pose.
                bool loaded = false;
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(peer, FixtureGlb()), ok => loaded = ok);
                Assert.That(loaded, Is.True);
                peerEntity.SetSharedScale(scale);
                peerEntity.SetUprightRotation(upright);
                peer.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                ScanObjectFit.FitReplica(peer);

                // The source's full-size model, measured before its preview shrank it: undo the preview on a copy of the numbers.
                Assert.That(ScanObjectFit.TryGeometryWorldBounds(peer, out Bounds peerBounds), Is.True);
                Assert.That(peerBounds.size.y, Is.EqualTo(0.6f).Within(Tolerance), "upright: as tall as the box");
                Transform sourceGeometry = ScanObjectFit.FindGeometry(source.transform);
                Transform peerGeometry = ScanObjectFit.FindGeometry(peer.transform);
                Quaternion sourceStand = Quaternion.Inverse(source.transform.rotation) * sourceGeometry.rotation;
                Quaternion peerStand = Quaternion.Inverse(peer.transform.rotation) * peerGeometry.rotation;
                // The source's geometry also turns in its preview (about world up); before any turn it equals the correction.
                Assert.That(Quaternion.Angle(sourceStand, peerStand), Is.LessThan(0.01f), "the copy stands as the source's does");
                // The footprint (the collider in the level root's frame) matches the source's at full size.
                var sourceCollider = source.GetComponent<BoxCollider>();
                var peerCollider = peer.GetComponent<BoxCollider>();
                Assert.That(peerCollider.size.x, Is.EqualTo(sourceCollider.size.x / ScanObjectFit.PreviewFactor).Within(1e-3f));
                Assert.That(peerCollider.size.z, Is.EqualTo(sourceCollider.size.z / ScanObjectFit.PreviewFactor).Within(1e-3f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(peer);
            }
        }

        // A scan placed by the server pose with no box is levelled; its collider then follows the
        // standing model (the region footprint, the ray and the shadow read it).
        [UnityTest]
        public IEnumerator AServerPosedScanWithNoBoxHasItsColliderOnTheStandingModel()
        {
            GameObject root = null;
            yield return Await(LoadAsSource("tilted-posed"), r => root = r);
            try
            {
                File.WriteAllText(Path.Combine(_directory, "tilted-posed_objinfo.json"),
                    "{\"filename\":\"reconstruction\",\"obj_2_pt3d_transform\":{\"scale\":[2,2,2],\"matrix\":" +
                    "[2,0,0,0.5, 0,2,0,0, 0,0,2,1, 0,0,0,1]}}");
                // c2w: a quarter turn about X (a tilt), translation (1, 2, 3).
                File.WriteAllText(Path.Combine(_directory, "tilted-posed_caminfo.json"),
                    "{\"c2w\":{\"matrix\":[1,0,0,1, 0,0,-1,2, 0,1,0,3, 0,0,0,1]}}");
                GltfLoader loader = _loaderHost.AddComponent<GltfLoader>();

                loader.SetupLoadedObject(root, true, ModelPath("tilted-posed"), null, useServerPose: true);

                Assert.That(Quaternion.Angle(root.transform.rotation, Quaternion.Euler(0f, root.transform.eulerAngles.y, 0f)),
                    Is.LessThan(0.01f), "the root is level");
                Assert.That(ScanObjectFit.TryGeometryWorldBounds(root, out Bounds model), Is.True);
                var collider = root.GetComponent<BoxCollider>();
                AssertVector(root.transform.TransformPoint(collider.center), model.center);
                Vector3 worldSize = root.transform.rotation * Vector3.Scale(collider.size, root.transform.lossyScale);
                AssertVector(new Vector3(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.y), Mathf.Abs(worldSize.z)), model.size);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator APeerTakesTheSharedScaleAndTheSameNormalisation()
        {
            var peer = new GameObject("peer-copy");
            var entity = peer.AddComponent<SbNativeEntity>();
            entity.SetSharedScale(Vector3.one * 0.6f);
            try
            {
                bool loaded = false;
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(peer, FixtureGlb()), ok => loaded = ok);
                Assert.That(loaded, Is.True);

                ScanObjectFit.FitReplica(peer);

                AssertVector(peer.transform.localScale, Vector3.one * 0.6f);
                Transform geometry = ScanObjectFit.FindGeometry(peer.transform);
                Assert.That(geometry, Is.Not.Null);
                AssertVector(geometry.localPosition, new Vector3(1.25f, -0.25f, 0.75f));
                AssertVector(geometry.localScale, Vector3.one * 2.5f);
                Assert.That(peer.GetComponentInChildren<Renderer>().bounds.size.y, Is.EqualTo(0.6f).Within(Tolerance),
                    "The peer's copy is as tall as the source's.");
            }
            finally { UnityEngine.Object.DestroyImmediate(peer); }
        }

        [UnityTest]
        public IEnumerator ContentWithoutASharedScaleIsLeftAlone()
        {
            var plain = new GameObject("plain-copy");
            plain.AddComponent<SbNativeEntity>();
            try
            {
                yield return Await(GltfRuntimeLoader.LoadModelFromBytes(plain, FixtureGlb()), _ => { });
                ScanObjectFit.FitReplica(plain);
                AssertVector(plain.transform.localScale, Vector3.one);
                Transform geometry = ScanObjectFit.FindGeometry(plain.transform);
                Assert.That(geometry, Is.Not.Null);
                AssertVector(geometry.localScale, Vector3.one);
            }
            finally { UnityEngine.Object.DestroyImmediate(plain); }
        }

        // helpers

        private string ModelPath(string stem) => Path.Combine(_directory, stem + ScanResponseParser.ModelSuffix);

        /// <summary>The source's object: a content root with a collider and an entity, the GLB under it as GltfLoader instantiates it.</summary>
        private Task<GameObject> LoadAsSource(string name)
        {
            return LoadAsSourceAsync(name);
        }

        private async Task<GameObject> LoadAsSourceAsync(string name)
        {
            var root = new GameObject(name);
            root.AddComponent<BoxCollider>();
            root.AddComponent<SbNativeEntity>();
            File.WriteAllBytes(ModelPath(name), FixtureGlb());
            var import = new GltfImport(null, new UninterruptedDeferAgent(), GltfOcclusionMaterial.CreateGenerator());
            Assert.That(await import.LoadGltfBinary(FixtureGlb()), Is.True);
            var instantiator = new GameObjectInstantiator(import, root.transform, null,
                new InstantiationSettings { SceneObjectCreation = SceneObjectCreation.Never });
            Assert.That(await import.InstantiateMainSceneAsync(instantiator), Is.True);
            return root;
        }

        private static IEnumerator Await<T>(Task<T> task, Action<T> done)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception.GetBaseException();
            done(task.Result);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"expected {expected}, was {actual}");
        }

        /// <summary>One triangle on a node called geometry_0 under a node called world, with the bounds in the class summary.</summary>
        private static byte[] FixtureGlb()
        {
            byte[] json = Encoding.UTF8.GetBytes(
                "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
                "\"nodes\":[{\"name\":\"world\",\"children\":[1]},{\"name\":\"geometry_0\",\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"," +
                "\"min\":[0.4,-0.1,-0.35],\"max\":[0.6,0.3,-0.25]}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36,\"target\":34962}]," +
                "\"buffers\":[{\"byteLength\":36}]}");
            int jsonPadded = (json.Length + 3) & ~3;
            float[] positions = { 0.4f, -0.1f, -0.35f, 0.6f, 0.3f, -0.25f, 0.6f, -0.1f, -0.35f };
            byte[] bin = new byte[36];
            Buffer.BlockCopy(positions, 0, bin, 0, 36);
            byte[] glb = new byte[12 + 8 + jsonPadded + 8 + bin.Length];
            WriteUInt32(glb, 0, 0x46546C67);
            WriteUInt32(glb, 4, 2);
            WriteUInt32(glb, 8, (uint)glb.Length);
            WriteUInt32(glb, 12, (uint)jsonPadded);
            WriteUInt32(glb, 16, 0x4E4F534A);
            Buffer.BlockCopy(json, 0, glb, 20, json.Length);
            for (int i = 20 + json.Length; i < 20 + jsonPadded; ++i) glb[i] = 0x20;
            int binHeader = 20 + jsonPadded;
            WriteUInt32(glb, binHeader, (uint)bin.Length);
            WriteUInt32(glb, binHeader + 4, 0x004E4942);
            Buffer.BlockCopy(bin, 0, glb, binHeader + 8, bin.Length);
            return glb;
        }

        private static void WriteUInt32(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }
    }
}
#endif
