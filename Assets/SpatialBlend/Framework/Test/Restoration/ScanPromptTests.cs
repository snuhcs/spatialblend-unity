// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using SpatialBlend.Freeform.Scanning;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The box projection (scan UI Concept A): the drawn 3D box projected into the photo through the capture snapshot, the labels
    /// that ride in SegPoint.z, the fallback for a server without box prompts, and the clicks-only prompt the capture card sends.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ScanPromptTests
    {
        // A 1280 x 960 sensor at fx = fy = 866 (Quest 3 passthrough-like), photo downscaled to 640 x 480.
        private static ScanCameraModel Camera(Vector3 position, Quaternion rotation) =>
            new ScanCameraModel(position, rotation, new Vector2(866f, 866f), new Vector2(640f, 480f), new Vector2Int(1280, 960), 640, 480);

        /// <summary>The camera at the origin looking down +z with Unity axes (x right, y up), as the app's camera pose is.</summary>
        private static Quaternion Facing(Quaternion look) => look;

        [Test]
        public void APointOnTheAxisLandsOnThePrincipalPoint()
        {
            var cam = Camera(Vector3.zero, Quaternion.identity);
            Assert.That(cam.TryProject(new Vector3(0f, 0f, 2f), out Vector2 p), Is.True);
            Assert.That(p.x, Is.EqualTo(320f).Within(1e-3));
            Assert.That(p.y, Is.EqualTo(240f).Within(1e-3));
        }

        [Test]
        public void ProjectionIsThePinholeModelScaledToThePhoto()
        {
            var cam = Camera(Vector3.zero, Quaternion.identity);
            // x = 0.5 m right and y = 0.25 m up at z = 2 m: u = 866 * 0.25 + 640 = 856.5 sensor px = 428.25 photo px;
            // v from the bottom = 866 * 0.125 + 480 = 588.25, from the top 960 - 588.25 = 371.75 sensor px = 185.875 photo px.
            Assert.That(cam.TryProject(new Vector3(0.5f, 0.25f, 2f), out Vector2 p), Is.True);
            Assert.That(p.x, Is.EqualTo((866f * 0.25f + 640f) * 0.5f).Within(1e-3));
            Assert.That(p.y, Is.EqualTo((960f - (866f * 0.125f + 480f)) * 0.5f).Within(1e-3));
        }

        [Test]
        public void ThePrincipalPointIsMeasuredFromTheTop()
        {
            // Android's intrinsics put the principal point in an upright image from its top-left corner.
            var cam = new ScanCameraModel(Vector3.zero, Quaternion.identity, new Vector2(866f, 866f), new Vector2(640f, 400f),
                new Vector2Int(1280, 960), 640, 480);
            Assert.That(cam.TryProject(new Vector3(0f, 0f, 2f), out Vector2 onAxis), Is.True);
            Assert.That(onAxis.y, Is.EqualTo(200f).Within(1e-3), "the optical axis lands on cy (400 sensor px from the top = 200 photo px)");
            Assert.That(cam.TryProject(new Vector3(0f, 0.2f, 2f), out Vector2 up), Is.True);
            Assert.That(up.y, Is.EqualTo((400f - 866f * 0.1f) * 0.5f).Within(1e-3));
        }

        [Test]
        public void ARealWorldPointAboveTheAxisIsNearTheTopOfThePhoto()
        {
            // World up is image up: v (from the top) shrinks; world right is image right.
            var cam = Camera(Vector3.zero, Facing(Quaternion.identity));
            Assert.That(cam.TryProject(new Vector3(0f, 0.3f, 2f), out Vector2 up), Is.True);
            Assert.That(up.y, Is.LessThan(240f));
            Assert.That(cam.TryProject(new Vector3(0.3f, 0f, 2f), out Vector2 right), Is.True);
            Assert.That(right.x, Is.GreaterThan(320f));
        }

        [Test]
        public void APointBehindTheCameraDoesNotProject()
        {
            var cam = Camera(Vector3.zero, Quaternion.identity);
            Assert.That(cam.TryProject(new Vector3(0f, 0f, -1f), out _), Is.False);
            Assert.That(new ScanCameraModel(Vector3.zero, Quaternion.identity, Vector2.zero, Vector2.zero, Vector2Int.zero, 0, 0)
                .TryProject(Vector3.forward, out _), Is.False);
        }

        [Test]
        public void ABoxInFrontBecomesAPixelBoxAroundItsCornersWithTheCentreAsFallback()
        {
            var cam = Camera(Vector3.zero, Facing(Quaternion.identity));
            var center = new Vector3(0f, 0f, 1.5f);
            var size = new Vector3(0.3f, 0.4f, 0.3f);
            Assert.That(ScanBoxProjection.TryBuildPrompt(cam, center, size, Quaternion.identity, out ScanPrompt prompt, out float coverage), Is.True);
            ScanPixelBox box = prompt.Box.Value;

            Vector2[] projected = ScanBoxProjection.Corners(center, size, Quaternion.identity)
                .Select(c => { cam.TryProject(c, out Vector2 p); return p; }).ToArray();
            Assert.That(box.X1, Is.EqualTo(projected.Min(p => p.x)).Within(1e-3));
            Assert.That(box.X2, Is.EqualTo(projected.Max(p => p.x)).Within(1e-3));
            Assert.That(box.Y1, Is.EqualTo(projected.Min(p => p.y)).Within(1e-3));
            Assert.That(box.Y2, Is.EqualTo(projected.Max(p => p.y)).Within(1e-3));
            Assert.That(box.CenterX, Is.EqualTo(320).Within(1e-3));
            Assert.That(coverage, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(prompt.BoxFallback.Count, Is.EqualTo(1));
            Assert.That(prompt.BoxFallback[0].X, Is.EqualTo(320).Within(1e-3));
            Assert.That(prompt.BoxFallback[0].Y, Is.EqualTo(240).Within(1e-3));
        }

        [Test]
        public void ABoxPartlyOutOfFrameIsClampedToThePhoto()
        {
            var cam = Camera(Vector3.zero, Facing(Quaternion.identity));
            Assert.That(ScanBoxProjection.TryBuildPrompt(cam, new Vector3(1.2f, 0f, 1.5f), new Vector3(0.6f, 0.4f, 0.3f),
                Quaternion.identity, out ScanPrompt prompt, out _), Is.True);
            Assert.That(prompt.Box.Value.X2, Is.EqualTo(639).Within(1e-6), "clamped to the last pixel, as taps are");
        }

        [Test]
        public void ABoxBehindOrOutOfViewIsNoPrompt()
        {
            var cam = Camera(Vector3.zero, Facing(Quaternion.identity));
            Assert.That(ScanBoxProjection.TryBuildPrompt(cam, new Vector3(0f, 0f, -1.5f), Vector3.one * 0.3f, Quaternion.identity,
                out _, out _), Is.False);
            Assert.That(ScanBoxProjection.TryBuildPrompt(cam, new Vector3(9f, 0f, 1.5f), Vector3.one * 0.3f, Quaternion.identity,
                out _, out _), Is.False, "entirely off to the side: clamped to nothing");
        }

        [Test]
        public void ARotatedBoxProjectsItsRotatedCorners()
        {
            var cam = Camera(Vector3.zero, Facing(Quaternion.identity));
            var center = new Vector3(0f, 0f, 1.5f);
            var size = new Vector3(0.6f, 0.2f, 0.05f);
            ScanBoxProjection.TryBuildPrompt(cam, center, size, Quaternion.identity, out ScanPrompt flat, out _);
            ScanBoxProjection.TryBuildPrompt(cam, center, size, Quaternion.Euler(0f, 90f, 0f), out ScanPrompt turned, out _);
            Assert.That(turned.Box.Value.Width, Is.LessThan(flat.Box.Value.Width * 0.5));
        }

        [Test]
        public void OnTheWireTheBoxIsTwoLabelledCornersOrItsFallbackClicks()
        {
            var prompt = ScanPrompt.ForBox(new ScanPixelBox(30, 40, 10, 20), new[] { new ScanPoint(20, 30) });
            prompt.Clicks.Add(new ScanPoint(25, 35, ScanPointLabel.Negative));

            ScanPoint[] withBox = prompt.ToWire(boxPrompts: true);
            Assert.That(withBox.Select(p => p.Label), Is.EqualTo(new[] { ScanPointLabel.Negative, ScanPointLabel.BoxTopLeft, ScanPointLabel.BoxBottomRight }));
            Assert.That(withBox[1].X, Is.EqualTo(10)); Assert.That(withBox[1].Y, Is.EqualTo(20));
            Assert.That(withBox[2].X, Is.EqualTo(30)); Assert.That(withBox[2].Y, Is.EqualTo(40));
            Assert.That(withBox.Select(p => p.Z), Is.EqualTo(new[] { -1.0, 2.0, 3.0 }));

            ScanPoint[] noBox = prompt.ToWire(boxPrompts: false);
            Assert.That(noBox.Select(p => p.Label), Is.EqualTo(new[] { ScanPointLabel.Negative, ScanPointLabel.Positive }));
            Assert.That(noBox.Any(p => p.IsBoxCorner), Is.False, "an older server must never see a corner");
        }

        // Segmentation is point-based. A prompt of the user's clicks carries exactly those clicks on the wire, in
        // order with their labels, whether or not the server takes boxes: no corners (z = 2/3) and no box-centre fallback click.
        [Test]
        public void AClicksOnlyPromptCarriesOnlyTheUsersPointsOnTheWire()
        {
            var clicks = new[] { new ScanPoint(12, 34), new ScanPoint(56, 78, ScanPointLabel.Negative), new ScanPoint(9, 10) };
            var prompt = ScanPrompt.ForClicks(clicks);
            Assert.That(prompt.HasBox, Is.False);
            Assert.That(prompt.BoxFallback, Is.Empty);
            foreach (bool boxPrompts in new[] { true, false })
            {
                ScanPoint[] wire = prompt.ToWire(boxPrompts);
                Assert.That(wire.Any(p => p.IsBoxCorner), Is.False, "no box corner, boxPrompts=" + boxPrompts);
                Assert.That(wire.Select(p => (p.X, p.Y, p.Label)), Is.EqualTo(clicks.Select(p => (p.X, p.Y, p.Label))),
                    "exactly the user's points, boxPrompts=" + boxPrompts);
                Assert.That(wire.Select(p => p.Z), Is.EqualTo(new[] { 0.0, -1.0, 0.0 }), "z: positive 0, negative -1");
            }
        }

        [Test]
        public void ALegacyClickIsZeroOnTheWire()
        {
            Assert.That(new ScanPoint(1, 2).Z, Is.EqualTo(0.0));
            Assert.That(new ScanPoint(1, 2).Label, Is.EqualTo(ScanPointLabel.Positive));
        }

        [TestCase("Segment OK | GPU:0 | points:0 | pos:0 | neg:0 | box:1 | time:1.4s", true)]
        [TestCase("Segment OK (sample) | box:1", true)]
        [TestCase("Segment OK | GPU:0 | points:2 | time:1.5s", false)]
        [TestCase("Segment OK | box:0", false)]
        [TestCase("box:10", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void TheServerSaysWhetherItUsedTheBox(string message, bool used)
        {
            Assert.That(ScanResponseParser.ServerUsedBox(message), Is.EqualTo(used));
        }
    }
}
#endif
