// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The control panel follows the left controller (pinned by <see cref="ControlPanelPlacementTests"/>) and, when hands are the input,
    /// the left palm with the same clearance, scale and tilt. It switches without a jump and holds its last pose when tracking is lost.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ControlPanelHandAnchorTests
    {
        private GameObject _anchor, _panel, _canvas;
        private ControlPanelAnchor _follower;
        private bool _controllerInUse, _controllerTracked, _handInUse;
        private Pose? _hand;

        // A left hand held palm up in front of the user: wrist at the origin of the hand, fingers along +z, thumb to the left (-x) when the palm is up.
        private static readonly Vector3 Wrist = new Vector3(0.1f, 1.0f, 0.3f);
        private static readonly Vector3 Index1 = new Vector3(0.1f - 0.02f, 1.0f, 0.3f + 0.09f);
        private static readonly Vector3 Middle1 = new Vector3(0.1f, 1.0f, 0.3f + 0.095f);
        private static readonly Vector3 Pinky1 = new Vector3(0.1f + 0.03f, 1.0f, 0.3f + 0.075f);

        [SetUp]
        public void SetUp()
        {
            _controllerInUse = true;
            _controllerTracked = true;
            _handInUse = false;
            _anchor = new GameObject("LeftControllerAnchor");
            _anchor.transform.SetPositionAndRotation(new Vector3(-0.3f, 1.1f, 0.4f), Quaternion.Euler(10f, 30f, 0f));
            _panel = new GameObject("ControlPanel");
            _panel.transform.SetParent(_anchor.transform, false);
            _panel.transform.localPosition = UiTheme.HandPanelOffset;
            _panel.transform.localScale = Vector3.one * UiTheme.MetresPerUnit;
            _canvas = new GameObject("Canvas", typeof(Canvas));
            _canvas.transform.SetParent(_panel.transform, false);
            _follower = _panel.AddComponent<ControlPanelAnchor>();
            _follower.LeftControllerInUse = () => _controllerInUse;
            _follower.LeftControllerTracked = () => _controllerTracked;
            _follower.LeftHandInUse = () => _handInUse;
            _hand = null;
            _follower.HandPose = () => _hand;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_anchor);

        private static Pose PalmUp()
        {
            Assert.That(ControlPanelHandPlacement.TryCompute(Wrist, Index1, Middle1, Pinky1, true, out Pose pose), Is.True);
            return pose;
        }

        [Test]
        public void WithAControllerTheAnchorDrivesThePanelAsBefore()
        {
            _follower.Step(0.016f);
            Assert.That(_panel.transform.localPosition, Is.EqualTo(UiTheme.HandPanelOffset));
            Assert.That(_panel.transform.lossyScale.x, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-7f));
            Assert.That(_follower.CurrentSource, Is.EqualTo(ControlPanelAnchor.Source.Controller));
            Assert.That(_follower.IsHidden, Is.False);
        }

        [Test]
        public void ThePalmFrameHasItsNormalOutOfThePalmAndItsFingersAlongZ()
        {
            Pose pose = PalmUp();
            Assert.That(Vector3.Angle(pose.rotation * Vector3.up, Vector3.up), Is.LessThan(1f), "palm up: the panel stands on the palm's up side");
            Assert.That(Vector3.Angle(pose.rotation * Vector3.forward, Vector3.forward), Is.LessThan(5f), "z points toward the fingers");
        }

        [Test]
        public void TheRightHandMirrorsTheLeft()
        {
            // The mirror image across x of the left hand above is a right hand, palm up: the same normal.
            Vector3 M(Vector3 p) => new Vector3(-p.x, p.y, p.z);
            Assert.That(ControlPanelHandPlacement.TryCompute(M(Wrist), M(Index1), M(Middle1), M(Pinky1), false, out Pose right), Is.True);
            Assert.That(Vector3.Angle(right.rotation * Vector3.up, Vector3.up), Is.LessThan(1f));
        }

        [Test]
        public void ThePanelStandsTheControllerClearanceAboveThePalmSurfaceAndItsBottomEdgeIsOnTheKnuckleLine()
        {
            Pose pose = PalmUp();
            Vector3 centre = (Wrist + Index1 + Middle1 + Pinky1) * 0.25f;
            Vector3 up = pose.rotation * Vector3.up;
            Assert.That(Vector3.Dot(pose.position - centre, up), Is.EqualTo(ControlPanelHandPlacement.HandHalfThicknessMetres + ControlPanelHandPlacement.HandMarginMetres + UiTheme.HandPanelClearanceMetres + ControlPanelHandPlacement.ExtraAboveMetres).Within(1e-5f),
                "the hand's volume top (half thickness + margin), then the controller placement's clearance (0.05 m) plus its 7 mm: measured from the hand's surface, as the controller's is from its top");
            Assert.That(Vector3.Dot(pose.position - Middle1, pose.rotation * Vector3.forward), Is.EqualTo(0f).Within(1e-5f), "bottom-centre over the knuckles");
            Assert.That(UiTheme.HandPanelOffset.y, Is.EqualTo(0.0133f + UiTheme.HandPanelClearanceMetres + ControlPanelHandPlacement.ExtraAboveMetres).Within(0.0005f),
                "the 7 mm is the controller placement's, so the two placements share a basis");
        }

        [Test]
        public void WithTheHandAsTheInputThePanelSitsOverThePalmAtTheSameScaleAndTilt()
        {
            _controllerInUse = false;
            _handInUse = true;
            _hand = PalmUp();
            _follower.Step(0.016f);
            for (int i = 0; i < 20; i++) _follower.Step(0.02f); // past the blend
            Pose expected = PalmUp();
            Assert.That(Vector3.Distance(_panel.transform.position, expected.position), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(_panel.transform.rotation, expected.rotation), Is.LessThan(0.01f));
            Assert.That(_panel.transform.lossyScale.x, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-7f), "same angular-size basis as the controller placement");
            Assert.That(_follower.CurrentSource, Is.EqualTo(ControlPanelAnchor.Source.Hand));
        }

        [Test]
        public void SwitchingFromControllerToHandBlendsWithoutAJump()
        {
            _follower.Step(0.016f);
            Vector3 before = _panel.transform.position;
            _controllerInUse = false;
            _handInUse = true;
            _hand = PalmUp();
            _follower.Step(0f);
            Assert.That(Vector3.Distance(_panel.transform.position, before), Is.LessThan(1e-4f), "the first frame of the new input does not move the panel");
            Vector3 previous = before;
            float total = Vector3.Distance(before, PalmUp().position);
            float biggest = 0f;
            for (int i = 0; i < 16; i++)
            {
                _follower.Step(1f / 72f);
                biggest = Mathf.Max(biggest, Vector3.Distance(_panel.transform.position, previous));
                previous = _panel.transform.position;
            }
            Assert.That(biggest, Is.LessThan(total * 0.3f), "it eases over " + ControlPanelAnchor.BlendSeconds + " s, not in one step");
            Assert.That(Vector3.Distance(_panel.transform.position, PalmUp().position), Is.LessThan(1e-4f));
        }

        [Test]
        public void SwitchingBackToTheControllerReturnsThePanelToItsAnchorWithoutAJump()
        {
            _controllerInUse = false; _handInUse = true; _hand = PalmUp();
            for (int i = 0; i < 20; i++) _follower.Step(0.02f);
            Vector3 onHand = _panel.transform.position;
            _controllerInUse = true; _handInUse = false;
            _follower.Step(0f);
            Assert.That(Vector3.Distance(_panel.transform.position, onHand), Is.LessThan(1e-4f));
            for (int i = 0; i < 20; i++) _follower.Step(0.02f);
            Assert.That(_panel.transform.localPosition, Is.EqualTo(UiTheme.HandPanelOffset).Using(UnityEngine.TestTools.Utils.Vector3EqualityComparer.Instance));
            Assert.That(_panel.transform.parent, Is.SameAs(_anchor.transform));
        }

        [Test]
        public void WhenTrackingIsLostThePanelHoldsItsLastPoseAndNeverGoesToTheOrigin()
        {
            _controllerInUse = false; _handInUse = true; _hand = PalmUp();
            for (int i = 0; i < 20; i++) _follower.Step(0.02f);
            Vector3 held = _panel.transform.position;

            _hand = null; // tracking lost
            for (int i = 0; i < 30; i++) _follower.Step(0.02f);
            Assert.That(_panel.transform.position, Is.EqualTo(held).Using(UnityEngine.TestTools.Utils.Vector3EqualityComparer.Instance));
            Assert.That(_panel.transform.position.magnitude, Is.GreaterThan(0.5f), "not at the origin");
            Assert.That(_follower.IsHidden, Is.False);

            // The hand comes back elsewhere: the panel eases there, no jump.
            _hand = new Pose(PalmUp().position + new Vector3(0.2f, 0f, 0f), PalmUp().rotation);
            _follower.Step(0f);
            Assert.That(Vector3.Distance(_panel.transform.position, held), Is.LessThan(1e-4f), "tracking that returns starts from the held pose");
        }

        [Test]
        public void ALostControllerHoldsTheLastPoseToo()
        {
            _follower.Step(0.016f);
            Vector3 held = _panel.transform.position;
            _controllerTracked = false;
            _anchor.transform.position = Vector3.zero; // the rig's stale pose for an untracked controller
            _follower.Step(0.016f);
            Assert.That(Vector3.Distance(_panel.transform.position, held), Is.LessThan(1e-4f));
        }

        [Test]
        public void BeforeAnyValidPoseThePanelIsHiddenNotShownAtTheOrigin()
        {
            _controllerInUse = false; _handInUse = true; _hand = null;
            _follower.Step(0.016f);
            Assert.That(_follower.IsHidden, Is.True);
            Assert.That(_canvas.activeSelf, Is.False, "the panel's canvas, and the ray interactables under it, are off: nothing to hit");
            Assert.That(_panel.transform.localScale, Is.EqualTo(Vector3.one * UiTheme.MetresPerUnit), "not scaled to zero: the SDK's surfaces keep an inverse");

            _hand = PalmUp();
            _follower.Step(0.016f);
            Assert.That(_follower.IsHidden, Is.False);
            Assert.That(_canvas.activeSelf, Is.True);
            Assert.That(_panel.transform.lossyScale.x, Is.EqualTo(UiTheme.MetresPerUnit).Within(1e-7f));
        }

        [Test]
        public void DegenerateJointsGiveNoPose()
        {
            Assert.That(ControlPanelHandPlacement.TryCompute(Wrist, Wrist, Wrist, Wrist, true, out _), Is.False);
        }
    }
}
#endif
