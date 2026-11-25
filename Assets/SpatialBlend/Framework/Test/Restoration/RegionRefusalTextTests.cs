// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A refused region outline reaches the user as one short sentence with an action; the technical
    /// reason goes to the log. Every reason the authoring layer can give for a refused outline is mapped.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class RegionRefusalTextTests
    {
        private static readonly string[] Technical =
        {
            "SbSpatialAuthoring requires a footprint polygon or a collider.",
            "A declared extent needs at least three points.",
            "The declared extent must be a simple polygon.",
            "Expanding the footprint by the fallback margin did not produce a simple polygon.",
            "The declared extent must contain the object footprint.",
            "A collider is required to derive the polygon.",
            "Collider 'Chair' has no local box. Declare a polygon instead.",
            "The collider footprint is degenerate.",
        };

        private static void AssertPlain(string message)
        {
            Assert.That(message, Is.Not.Empty);
            foreach (var banned in new[] { "polygon", "collider", "footprint", "SbSpatialAuthoring", "extent", "margin", "Region not created" })
                StringAssert.DoesNotContain(banned.ToLowerInvariant(), message.ToLowerInvariant(), message);
            Assert.That(message.Length, Is.LessThan(80), "one short sentence: " + message);
            Assert.That(message, Does.EndWith("."));
        }

        [Test]
        public void EveryTechnicalReasonHasItsOwnPlainSentenceWithAnAction()
        {
            foreach (var reason in Technical)
            {
                string message = RegionRefusalText.ToUserMessage("Region not created: " + reason);
                AssertPlain(message);
                Assert.That(message, Is.Not.EqualTo(RegionRefusalText.Fallback), reason + " must not fall back");
                Assert.That(message, Does.Contain("Draw it again").Or.Contain("Try another object"), "an action: " + message);
            }
            Assert.That(RegionRefusalText.ToUserMessage("Region not created: The declared extent must be a simple polygon."),
                Is.EqualTo("This outline crosses itself. Draw it again."));
        }

        [Test]
        public void ReasonsWithAndWithoutThePrefixMapTheSame()
        {
            foreach (var reason in Technical)
                Assert.That(RegionRefusalText.ToUserMessage(reason), Is.EqualTo(RegionRefusalText.ToUserMessage("Region not created: " + reason)));
        }

        [Test]
        public void UnknownAndEmptyReasonsGetTheFallback()
        {
            foreach (var unknown in new[] { null, "", "   ", "Region not created: something new happened", "kaboom" })
            {
                Assert.That(RegionRefusalText.ToUserMessage(unknown), Is.EqualTo(RegionRefusalText.Fallback), "'" + unknown + "'");
                AssertPlain(RegionRefusalText.ToUserMessage(unknown));
            }
        }

        // The reasons below are produced by the real authoring layer, not typed in, so a reworded reason cannot silently fall back.
        // (The fallback-margin and collider-required reasons are not
        // reachable through TryValidateDeclaredExtent (an outline of three or more points always takes the declared path, and a missing
        // collider is caught earlier as 'requires a footprint polygon or a collider'). Those are covered by the typed list above.)
        [Test]
        public void ReasonsFromTheRealAuthoringLayerAreMapped()
        {
            var withBox = new GameObject("object", typeof(BoxCollider));
            var bare = new GameObject("bare");
            var extra = new System.Collections.Generic.List<GameObject>();
            try
            {
                var authoring = withBox.AddComponent<SbSpatialAuthoring>();
                authoring.footprint = withBox.GetComponent<BoxCollider>();
                var square = new[] { new Vector2(-2, -2), new Vector2(2, -2), new Vector2(2, 2), new Vector2(-2, 2) };
                var bowtie = new[] { new Vector2(-2, -2), new Vector2(2, 2), new Vector2(2, -2), new Vector2(-2, 2) };
                var small = new[] { new Vector2(-0.1f, -0.1f), new Vector2(0.1f, -0.1f), new Vector2(0.1f, 0.1f), new Vector2(-0.1f, 0.1f) };
                var flat = new GameObject("flat", typeof(BoxCollider));
                flat.GetComponent<BoxCollider>().size = new Vector3(0f, 1f, 1f);
                var flatAuthoring = flat.AddComponent<SbSpatialAuthoring>();
                flatAuthoring.footprint = flat.GetComponent<BoxCollider>();
                extra.Add(flat);
                // A mesh collider with no mesh has no local box.
                var hollow = new GameObject("hollow", typeof(MeshCollider));
                var hollowAuthoring = hollow.AddComponent<SbSpatialAuthoring>();
                hollowAuthoring.footprint = hollow.GetComponent<MeshCollider>();
                extra.Add(hollow);
                var cases = new (SbSpatialAuthoring who, Vector2[] outline, string expectContains)[]
                {
                    (flatAuthoring, square, "too thin"),
                    (hollowAuthoring, square, "can't be outlined"),
                    (authoring, new[] { Vector2.zero, Vector2.one }, "Draw it again"),
                    (authoring, bowtie, "crosses itself"),
                    (authoring, small, "surround the whole object"),
                    (bare.AddComponent<SbSpatialAuthoring>(), square, "Try another object"),
                };
                foreach (var testCase in cases)
                {
                    Assert.That(testCase.who.TryValidateDeclaredExtent(testCase.outline, out string reason), Is.False);
                    string message = RegionRefusalText.ToUserMessage("Region not created: " + reason);
                    Assert.That(message, Does.Contain(testCase.expectContains), reason);
                    Assert.That(message, Is.Not.EqualTo(RegionRefusalText.Fallback), reason);
                    AssertPlain(message);
                }
            }
            finally
            {
                Object.DestroyImmediate(withBox);
                Object.DestroyImmediate(bare);
                foreach (var go in extra) Object.DestroyImmediate(go);
            }
        }
    }
}
#endif
