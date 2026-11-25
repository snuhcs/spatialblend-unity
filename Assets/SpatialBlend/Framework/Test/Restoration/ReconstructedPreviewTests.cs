// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using SpatialBlend.Objects;
using SpatialBlend.Samples;
using SpatialBlend.Samples.Objects;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// As v1, the source sees a scan at 0.3 of its
    /// box-fit size, centred above the box with its underside 0.2 box heights over the box top, turning at 30 degrees per second
    /// about its own centre, with a flat ring on the box bottom and no outline glow.
    /// The root keeps the full box-fit scale, which is what the peers are told and what the declared region uses.
    /// The box here is 0.5 x 2 x 0.5 m centred at (1, 1, 2); the mesh is a unit cube, so its box-fit scale is 2.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ReconstructedPreviewTests
    {
        private static readonly ScanRegionHint Box = new ScanRegionHint(new Vector3(1f, 1f, 2f), new Vector3(0.5f, 2f, 0.5f));

        private GameObject _root;
        private Transform _geometry;
        private MeshRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("scan-root");
            _root.AddComponent<BoxCollider>();
            var geometry = new GameObject(ScanObjectFit.GeometryNodeName);
            geometry.transform.SetParent(_root.transform, false);
            var filter = geometry.AddComponent<MeshFilter>();
            filter.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _renderer = geometry.AddComponent<MeshRenderer>();
            _geometry = geometry.transform;
            ScanObjectFit.Normalize(_root);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void TheGeometryShrinksToThreeTenthsAndFloatsAboveTheBoxWhileTheRootKeepsTheFullScale()
        {
            float fullScale = ScanObjectFit.PlaceInBox(_root, Box, Quaternion.identity);
            Assert.That(fullScale, Is.EqualTo(2f).Within(1e-5f));

            ScanObjectFit.ApplyPreview(_root, Box);

            Assert.That(_root.transform.localScale.x, Is.EqualTo(2f).Within(1e-5f), "the root is at the box-fit scale");
            Assert.That(_geometry.localScale.x, Is.EqualTo(0.3f).Within(1e-5f));
            Bounds shown = _renderer.bounds;
            Assert.That(shown.size.y, Is.EqualTo(0.6f).Within(1e-4f), "0.3 of the 2 m box");
            // box top 2.0 + gap 0.2 * 2.0 + half the preview 0.3
            Assert.That(shown.center.y, Is.EqualTo(2.7f).Within(1e-4f));
            Assert.That(shown.min.y, Is.EqualTo(2.4f).Within(1e-4f), "its underside clears the box top by the gap");
            Assert.That(shown.center.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(shown.center.z, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void TheColliderFollowsTheLiftedPreview()
        {
            ScanObjectFit.PlaceInBox(_root, Box, Quaternion.identity);

            ScanObjectFit.ApplyPreview(_root, Box);

            var collider = _root.GetComponent<BoxCollider>();
            Assert.That(collider.size.y, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(collider.center.y, Is.EqualTo(0.85f).Within(1e-5f), "(2.7 - 1) / 2 in the root's frame");
        }

        [Test]
        public void ARootWithoutAGeometryNodeIsLeftAlone()
        {
            var bare = new GameObject("bare");
            try
            {
                bare.transform.localScale = Vector3.one * 2f;
                ScanObjectFit.ApplyPreview(bare, Box);
                Assert.That(bare.transform.localScale, Is.EqualTo(Vector3.one * 2f));
                Assert.That(bare.transform.childCount, Is.Zero);
            }
            finally { Object.DestroyImmediate(bare); }
        }

        [Test]
        public void TheLoaderPreviewsTheBoxFitAndTurnsTheGeometry()
        {
            MethodInfo applyBox = typeof(GltfLoader).GetMethod("ApplyBox", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(applyBox, Is.Not.Null);

            applyBox.Invoke(null, new object[] { _root, null, Box, Quaternion.identity });

            Assert.That(_root.transform.position, Is.EqualTo(Box.BoxCenter));
            Assert.That(_geometry.localScale.x, Is.EqualTo(0.3f).Within(1e-5f));
            var rotator = _geometry.GetComponent<SlowRotate>();
            Assert.That(rotator, Is.Not.Null, "the geometry turns, not the root, so the declared region stays put");
            Assert.That(rotator.DegreesPerSecond, Is.EqualTo(30f));
            Assert.That(_root.GetComponent<SlowRotate>(), Is.Null);
        }

        // No outline glow (of either copy) on the preview; a ring marks the box bottom.
        [Test]
        public void ThePreviewHasNoGlowAndItsBoxBottomCarriesOneFlatRing()
        {
            ApplyBox(_root, Box, Quaternion.identity);

            foreach (Component c in _root.GetComponentsInChildren<Component>(true))
                Assert.That(c.GetType().Name, Does.Not.Contain("Glow"), "no glow component on the preview");
            foreach (Renderer r in _root.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    Assert.That(m == null || m.shader == null || m.shader.name != "Sb/OutlineGlow", Is.True, "no outline material");

            Transform ring = _root.transform.Find(GltfLoader.FootprintRingName);
            Assert.That(ring, Is.Not.Null, "the ring under the box");
            var line = ring.GetComponent<LineRenderer>();
            Assert.That(line, Is.Not.Null);
            Assert.That(line.loop, Is.True);
            Assert.That(line.alignment, Is.EqualTo(LineAlignment.TransformZ), "flat on the floor, not turned to the viewer");
            Assert.That(line.sharedMaterial, Is.SameAs(SpatialBlend.Samples.FreeformCollaboration.UI.SceneAnchorHighlight.LineMaterial),
                "the one shared outline material");
            Color accent = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.Accent;
            Assert.That((Vector3)(Vector4)line.startColor, Is.EqualTo((Vector3)(Vector4)accent).Using(Vector3EqualityComparer.Instance));
            Assert.That(line.positionCount, Is.EqualTo(GltfLoader.FootprintRingSegments));
            float radius = 0.5f * Mathf.Sqrt(0.5f * 0.5f + 0.5f * 0.5f);
            for (int i = 0; i < line.positionCount; i++)
            {
                Vector3 p = ring.TransformPoint(line.GetPosition(i));
                Assert.That(p.y, Is.EqualTo(Box.YBottom + GltfLoader.FootprintRingLift).Within(1e-4f), "on the box bottom");
                Assert.That(new Vector2(p.x - Box.BoxCenter.x, p.z - Box.BoxCenter.z).magnitude, Is.EqualTo(radius).Within(1e-4f),
                    "around the box footprint, at 1:1 whatever the root's scale");
            }
            Assert.That(ring.GetComponents<MonoBehaviour>(), Is.Empty, "nothing runs per frame");

            // Built once: building again finds the same ring.
            Assert.That(GltfLoader.BuildFootprintRing(_root.transform, Box), Is.SameAs(ring.gameObject));
            int rings = 0;
            foreach (Transform child in _root.transform) if (child.name == GltfLoader.FootprintRingName) rings++;
            Assert.That(rings, Is.EqualTo(1));
        }

        // The preview hovers above the box, not beside it, with a server pose. Two ways it can go wrong: (1) a lift
        // along the root's own up axis, which a server rotation that stands a Z-up reconstruction upright turns sideways (and a box
        // fit that measures the unturned height); (2) a preview turning about geometry_0's origin, which the normalisation leaves away
        // from the mesh centre, so the model swings around a point beside the box.
        [Test]
        public void APreviewOfAZUpModelOffItsOriginHoversCentredOverTheBoxAndStaysThereWhileItTurns()
        {
            Object.DestroyImmediate(_root);
            _root = new GameObject("scan-root");
            _root.AddComponent<BoxCollider>();
            var geometry = new GameObject(ScanObjectFit.GeometryNodeName);
            geometry.transform.SetParent(_root.transform, false);
            // 0.2 x 0.2 wide, 0.6 tall along Z (Z-up), centred far from its origin, at (3, -1, 2).
            geometry.AddComponent<MeshFilter>().sharedMesh = OffsetBox(new Vector3(0.2f, 0.2f, 0.6f), new Vector3(3f, -1f, 2f));
            geometry.AddComponent<MeshRenderer>();
            _geometry = geometry.transform;
            ScanObjectFit.Normalize(_root);
            Quaternion upright = Quaternion.Euler(-90f, 0f, 0f);   // the server pose: Z up becomes Y up
            Assert.That(upright * Vector3.forward, Is.EqualTo(Vector3.up).Using(Vector3EqualityComparer.Instance));

            ApplyBox(_root, Box, upright);

            Assert.That(_root.transform.localScale.x, Is.EqualTo(2f).Within(1e-4f), "the box height fits the model's upright height");
            Assert.That(ScanObjectFit.TryGeometryWorldBounds(_root, out Bounds shown), Is.True);
            Assert.That(shown.size.y, Is.EqualTo(0.6f).Within(1e-4f), "0.3 of the 2 m box");
            Assert.That(shown.center.x, Is.EqualTo(1f).Within(1e-4f), "over the box centre");
            Assert.That(shown.center.z, Is.EqualTo(2f).Within(1e-4f), "over the box centre");
            Assert.That(shown.min.y, Is.EqualTo(2.4f).Within(1e-4f), "its underside a gap of 0.2 box heights over the box top");

            var collider = _root.GetComponent<BoxCollider>();
            Vector3 hitCentre = _root.transform.TransformPoint(collider.center);
            Vector3 hitSize = _root.transform.rotation * Vector3.Scale(collider.size, _root.transform.lossyScale);
            Assert.That(Vector3.Distance(hitCentre, shown.center), Is.LessThan(1e-3f), "the collider is where the preview is");
            Assert.That(Mathf.Abs(hitSize.y), Is.EqualTo(0.6f).Within(1e-3f), "as tall as the preview");

            var rotator = _geometry.GetComponent<SlowRotate>();
            for (int i = 0; i < 8; i++)
            {
                rotator.Turn(45f);
                ScanObjectFit.TryGeometryWorldBounds(_root, out Bounds turned);
                Assert.That(new Vector2(turned.center.x - 1f, turned.center.z - 2f).magnitude, Is.LessThan(1e-3f),
                    $"after {(i + 1) * 45} degrees it still turns over the box centre");
                Assert.That(turned.center.y, Is.EqualTo(shown.center.y).Within(1e-3f));
            }
        }

        private static void ApplyBox(GameObject root, ScanRegionHint box, Quaternion rotation)
        {
            MethodInfo applyBox = typeof(GltfLoader).GetMethod("ApplyBox", BindingFlags.Static | BindingFlags.NonPublic);
            applyBox.Invoke(null, new object[] { root, null, box, rotation });
        }

        private static Mesh OffsetBox(Vector3 size, Vector3 centre)
        {
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            Vector3[] v = cube.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = Vector3.Scale(v[i], size) + centre;
            var mesh = new Mesh { vertices = v, triangles = cube.triangles };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
#endif
