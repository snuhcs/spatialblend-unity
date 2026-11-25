// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The box of a real object shared with the other room: the scan box's look (edges, corner brackets, a faint fill)
    /// in its own colour, built once with the one shared outline material.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SharedAnchorBoxTests
    {
        private GameObject _parent;

        [SetUp]
        public void SetUp() => _parent = new GameObject("anchor");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_parent);

        private static float Hue(Color c) { Color.RGBToHSV(c, out float h, out _, out _); return h * 360f; }
        private static float Gap(Color a, Color b) { float d = Mathf.Abs(Hue(a) - Hue(b)) % 360f; return d > 180f ? 360f - d : d; }

        [Test]
        // Hue separation (the test's claim): at least 90 degrees from every other outline colour. Over passthrough it is also bright and
        // coloured enough to read (value and saturation below); it is not a luminance-contrast claim against the room.
        public void ItsHueIsSeparatedFromTheScanBoxTheHologramUnsharedFurnitureAndStatusColours_AndItIsBrightOverPassthrough()
        {
            var p = UiTheme.Palette;
            Color shared = p.SharedReal;
            var look = AnnotationLook.Graphite();
            foreach ((Color other, string what) in new[]
            {
                (look.Edge, "the scan box's edges"), (look.EdgeHover, "the scan box hovered"), (p.Accent, "the accent (virtual things)"),
                (PlacementPreview.ValidColor, "the placement hologram's cyan"), (PlacementPreview.PulledColor, "the hologram's amber"),
                (p.RealAnchor, "unshared furniture's orange"), (p.Error, "the error red"), (p.Warning, "the warning yellow"), (p.Success, "success"),
            })
                Assert.That(Gap(shared, other), Is.GreaterThanOrEqualTo(90f), "hue separation from " + what);
            Color.RGBToHSV(shared, out _, out float s, out float v);
            Assert.That(s, Is.GreaterThanOrEqualTo(0.4f), "coloured, not grey");
            Assert.That(v, Is.GreaterThanOrEqualTo(0.9f), "bright enough over a camera feed");
        }

        [Test]
        public void ItIsTheScanBoxsShape_EdgesBracketsAndAFaintFill_WithOneSharedMaterialAndOneSharedFill()
        {
            GameObject a = SharedAnchorBox.Build(_parent.transform, new Vector3(0f, 0.4f, 0f), new Vector3(1.2f, 0.8f, 0.2f), "a");
            GameObject b = SharedAnchorBox.Build(_parent.transform, Vector3.zero, new Vector3(0.5f, 0.5f, 0.5f), "b");
            LineRenderer[] lines = a.GetComponentsInChildren<LineRenderer>();
            Assert.That(lines.Length, Is.EqualTo(9), "one polyline for the twelve edges and eight corner brackets");
            Assert.That(a.GetComponentsInChildren<Collider>(), Is.Empty);
            Material shared = SceneAnchorHighlight.LineMaterial;
            Assert.That(a.GetComponentsInChildren<Renderer>().Concat(b.GetComponentsInChildren<Renderer>()).All(r => r.sharedMaterial == shared), Is.True,
                "every part draws with the one shared outline material");
            Mesh fillA = a.transform.Find("Fill").GetComponent<MeshFilter>().sharedMesh, fillB = b.transform.Find("Fill").GetComponent<MeshFilter>().sharedMesh;
            Assert.That(fillA, Is.SameAs(fillB), "one fill mesh for every box");
            Assert.That(fillA.colors.All(c => Mathf.Abs(c.a - SharedAnchorBox.FillAlpha) < 1e-4f), Is.True, "a faint fill");
            Assert.That(a.transform.Find("Fill").localScale, Is.EqualTo(new Vector3(1.2f, 0.8f, 0.2f)));

            // The scan box's brackets: a quarter of the edge, at most 9 cm, thicker than the edges.
            LineRenderer bracket = a.transform.Find("Bracket0").GetComponent<LineRenderer>();
            Assert.That(a.transform.Find("Fill").GetComponent<MeshFilter>().sharedMesh, Is.SameAs(SharedAnchorBox.FillMesh));
            Vector3 corner = bracket.GetPosition(1);
            Assert.That(Vector3.Distance(bracket.GetPosition(0), corner), Is.EqualTo(0.09f).Within(1e-4f), "1.2 m edge: capped at 9 cm");
            Assert.That(Vector3.Distance(bracket.GetPosition(4), corner), Is.EqualTo(0.05f).Within(1e-4f), "0.2 m edge: a quarter");
            Assert.That(bracket.widthMultiplier, Is.GreaterThan(a.transform.Find("Edges").GetComponent<LineRenderer>().widthMultiplier));
            Color c = bracket.startColor;
            Assert.That(new[] { c.r, c.g, c.b }, Is.EqualTo(new[] { UiTheme.Palette.SharedReal.r, UiTheme.Palette.SharedReal.g, UiTheme.Palette.SharedReal.b }).Within(1e-3f));
        }

        private static float Width(GameObject root, string child) => root.transform.Find(child).GetComponent<LineRenderer>().widthMultiplier;
        private static Color Start(GameObject root, string child) => root.transform.Find(child).GetComponent<LineRenderer>().startColor;
        private static float FillAlpha(GameObject root) => root.transform.Find("Fill").GetComponent<MeshFilter>().sharedMesh.colors[0].a;

        [Test]
        public void PendingAndHighlightedAreAnEmphasisOverTheOneBox_MeasurablyDifferentFromTheCalmPlacedBox()
        {
            // A shared anchor (or a joiner's box) draws its calm box; pending (still to place) and a highlighted row add an emphasis.
            GameObject calm = SharedAnchorBox.Build(_parent.transform, Vector3.zero, new Vector3(1f, 0.8f, 0.6f), SbMRUKAnchorEntity.SharedBoxName);
            GameObject emphasis = SceneAnchorHighlight.Build(_parent.transform, Vector3.zero, new Vector3(1f, 0.8f, 0.6f));
            Assert.That(emphasis.name, Is.EqualTo(SceneAnchorHighlight.ObjectName));
            Assert.That(emphasis.transform.Find("Edges"), Is.Null, "over the calm box: no second set of edges, not a duplicate box");
            Assert.That(_parent.GetComponentsInChildren<Transform>().Count(t => t.name == "Edges"), Is.EqualTo(1));
            Assert.That(Width(emphasis, "Bracket0"), Is.GreaterThanOrEqualTo(Width(calm, "Bracket0") * 1.5f), "thicker brackets");
            Assert.That(FillAlpha(emphasis), Is.GreaterThanOrEqualTo(FillAlpha(calm) * 2f), "a stronger fill");
            Color.RGBToHSV(Start(calm, "Bracket0"), out _, out float calmS, out _);
            Color.RGBToHSV(Start(emphasis, "Bracket0"), out _, out float emphasisS, out _);
            Assert.That(calmS - emphasisS, Is.GreaterThan(0.1f), "brighter (whiter) brackets");
            Assert.That(Start(emphasis, "Bracket0").a, Is.GreaterThan(Start(calm, "Bracket0").a));
            // Placed: the emphasis goes and the calm box stays (SharedRoomObjectBox.Place destroys its outline).
            Object.DestroyImmediate(emphasis);
            Assert.That(_parent.transform.Find(SbMRUKAnchorEntity.SharedBoxName), Is.Not.Null);
            Assert.That(SbMRUKAnchorEntity.SharedBoxName, Does.StartWith("SceneAnchorWire_"), "what the receiver policy and a re-adoption find");
        }

        [Test]
        public void AHighlightedRowOfAnUnsharedAnchor_DrawsTheEmphasisWithItsEdges()
        {
            GameObject highlight = SceneAnchorHighlight.Build(_parent.transform, Vector3.zero, Vector3.one);
            Assert.That(highlight.transform.Find("Edges"), Is.Not.Null, "no calm box under it: the emphasis carries the edges");
            Assert.That(highlight.transform.Find("Fill").GetComponent<MeshFilter>().sharedMesh, Is.SameAs(SharedAnchorBox.EmphasisFillMesh));
        }
    }
}
#endif
