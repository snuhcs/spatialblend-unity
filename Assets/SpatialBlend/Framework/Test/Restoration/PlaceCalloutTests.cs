// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A scan a share just dropped on the floor carries a card that says to hover it and place it. The card hides once
    /// the cursor is close, and it is gone once the object has been placed.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class PlaceCalloutTests
    {
        [Test]
        public void TheCardStaysUpFarAwayAndHidesWhenTheCursorIsClose()
        {
            var aimedPast = new Ray(new Vector3(0f, 1.5f, -3f), Vector3.forward);
            Assert.That(PlaceCallout.CursorDistance(aimedPast, Vector3.zero), Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(PlaceCallout.Shows(needsPlace: true, 1.5f), Is.True, "a metre and a half off the ray is still an invitation");

            var onTheObject = new Ray(new Vector3(0f, 0f, -2f), Vector3.forward);
            var bounds = new Bounds(Vector3.zero, Vector3.one * 0.2f);
            Assert.That(PlaceCallout.CursorDistance(onTheObject, bounds), Is.EqualTo(0f));
            Assert.That(PlaceCallout.Shows(needsPlace: true, 0f), Is.False, "the ray is on the object");

            var beside = new Ray(Vector3.zero, Vector3.forward);
            Assert.That(PlaceCallout.CursorDistance(beside, new Vector3(0.4f, 0f, 0f)), Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(PlaceCallout.Shows(needsPlace: true, 0.4f), Is.False, "the cursor origin is next to it");

            Assert.That(PlaceCallout.CursorDistance(beside, new Vector3(0f, 0f, -2f)), Is.EqualTo(2f).Within(1e-4f));
            Assert.That(PlaceCallout.Shows(needsPlace: true, 2f), Is.True, "behind the cursor, and far, it still invites");

            Assert.That(PlaceCallout.Shows(needsPlace: false, 5f), Is.False, "a placed object has nothing to announce");
        }

        [Test]
        public void TheCardHidesAsTheCursorArrivesAndLeavesOnceTheObjectIsPlaced()
        {
            var scan = new GameObject("scan");
            var eye = new GameObject("eye");
            try
            {
                var pending = scan.AddComponent<UnplacedSharedScan>();
                var view = scan.AddComponent<PlaceCalloutView>();
                eye.transform.position = new Vector3(0f, 1.6f, -2f);

                view.Apply(new Ray(new Vector3(0f, 1.6f, -3f), Vector3.forward), eye.transform);
                Assert.That(view.CalloutVisible, Is.True);

                view.Apply(new Ray(scan.transform.position + new Vector3(0f, 0.2f, -2f), Vector3.forward), eye.transform);
                Assert.That(view.CalloutVisible, Is.False, "aiming at the scan puts the card away");

                view.Apply(new Ray(new Vector3(0f, 1.6f, -3f), Vector3.forward), eye.transform);
                Assert.That(view.CalloutVisible, Is.True, "looking away brings the invitation back");

                pending.SpecifyPosition();
                view.Apply(new Ray(new Vector3(0f, 1.6f, -3f), Vector3.forward), eye.transform);
                Assert.That(view.CalloutVisible, Is.False);
            }
            finally
            {
                if (scan != null) Object.DestroyImmediate(scan);
                if (eye != null) Object.DestroyImmediate(eye);
            }
        }
    }
}
#endif
