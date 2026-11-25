// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Interactions.Annotation;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The annotation graphics (graphite lines with a dark casing, corner brackets, faint faces): the geometry is exactly the
    /// box's edges (the old wireframe drew three face diagonals), edges behind the box dim, a segment is clipped at the near
    /// plane, hover and selection change the colours and widths, no colour is amber or orange, the aim ray draws, and the
    /// shaders ship in a player build.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AnnotationVisualTests
    {
        private const float Eps = 1e-4f;
        private static readonly Vector3 Size = new Vector3(0.9f, 0.7f, 0.6f);

        private GameObject _camGo;
        private Camera _cam;
        private GameObject _root;
        private AnnotationVisual _visual;

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("view", typeof(Camera));
            _cam = _camGo.GetComponent<Camera>();
            _cam.transform.position = new Vector3(0.4f, 1.3f, -3f);
            _cam.transform.LookAt(new Vector3(0f, 0.35f, 0f));
            AnnotationVisual.ViewCameraOverride = _cam;
            _root = new GameObject("annotation");
            _visual = AnnotationVisual.Create(_root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            AnnotationVisual.ViewCameraOverride = null;
            UnityEngine.Object.DestroyImmediate(_root);
            UnityEngine.Object.DestroyImmediate(_camGo);
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        private static (Vector3, Vector3)[] BoxEdgeSegments(Vector3 centre, Vector3 size)
        {
            var corners = AnnotationGeometry.BoxCorners(centre, Quaternion.identity, size);
            var list = new List<(Vector3, Vector3)>();
            for (int e = 0; e < 12; e++) list.Add((corners[AnnotationGeometry.BoxEdges[e, 0]], corners[AnnotationGeometry.BoxEdges[e, 1]]));
            return list.ToArray();
        }

        private static float Hue(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            return s < 0.2f ? -1f : h * 360f;
        }

        private static float HueGap(float a, float b) { float d = Mathf.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }

        // ---- the edge set ---------------------------------------------------------------------------------------------

        [Test]
        public void ABoxIsExactlyItsTwelveEdgesWithNoDiagonals()
        {
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Normal);
            _visual.Refresh();

            var edges = BoxEdgeSegments(Vector3.zero, Size);
            var full = new HashSet<int>();
            foreach (var seg in _visual.Segments)
            {
                // every segment (edge or bracket arm) lies on one of the 12 box edges: no face or space diagonal
                bool onEdge = edges.Any(e => DistanceToSegment(seg.A, e.Item1, e.Item2) < Eps && DistanceToSegment(seg.B, e.Item1, e.Item2) < Eps);
                Assert.That(onEdge, Is.True, $"segment {seg.A} -> {seg.B} is not on a box edge");
                // a full-length segment is one of the edges
                for (int i = 0; i < 12; i++)
                {
                    if ((seg.A - edges[i].Item1).magnitude < Eps && (seg.B - edges[i].Item2).magnitude < Eps) full.Add(i);
                    if ((seg.B - edges[i].Item1).magnitude < Eps && (seg.A - edges[i].Item2).magnitude < Eps) full.Add(i);
                }
            }
            Assert.That(full.Count, Is.EqualTo(12), "all twelve edges are drawn, each as one full segment");
            Assert.That(_visual.Segments.Count, Is.EqualTo(12 + 8 * 3), "12 edges and a three-arm bracket at each of the 8 corners");
        }

        [Test]
        public void ARegionPrismIsItsOutlinesAndVerticalEdges()
        {
            var square = new[] { new Vector3(-.5f, 0, -.5f), new Vector3(.5f, 0, -.5f), new Vector3(.5f, 0, .5f), new Vector3(-.5f, 0, .5f) };
            _visual.SetPrism(square, Vector3.up, 0.8f, AnnotationEmphasis.Normal);
            _visual.Refresh();

            var edges = new List<(Vector3, Vector3)>();
            for (int i = 0; i < 4; i++)
            {
                edges.Add((square[i], square[(i + 1) % 4]));
                edges.Add((square[i] + Vector3.up * .8f, square[(i + 1) % 4] + Vector3.up * .8f));
                edges.Add((square[i], square[i] + Vector3.up * .8f));
            }
            foreach (var seg in _visual.Segments)
                Assert.That(edges.Any(e => DistanceToSegment(seg.A, e.Item1, e.Item2) < Eps && DistanceToSegment(seg.B, e.Item1, e.Item2) < Eps), Is.True, "no diagonal across the prism");
            Assert.That(_visual.Segments.Count, Is.EqualTo(12 + 4 * 6));
        }

        [Test]
        public void TheFloorRectangleIsFourEdgesAndABracketAtEachCorner()
        {
            _visual.SetFloorRect(new Vector3(-.4f, 0, -.3f), new Vector3(.4f, 0, -.3f), new Vector3(-.4f, 0, .3f), Vector3.up, 2, CornerFocus.Active);
            _visual.Refresh();
            Assert.That(_visual.Segments.Count, Is.EqualTo(4 + 4 * 2));
            Assert.That(_visual.FloorFaceVisible, Is.True, "a faint footprint");
            Assert.That(_visual.BoxFaceVisible, Is.False);
        }

        // ---- hidden edges ---------------------------------------------------------------------------------------------

        [Test]
        public void SeenFromAboveTheTopEdgesShowAndTheRestAreBehindTheBox()
        {
            var facing = new bool[6];
            AnnotationGeometry.FacesToward(new Vector3(0, .35f, 0), Quaternion.identity, Size, new Vector3(0, 5f, 0), facing);
            Assert.That(Enumerable.Range(0, 12).Count(e => AnnotationGeometry.EdgeHidden(e, facing)), Is.EqualTo(8));
            Assert.That(Enumerable.Range(4, 4).All(e => !AnnotationGeometry.EdgeHidden(e, facing)), Is.True, "the four top edges");
            Assert.That(Enumerable.Range(0, 4).All(c => AnnotationGeometry.CornerHidden(c, facing)), Is.True, "the base corners");
            Assert.That(Enumerable.Range(4, 4).All(c => !AnnotationGeometry.CornerHidden(c, facing)), Is.True);
        }

        [Test]
        public void SeenFromTheFrontTheFarEdgesAreBehindTheBox()
        {
            var facing = new bool[6];
            AnnotationGeometry.FacesToward(new Vector3(0, .35f, 0), Quaternion.identity, Size, new Vector3(0, .35f, -5f), facing);
            // only the -z face looks at the viewer: its four edges show, the other eight are silhouettes or behind
            Assert.That(facing.Count(f => f), Is.EqualTo(1));
            Assert.That(Enumerable.Range(0, 12).Count(e => !AnnotationGeometry.EdgeHidden(e, facing)), Is.EqualTo(4));
        }

        [Test]
        public void HiddenEdgesAreDrawnDimmerThanVisibleOnes()
        {
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Normal);
            _visual.Refresh();
            var edges = BoxEdgeSegments(Vector3.zero, Size);
            var alphas = new List<float>();
            foreach (var seg in _visual.Segments)
                for (int i = 0; i < 12; i++)
                    if ((seg.A - edges[i].Item1).magnitude < Eps && (seg.B - edges[i].Item2).magnitude < Eps) alphas.Add(seg.ColorA.a);
            Assert.That(alphas.Max(), Is.EqualTo(1f).Within(Eps));
            Assert.That(alphas.Min(), Is.EqualTo(AnnotationLook.Graphite().HiddenAlpha).Within(Eps), "an edge behind the box");
        }

        // ---- near-plane clipping -----------------------------------------------------------------------------------

        [Test]
        public void ASegmentIsClippedToTheNearPlane()
        {
            var cam = new GameObject("cam", typeof(Camera)).GetComponent<Camera>();
            try
            {
                cam.transform.position = Vector3.zero;
                cam.transform.rotation = Quaternion.identity;
                var w2c = cam.worldToCameraMatrix;

                var a = new Vector3(0, 0, -1f); var b = new Vector3(0, 0, 1f);
                Assert.That(AnnotationGeometry.ClipToNear(w2c, 0.06f, ref a, ref b, out float t0, out float t1), Is.True);
                Assert.That(a.z, Is.EqualTo(0.06f).Within(Eps));
                Assert.That(b.z, Is.EqualTo(1f).Within(Eps));
                Assert.That(t0, Is.EqualTo(0.53f).Within(Eps));
                Assert.That(t1, Is.EqualTo(1f).Within(Eps));

                var behind0 = new Vector3(0, 0, -2f); var behind1 = new Vector3(1, 0, -0.5f);
                Assert.That(AnnotationGeometry.ClipToNear(w2c, 0.06f, ref behind0, ref behind1, out _, out _), Is.False, "wholly behind the eye");

                var f0 = new Vector3(0, 0, 0.5f); var f1 = new Vector3(0, 0, 2f);
                Assert.That(AnnotationGeometry.ClipToNear(w2c, 0.06f, ref f0, ref f1, out _, out _), Is.True);
                Assert.That(f0.z, Is.EqualTo(0.5f).Within(Eps), "a segment in front is untouched");
            }
            finally { UnityEngine.Object.DestroyImmediate(cam.gameObject); }
        }

        [Test]
        public void AVisualBehindTheViewerDrawsNothingAndDoesNotThrow()
        {
            _cam.transform.position = new Vector3(0, 1f, 5f);
            _cam.transform.rotation = Quaternion.LookRotation(Vector3.forward);   // looking away from the box
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Normal);
            Assert.DoesNotThrow(() => _visual.Refresh());
            Assert.That(_visual.Segments.Count, Is.EqualTo(0));
        }

        // ---- states, colours, materials ----------------------------------------------------------------------------

        private float EdgeWidth(AnnotationEmphasis emphasis)
        {
            _visual.Clear();
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, emphasis);
            _visual.Refresh();
            var edges = BoxEdgeSegments(Vector3.zero, Size);
            // the widest full edge: the visible ones
            float best = 0f;
            foreach (var seg in _visual.Segments)
                for (int i = 0; i < 12; i++)
                    if ((seg.A - edges[i].Item1).magnitude < Eps && (seg.B - edges[i].Item2).magnitude < Eps) best = Mathf.Max(best, seg.WidthScale);
            return best;
        }

        [Test]
        public void HoverAndSelectionWidenTheEdgesAndRaiseTheFaces()
        {
            float normal = EdgeWidth(AnnotationEmphasis.Normal), normalFill = _visual.BoxFaceFillAlpha;
            float hovered = EdgeWidth(AnnotationEmphasis.Hovered), hoveredFill = _visual.BoxFaceFillAlpha;
            float selected = EdgeWidth(AnnotationEmphasis.Selected), selectedFill = _visual.BoxFaceFillAlpha;
            Assert.That(hovered, Is.GreaterThan(normal));
            Assert.That(selected, Is.GreaterThan(hovered));
            Assert.That(hoveredFill, Is.GreaterThan(normalFill));
            Assert.That(selectedFill, Is.GreaterThan(hoveredFill));
        }

        [Test]
        public void AFocusedCornerBracketIsBrighterAndThickerThanTheOthers()
        {
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Normal, 4, CornerFocus.Active);
            _visual.Refresh();
            var look = AnnotationLook.Graphite();
            var corners = AnnotationGeometry.BoxCorners(Vector3.zero, Quaternion.identity, Size);
            float maxWidth = 0f; Color brightest = Color.black;
            foreach (var seg in _visual.Segments)
            {
                bool atFocus = (seg.A - corners[4]).magnitude < Eps;
                if (!atFocus || (seg.B - seg.A).magnitude > look.BracketMax + Eps) continue;
                maxWidth = Mathf.Max(maxWidth, seg.WidthScale);
                if (seg.ColorA.maxColorComponent > brightest.maxColorComponent || brightest == Color.black) brightest = seg.ColorA;
            }
            Assert.That(maxWidth, Is.EqualTo(look.ActiveBracketWidthScale).Within(Eps));
            Assert.That(look.BracketColor(CornerFocus.Active, AnnotationEmphasis.Normal).r, Is.GreaterThan(look.Edge.r), "toward white");
            Assert.That(look.BracketScale(CornerFocus.Active), Is.GreaterThan(look.BracketScale(CornerFocus.Hover)));
            Assert.That(look.BracketScale(CornerFocus.Hover), Is.GreaterThan(look.BracketScale(CornerFocus.None)));
            Assert.That(look.BracketScale(CornerFocus.None), Is.GreaterThan(look.EdgeScale(AnnotationEmphasis.Normal)), "brackets are heavier than edges");
        }

        [Test]
        public void NoStateUsesAWarmColour()
        {
            var look = AnnotationLook.Graphite();
            var colours = new List<Color> { look.Edge, look.EdgeHover, look.FaceFill, look.Core };
            foreach (AnnotationEmphasis e in Enum.GetValues(typeof(AnnotationEmphasis)))
            {
                colours.Add(look.EdgeColor(e));
                foreach (CornerFocus f in Enum.GetValues(typeof(CornerFocus))) colours.Add(look.BracketColor(f, e));
            }
            foreach (var c in colours)
            {
                float h = Hue(c);
                Assert.That(h < 0f || HueGap(h, Hue(SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.RealAnchor)) >= 90f, Is.True, $"{c} is close to the real-anchor orange ({h}); warm colours belong to real-object anchors");
            }

            // and what a box in every state actually emits
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Selected, 6, CornerFocus.Active);
            _visual.Refresh();
            foreach (var seg in _visual.Segments)
            {
                float h = Hue(seg.ColorA);
                Assert.That(h < 0f || HueGap(h, Hue(SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.RealAnchor)) >= 90f, Is.True, $"segment colour {seg.ColorA}");
            }
        }

        [Test]
        public void TheMaterialsUseTheAnnotationShadersAndTheLooksNumbers()
        {
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Normal);
            _visual.Refresh();
            var look = AnnotationLook.Graphite();
            Assert.That(_visual.LineMaterial.shader.name, Is.EqualTo(AnnotationVisual.LineShaderName));
            Assert.That(_visual.FaceMaterial.shader.name, Is.EqualTo(AnnotationVisual.FaceShaderName));
            Assert.That(_visual.LineMaterial.GetFloat("_WidthPx"), Is.EqualTo(look.LinePx).Within(Eps));
            Assert.That(_visual.LineMaterial.GetFloat("_CasingPx"), Is.EqualTo(look.CasingPx).Within(Eps));
            Assert.That(_visual.LineMaterial.GetColor("_CasingColor").a, Is.GreaterThan(0f), "the dark hairline is on");
            Assert.That(_visual.FaceMaterial.GetFloat("_FillAlpha"), Is.EqualTo(look.FaceAlpha).Within(Eps));
            Assert.That(_visual.BoxFaceVisible, Is.True);
        }

        [Test]
        public void ARealAnchorLookDrawsInItsOwnColour()
        {
            var look = AnnotationLook.Graphite();
            look.Edge = look.EdgeHover = SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.RealAnchor;
            _visual.Look = look;
            _visual.SetBox(Vector3.zero, Quaternion.identity, Size, AnnotationEmphasis.Normal);
            _visual.Refresh();
            Assert.That(_visual.Segments.Any(s => Mathf.Abs(Hue(s.ColorA) - Hue(SpatialBlend.Samples.FreeformCollaboration.UI.UiTheme.Palette.RealAnchor)) < 8f), Is.True);
            Assert.That(_visual.LineMaterial, Is.Not.Null, "the materials were rebuilt for the new look");
        }

        // ---- the aim ray -------------------------------------------------------------------------------------------

        [Test]
        public void TheAimDrawsAFadingRayFromTheControllerAndAMarkerOnTheFloor()
        {
            var origin = new Vector3(0.3f, 1.1f, -1.2f);
            _visual.SetAim(origin, new Vector3(0, 0, 1f), Vector3.right, Vector3.forward);
            _visual.Refresh();
            var look = AnnotationLook.Graphite();
            var ray = _visual.Segments.First(s => (s.A - origin).magnitude < 0.2f || (s.B - origin).magnitude < 0.2f);
            Assert.That(Mathf.Min(ray.ColorA.a, ray.ColorB.a), Is.LessThan(0.2f), "fades out toward the controller");
            Assert.That(Mathf.Max(ray.ColorA.a, ray.ColorB.a), Is.EqualTo(look.AimRayAlpha).Within(0.05f));
            Assert.That(_visual.Segments.Count, Is.EqualTo(1 + 8), "a ray and a square of four corner brackets, two arms each");
        }

        // ---- shipping ----------------------------------------------------------------------------------------------

        [Test]
        public void TheShadersAreFoundAndSupported()
        {
            foreach (var name in new[] { AnnotationVisual.LineShaderName, AnnotationVisual.FaceShaderName })
            {
                var shader = Shader.Find(name);
                Assert.That(shader, Is.Not.Null, name);
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.That(shader.isSupported, Is.True, name);
            }
        }

        [Test]
        public void TheShadersAreInAlwaysIncludedShadersSoAPlayerBuildKeepsThem()
        {
            var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            Assert.That(graphics, Is.Not.Empty);
            var property = new SerializedObject(graphics[0]).FindProperty("m_AlwaysIncludedShaders");
            var included = new List<UnityEngine.Object>();
            for (int i = 0; i < property.arraySize; i++) included.Add(property.GetArrayElementAtIndex(i).objectReferenceValue);
            Assert.That(included, Does.Contain(Shader.Find(AnnotationVisual.LineShaderName)), "Shader.Find only works in a player if the shader is included");
            Assert.That(included, Does.Contain(Shader.Find(AnnotationVisual.FaceShaderName)));
        }

        [Test]
        public void TheOldBaselineLookIsGone()
        {
            string root = Path.Combine(Application.dataPath, "SpatialBlend/Interaction/Scripts/Annotation");
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = string.Concat(File.ReadAllLines(file).Select(l => l.Split(new[] { "//" }, StringSplitOptions.None)[0]));
                Assert.That(text, Does.Not.Contain("Sprites/Default"), file);
                Assert.That(text, Does.Not.Contain("UnlitTransparentColor"), file);
                Assert.That(text, Does.Not.Contain("PrimitiveType.Sphere"), file);
                Assert.That(text, Does.Not.Contain("LineRenderer"), file);
                Assert.That(text, Does.Not.Contain("Color.yellow"), file);
            }
        }
    }
}
#endif
