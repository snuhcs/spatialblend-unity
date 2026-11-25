// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The annotation components draw with <see cref="AnnotationVisual"/> in every state they have: the box instance (normal,
    /// hovered, selected, a hovered corner), the motif's aim ray (a runtime-created motif has no LineRenderer to wire up),
    /// the motif's creation previews, the affordance region preview, and the region volume. Every colour they emit stays away
    /// from the real-anchor orange.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class AnnotationMotifStyleTests
    {
        private const float Eps = 1e-4f;
        private static readonly Vector3 Size = new Vector3(0.8f, 0.6f, 0.5f);

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private Camera _cam;

        [SetUp]
        public void SetUp()
        {
            var camGo = new GameObject("view", typeof(Camera));
            _spawned.Add(camGo);
            _cam = camGo.GetComponent<Camera>();
            _cam.transform.position = new Vector3(0.3f, 1.4f, -3f);
            _cam.transform.LookAt(new Vector3(0f, 0.3f, 0f));
            AnnotationVisual.ViewCameraOverride = _cam;
        }

        [TearDown]
        public void TearDown()
        {
            AnnotationVisual.ViewCameraOverride = null;
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private T Add<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go.AddComponent<T>();
        }

        private Transform NewRoot(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go.transform;
        }

        private static object Get(object target, string field)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var f = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f.GetValue(target);
            }
            throw new MissingFieldException(target.GetType().Name, field);
        }

        private static void Set(object target, string field, object value)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var f = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) { f.SetValue(target, value); return; }
            }
            throw new MissingFieldException(target.GetType().Name, field);
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var m = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, args.Select(a => a.GetType()).ToArray(), null);
                if (m == null) m = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
                if (m != null) return m.Invoke(target, args);
            }
            throw new MissingMethodException(target.GetType().Name, method);
        }

        private static float Hue(Color c) { Color.RGBToHSV(c, out float h, out float s, out _); return s < 0.2f ? -1f : h * 360f; }
        private static float HueGap(float a, float b) { float d = Mathf.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }

        private static void AssertNotRealAnchorColoured(AnnotationVisual visual, string state)
        {
            float anchor = Hue(UiTheme.Palette.RealAnchor);
            Assert.That(visual.Segments.Count, Is.GreaterThan(0), state);
            foreach (var seg in visual.Segments)
                foreach (var c in new[] { seg.ColorA, seg.ColorB })
                {
                    float h = Hue(c);
                    Assert.That(h < 0f || HueGap(h, anchor) >= 90f, Is.True, $"{state}: {c} is too close to the real-anchor orange");
                }
        }

        // ---- the box instance ----------------------------------------------------------------------------------------

        private BoxAnnotationInstance SizedInstance()
        {
            var instance = Add<BoxAnnotationInstance>("box");
            Set(instance, "_size", Size);
            instance.SetEmphasis(AnnotationEmphasis.Hovered);          // creates the visual
            instance.SetEmphasis(AnnotationEmphasis.Normal);
            Assert.That(instance.Visual, Is.Not.Null);
            return instance;
        }

        [Test]
        public void TheBoxInstanceDrawsItsTwelveEdgesInEveryState()
        {
            var instance = SizedInstance();
            var states = new (AnnotationEmphasis emphasis, int corner)[]
            {
                (AnnotationEmphasis.Normal, -1), (AnnotationEmphasis.Hovered, -1), (AnnotationEmphasis.Hovered, 5), (AnnotationEmphasis.Selected, -1),
            };
            foreach (var (emphasis, corner) in states)
            {
                instance.SetEmphasis(emphasis, corner);
                instance.Visual.Refresh();
                Assert.That(instance.Visual.Segments.Count, Is.EqualTo(12 + 8 * 3), $"{emphasis}/{corner}: 12 edges and 3 bracket arms at each corner");
                Assert.That(instance.Visual.BoxFaceVisible, Is.True);
                AssertNotRealAnchorColoured(instance.Visual, $"{emphasis}/{corner}");
            }
        }

        [Test]
        public void TheBoxInstanceHoverAndSelectionBrightenAndThickenTheLines()
        {
            var instance = SizedInstance();
            float Brightest(AnnotationEmphasis e, int corner = -1)
            {
                instance.SetEmphasis(e, corner);
                instance.Visual.Refresh();
                return instance.Visual.Segments.Where(s => s.ColorA.a > 0.9f).Max(s => s.ColorA.maxColorComponent + s.ColorA.g + s.ColorA.b);
            }
            float normal = Brightest(AnnotationEmphasis.Normal);
            float hovered = Brightest(AnnotationEmphasis.Hovered);
            float selected = Brightest(AnnotationEmphasis.Selected);
            Assert.That(hovered, Is.GreaterThan(normal));
            Assert.That(selected, Is.GreaterThan(hovered));

            instance.SetEmphasis(AnnotationEmphasis.Normal, 3);
            instance.Visual.Refresh();
            var look = instance.Visual.Look;
            Assert.That(instance.Visual.Segments.Max(s => s.WidthScale), Is.EqualTo(look.HoverBracketWidthScale).Within(Eps), "the hovered corner's bracket is the thickest line");
        }

        [Test]
        public void TheBoxInstanceUsesTheAnnotationShadersOnBothMaterials()
        {
            var instance = SizedInstance();
            instance.Visual.Refresh();
            Assert.That(instance.Visual.LineMaterial.shader.name, Is.EqualTo(AnnotationVisual.LineShaderName));
            Assert.That(instance.Visual.FaceMaterial.shader.name, Is.EqualTo(AnnotationVisual.FaceShaderName));
            Assert.That(instance.Visual.LineMaterial.GetColor("_CasingColor").a, Is.GreaterThan(0f), "dark casing");
            Assert.That(instance.Visual.LineMaterial.GetFloat("_GlowAlpha"), Is.GreaterThan(0f), "soft glow");
        }

        // ---- the motif ---------------------------------------------------------------------------------------------

        private BoxAnnotationMotif RuntimeMotif()
        {
            // A runtime-created motif: nothing is wired in the inspector, which is how the manager makes it.
            var motif = Add<BoxAnnotationMotif>("runtime-motif");
            return motif;
        }

        private static AnnotationVisual VisualOf(BoxAnnotationMotif motif) => (AnnotationVisual)Get(motif, "_visual");

        [Test]
        public void ARuntimeCreatedMotifDrawsTheAimRayAndMarkerWithNoLineIndicator()
        {
            var motif = RuntimeMotif();
            // the creation flow's first point: the ray starts at the controller (the raycast anchor, or the origin without one)
            Set(motif, "_p0", new Vector3(0.2f, 0f, 1f));
            Set(motif, "_currentPointIndex", 0);
            Set(motif, "_boxMode", Enum.Parse(motif.GetType().GetField("_boxMode", BindingFlags.Instance | BindingFlags.NonPublic).FieldType, "Create"));
            Invoke(motif, "RenderCurrentState");
            var visual = VisualOf(motif);
            Assert.That(visual, Is.Not.Null);
            visual.Refresh();
            Assert.That(visual.Segments.Count, Is.EqualTo(1 + 8), "a ray and a square of corner brackets: it is drawn even though no LineRenderer was ever assigned");
            Assert.That(visual.LineMaterial.shader.name, Is.EqualTo(AnnotationVisual.LineShaderName));
            AssertNotRealAnchorColoured(visual, "aim");
        }

        [Test]
        public void TheMotifPreviewsEveryCreationStepInTheSameStyle()
        {
            var motif = RuntimeMotif();
            var modeField = motif.GetType().GetField("_boxMode", BindingFlags.Instance | BindingFlags.NonPublic);
            Invoke(motif, "BeginCreation", new Vector3(-0.4f, 0f, -0.3f), Vector3.up);   // builds the floor plane the markers lie in
            Set(motif, "_boxMode", Enum.Parse(modeField.FieldType, "Create"));
            Set(motif, "_p0", new Vector3(-0.4f, 0f, -0.3f));
            Set(motif, "_p1", new Vector3(0.4f, 0f, -0.3f));
            Set(motif, "_p2", new Vector3(0.4f, 0f, 0.3f));
            var expected = new Dictionary<int, int> { { 1, 8 + 1 + 8 }, { 2, 4 + 4 * 2 } };   // two point markers and the first side; the rectangle with its brackets
            foreach (var step in new[] { 1, 2 })
            {
                Set(motif, "_currentPointIndex", step);
                Invoke(motif, "RenderCurrentState");
                var visual = VisualOf(motif);
                visual.Refresh();
                Assert.That(visual.Segments.Count, Is.EqualTo(expected[step]), $"creation step {step}");
                AssertNotRealAnchorColoured(visual, $"step {step}");
            }
            Assert.That(VisualOf(motif).FloorFaceVisible, Is.True, "the footprint is a faint face");
        }

        [Test]
        public void TheMotifHasNoLeftoverLineRenderersOrSphereMarkers()
        {
            var motif = RuntimeMotif();
            Set(motif, "_currentPointIndex", 1);
            Set(motif, "_boxMode", Enum.Parse(motif.GetType().GetField("_boxMode", BindingFlags.Instance | BindingFlags.NonPublic).FieldType, "Create"));
            Set(motif, "_p0", Vector3.zero);
            Set(motif, "_p1", Vector3.right);
            Invoke(motif, "RenderCurrentState");
            Assert.That(motif.GetComponentsInChildren<LineRenderer>(true), Is.Empty);
            Assert.That(motif.GetComponentsInChildren<MeshFilter>(true).All(f => f.sharedMesh == null || !f.sharedMesh.name.Contains("Sphere")), Is.True);
        }

        [Test]
        public void ADisabledMotifLeavesItsBoxesAtNormalEmphasis()
        {
            var motif = RuntimeMotif();
            var instance = SizedInstance();
            instance.SetEmphasis(AnnotationEmphasis.Hovered, 2);
            ((List<BoxAnnotationInstance>)Get(motif, "_instances")).Add(instance);
            Invoke(motif, "OnDisable");   // EditMode does not send it for a plain MonoBehaviour
            Assert.That(Get(instance, "_emphasis"), Is.EqualTo(AnnotationEmphasis.Normal), "a box does not stay lit after its motif is gone");
            Assert.That(Get(instance, "_focusCorner"), Is.EqualTo(-1));
        }

        [Test]
        public void TheLineMeshHasTightBoundsAndNoParentTransform()
        {
            var instance = SizedInstance();
            instance.Visual.Refresh();
            var lines = (MeshRenderer)Get(instance.Visual, "_lineRenderer");
            Assert.That(lines.transform.parent, Is.Null, "world-space mesh on an identity root-level object");
            var box = new Bounds(instance.transform.position + Vector3.up * Size.y * 0.5f, Size);
            Assert.That(lines.bounds.size.magnitude, Is.LessThan(box.size.magnitude + 0.5f), "not a cube around the viewer");
            Assert.That(lines.bounds.Contains(box.center), Is.True);
            Invoke(instance.Visual, "OnDestroy");   // EditMode does not send it for a plain MonoBehaviour
            Assert.That(lines == null, Is.True, "the root-level line object goes with its visual");
        }

        // ---- the affordance region preview -------------------------------------------------------------------------

        [Test]
        public void TheAffordancePreviewUsesTheSameLookForPointsSegmentAndPrism()
        {
            var motif = Add<AffordanceAnnotationMotif>("affordance");
            var update = typeof(AffordanceAnnotationMotif).GetMethod("UpdatePreview", BindingFlags.Instance | BindingFlags.NonPublic);
            var preview = new Func<AnnotationVisual>(() => (AnnotationVisual)Get(motif, "_preview"));

            update.Invoke(motif, new object[] { true, new Vector3(0f, 0f, 1f) });
            Assert.That(preview(), Is.Not.Null);
            preview().Refresh();
            Assert.That(preview().Segments.Count, Is.EqualTo(8), "a lone cursor point is a square of corner brackets");

            // a polygon preview: three points make the prism (outlines, vertical edges, a bracket arm triple on every vertex level)
            var points = (List<Vector3>)typeof(FloorVertexCollectionMotif).GetProperty("Points", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motif);
            points.AddRange(new[] { new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f) });
            update.Invoke(motif, new object[] { true, new Vector3(0.5f, 0f, 1.5f) });
            preview().Refresh();
            Assert.That(preview().Segments.Count, Is.EqualTo(3 * 3 + 3 * 6), "three vertices: 9 prism edges and 6 bracket arms each");
            Assert.That(preview().LineMaterial.shader.name, Is.EqualTo(AnnotationVisual.LineShaderName));
            AssertNotRealAnchorColoured(preview(), "affordance preview");

            Invoke(motif, "ClearPreview");
            preview().Refresh();
            Assert.That(preview().Segments.Count, Is.EqualTo(0));
        }

        // ---- the region volume -------------------------------------------------------------------------------------

        [Test]
        public void TheRegionPrismHasNoWarmColourAtAnyEmphasis()
        {
            var visual = AnnotationVisual.Create(NewRoot("region"));
            var square = new[] { new Vector3(-.5f, 0, -.5f), new Vector3(.5f, 0, -.5f), new Vector3(.5f, 0, .5f), new Vector3(-.5f, 0, .5f) };
            foreach (AnnotationEmphasis e in Enum.GetValues(typeof(AnnotationEmphasis)))
            {
                visual.Clear();
                visual.SetPrism(square, Vector3.up, 0.9f, e, 1, CornerFocus.Hover);
                visual.Refresh();
                AssertNotRealAnchorColoured(visual, "region " + e);
            }
        }
    }
}
#endif
