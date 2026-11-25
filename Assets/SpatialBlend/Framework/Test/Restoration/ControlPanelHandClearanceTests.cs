// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// In hand mode the control panel stands clear of the left hand (above it and beside it), every button is reachable by the right hand's ray
    /// from a typical head pose, and the left hand's own interactors cannot press or block the panel.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ControlPanelHandClearanceTests
    {
        // A left hand held palm up in front of the left shoulder, fingers pointing away from the user (+z), thumb toward the body's centre (-x here is
        // the thumb side; the same joint layout as ControlPanelHandAnchorTests, moved to where a hand is held to read a panel).
        private static readonly Vector3 Origin = new Vector3(-0.18f, 1.18f, 0.38f);
        private static readonly Vector3 Wrist = Origin;
        private static readonly Vector3 Index1 = Origin + new Vector3(-0.02f, 0f, 0.09f);
        private static readonly Vector3 Middle1 = Origin + new Vector3(0f, 0f, 0.095f);
        private static readonly Vector3 Pinky1 = Origin + new Vector3(0.03f, 0f, 0.075f);
        private static readonly Vector3 MiddleTip = Origin + new Vector3(0f, 0f, 0.095f + 0.085f);

        // A typical head pose: standing, eye 1.62 m, looking forward and a little down at the held hand; the right hand holds its ray origin
        // below and to the right of the head, as a user points at a panel in front of the chest.
        private static readonly Vector3 Head = new Vector3(0f, 1.62f, 0f);
        private static readonly Vector3 RightHand = new Vector3(0.22f, 1.22f, 0.22f);

        private GameObject _anchor, _panelObject;
        private ControlPanel _panel;
        private ControlPanelAnchor _follower;
        private SpatialBlendSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _anchor = new GameObject("LeftControllerAnchor");
            _anchor.transform.SetPositionAndRotation(new Vector3(-0.3f, 1.1f, 0.4f), Quaternion.identity);
            _panelObject = new GameObject("control-panel");
            _panelObject.SetActive(false);
            _panelObject.transform.SetParent(_anchor.transform, false);
            var canvas = new GameObject("panel-canvas", typeof(RectTransform), typeof(Canvas));
            canvas.transform.SetParent(_panelObject.transform, false);
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(406f, 300f);
            var legacy = new GameObject("Connect", typeof(RectTransform), typeof(Image), typeof(Button));
            legacy.transform.SetParent(canvas.transform, false);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(legacy.transform, false);
            _settings = ScriptableObject.CreateInstance<SpatialBlendSettings>();
            _panel = _panelObject.AddComponent<ControlPanel>();
            Set(_panel, "_connect", legacy.GetComponent<Button>());
            Set(_panel, "_settings", _settings);
            typeof(ControlPanel).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_panel, null);
            Assert.That(_panel.BuildGeneratedLayout(), Is.True);
            _follower = _panelObject.GetComponent<ControlPanelAnchor>();
            Assert.That(_follower, Is.Not.Null, "the generated panel carries its anchor follower");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_anchor);
            Object.DestroyImmediate(_settings);
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private ControlPanelHandPlacement.HandVolume PutPanelOverTheHand()
        {
            Assert.That(ControlPanelHandPlacement.TryCompute(Wrist, Index1, Middle1, Pinky1, true, MiddleTip, out Pose root, out var volume), Is.True);
            _follower.LeftControllerInUse = () => false;
            _follower.LeftControllerTracked = () => false;
            _follower.LeftHandInUse = () => true;
            _follower.HandPose = () => root;
            _follower.HandVolumeSource = () => volume;
            _follower.Step(0.016f);
            for (int i = 0; i < 20; i++) _follower.Step(0.02f);
            Canvas.ForceUpdateCanvases();
            Assert.That(_follower.CurrentSource, Is.EqualTo(ControlPanelAnchor.Source.Hand));
            return volume;
        }

        private List<(Selectable button, Vector3[] corners)> ActiveButtons()
        {
            var list = new List<(Selectable, Vector3[])>();
            foreach (var button in _panelObject.GetComponentsInChildren<Selectable>(true))
            {
                // the panel object itself stays inactive in this fixture (its OnEnable is driven by hand), so "shown" is every parent below it being on
                bool shown = true;
                for (Transform t = button.transform; t != null && t != _panelObject.transform; t = t.parent) shown &= t.gameObject.activeSelf;
                if (!shown) continue;
                var rect = (RectTransform)button.transform;
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                list.Add((button, corners));
            }
            return list;
        }

        [Test]
        public void TheHandVolume_IsTheHandsMeasuredSize_PlusTheStatedMargin()
        {
            var volume = PutPanelOverTheHand();
            Assert.That(volume.HalfExtents.y, Is.EqualTo(ControlPanelHandPlacement.HandHalfThicknessMetres + ControlPanelHandPlacement.HandMarginMetres));
            float width = (Pinky1 - Index1).magnitude;
            Assert.That(volume.HalfExtents.x, Is.EqualTo(width * 0.5f + ControlPanelHandPlacement.HandMarginMetres).Within(1e-5f));
            Assert.That(volume.Contains(Wrist), Is.True);
            Assert.That(volume.Contains(MiddleTip), Is.True, "the fingertips are inside");
            Assert.That(volume.Contains(Middle1 + Vector3.up * 0.05f), Is.False, "5 cm over a knuckle is outside");
        }

        [Test]
        public void NoButtonsHitRect_IntersectsTheLeftHandsVolume()
        {
            var volume = PutPanelOverTheHand();
            var buttons = ActiveButtons();
            Assert.That(buttons.Count, Is.GreaterThanOrEqualTo(4), "the lobby panel has its controls (buttons, toggles, sliders)");
            foreach (var (button, corners) in buttons)
                Assert.That(volume.IntersectsQuad(corners), Is.False, button.name + " is clear of the hand");
            // the lowest edge of the panel itself stands the stated distance over the joint plane
            var canvas = (RectTransform)_panel.View.Root.parent;
            var canvasCorners = new Vector3[4];
            canvas.GetWorldCorners(canvasCorners);
            float lowest = canvasCorners.Min(c => Vector3.Dot(c - Middle1, Vector3.up));
            Assert.That(lowest, Is.GreaterThanOrEqualTo(ControlPanelHandPlacement.BottomEdgeAboveJointPlaneMetres - 1e-4f));
        }

        [Test]
        public void TheLowestEdge_StandsTheControllersClearanceAboveTheHandsSurface_NotAboveItsJointPlane()
        {
            var volume = PutPanelOverTheHand();
            var canvas = (RectTransform)_panel.View.Root.parent;
            var corners = new Vector3[4];
            canvas.GetWorldCorners(corners);
            float lowestAboveSurface = corners.Min(c => Vector3.Dot(c - Middle1, Vector3.up)) - volume.HalfExtents.y;
            Assert.That(lowestAboveSurface, Is.GreaterThanOrEqualTo(UiTheme.HandPanelClearanceMetres + ControlPanelHandPlacement.ExtraAboveMetres - 1e-4f),
                "the same gap the panel leaves above the controller (5 cm + 7 mm), measured from the hand's surface");
            Assert.That(0.057f - volume.HalfExtents.y, Is.LessThan(UiTheme.HandPanelClearanceMetres),
                "5.7 cm over the joint plane would leave the hand's volume only 1.7 cm: less than the controller's");
        }

        [Test]
        public void EveryButton_IsReachableByTheRightHandsRay_FromATypicalHeadPose()
        {
            var volume = PutPanelOverTheHand();
            var canvas = (RectTransform)_panel.View.Root.parent;
            Vector3 normal = canvas.TransformDirection(Vector3.back);   // the readable side faces the user (a uGUI canvas reads from -z)
            foreach (var (button, corners) in ActiveButtons())
            {
                Vector3 centre = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
                Vector3 dir = (centre - RightHand).normalized;
                Assert.That(Vector3.Dot(-dir, normal), Is.GreaterThan(Mathf.Sin(20f * Mathf.Deg2Rad)), button.name + ": the ray meets the face, not an edge (>= 20 deg from the plane)");
                Assert.That(volume.IntersectsSegment(RightHand, centre), Is.False, button.name + ": the left hand is not in the way of the ray");
                float angle = Vector3.Angle(Vector3.forward, centre - Head);
                Assert.That(angle, Is.LessThan(75f), button.name + " is inside a comfortable look-down field (75 deg)");
            }
        }

        [Test]
        public void TheLeftHandsInteractors_CannotSelectAnythingOnThePanel_AndKeepTheirOtherInteractables()
        {
            var filter = _panelObject.AddComponent<ControlPanelInteractableFilter>();
            filter.Panel = _panelObject.transform;
            var leftHand = new GameObject("left-hand-ray");
            var rightHand = new GameObject("right-hand-ray");
            var other = new GameObject("not-the-panel");
            try
            {
                var leftRay = leftHand.AddComponent<RayInteractor>();
                var rightRay = rightHand.AddComponent<RayInteractor>();
                var onPanel = new GameObject("on-panel", typeof(RayInteractable));
                onPanel.transform.SetParent(_panelObject.transform, false);
                var elsewhere = other.AddComponent<RayInteractable>();
                int changed = ControlPanelInteractableFilter.Apply(filter, new Component[] { leftRay, rightRay }, c => c == leftRay);
                Assert.That(changed, Is.EqualTo(1), "only the left hand's interactor is filtered");
                Assert.That(leftRay.CanSelect(onPanel.GetComponent<RayInteractable>()), Is.False, "the left hand cannot press the panel");
                Assert.That(leftRay.CanSelect(elsewhere), Is.True, "and still reaches other things");
                Assert.That(rightRay.CanSelect(onPanel.GetComponent<RayInteractable>()), Is.True, "the right hand's ray is the pointer");
                Assert.That(ControlPanelInteractableFilter.Apply(filter, new Component[] { leftRay }, c => true), Is.EqualTo(0), "applied once");
                Object.DestroyImmediate(onPanel);
            }
            finally
            {
                Object.DestroyImmediate(leftHand);
                Object.DestroyImmediate(rightHand);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void TheControllerPlacement_IsUnchanged()
        {
            Assert.That(UiTheme.HandPanelOffset, Is.EqualTo(new Vector3(0f, 0.07f, 0.07f)));
            Assert.That(UiTheme.HandPanelClearanceMetres, Is.EqualTo(0.05f));
            Assert.That(UiTheme.HandPanelTiltDegrees, Is.EqualTo(20f));
            _follower.LeftControllerInUse = () => true;
            _follower.LeftControllerTracked = () => true;
            _follower.LeftHandInUse = () => false;
            _follower.Step(0.016f);
            Assert.That(_panelObject.transform.localPosition, Is.EqualTo(UiTheme.HandPanelOffset));
        }
    }
}
#endif
