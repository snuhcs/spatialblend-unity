// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using UnityEngine;
using UnityEngine.TestTools;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// In Editing a hands-only user can do what a controller user does. The right hand's ray (pinch to select) and the right controller's ray
    /// are two <see cref="EditPointer"/>s behind <see cref="EditInput"/>; the motifs listen to that and never to a device. Everything here is driven
    /// with simulated pointers (a ray, a pinch, a wrist pose) through the same entry points the Interaction SDK's interactors use, so no headset is needed.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class HandEditingTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _entity, _eye, _other;
        private ObjectPlacementMotif _motif;
        private RayInteractable _interactable;
        private bool _ignoreFailing;
        private readonly List<GameObject> _parked = new List<GameObject>();

        // The simulated devices.
        private EditPointer _controller, _hand;
        private Vector3 _handOrigin, _handForward, _controllerOrigin, _controllerForward;
        private Quaternion _wrist;
        private Pose _head;
        private Vector3 _fingerAxis;
        private bool _pinch, _trigger, _controllerPresent, _handPresent, _bDown;
        private Vector2 _stick;
        private RayInteractable _handHover, _handSelected;
        private int _frame;
        private float _time;

        private static readonly Vector3 Start = new Vector3(0f, 1f, 2f);
        // The head at (0, 1.6, 0) facing +z puts the right shoulder at (0.17, 1.4, 0); the arm is 0.45 m long.
        private static readonly Vector3 Shoulder = new Vector3(0.17f, 1.4f, 0f);
        private const float Arm = 0.45f;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // no controller, camera rig or headset in an EditMode run
            EditInput.ResetForTests();
            ObjectMoveHistory.Clear();
            ParkLeftovers();
            InteractionMotif.FocusOverride = () => true;

            _frame = 100;
            _time = 10f;
            _controllerOrigin = new Vector3(0f, 1.4f, 0f);
            _handOrigin = Shoulder + Vector3.forward * Arm;
            _head = new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
            _fingerAxis = Vector3.forward;
            _handForward = _controllerForward = Vector3.forward;
            _wrist = Quaternion.identity;
            _pinch = _trigger = _bDown = false;
            _controllerPresent = true;
            _handPresent = false;
            _stick = Vector2.zero;
            _handHover = _handSelected = null;

            EditInput.Frame = () => _frame;
            EditInput.Clock = () => _time;
            EditInput.ControllerPresent = () => _controllerPresent;
            EditInput.HandPresent = () => _handPresent;
            EditInput.ControllerAcceptSource = () => _trigger;
            EditInput.ControllerUndoSource = () => _bDown;
            EditInput.ControllerStickSource = () => _stick;

            _controller = new EditPointer(EditInputSource.Controller)
            {
                OriginSource = () => _controllerOrigin,
                ForwardSource = () => _controllerForward,
            };
            _hand = new EditPointer(EditInputSource.Hand)
            {
                OriginSource = () => _handOrigin,
                ForwardSource = () => _handForward,
                PinchSource = () => _pinch,
                WristSource = () => new Pose(Vector3.zero, _wrist),
                HoveredSource = () => _handHover,
                SelectedSource = () => _handSelected,
                HeadSource = () => _head,
                FingerAxisSource = () => _fingerAxis,
            };
            EditInput.Register(_controller);
            EditInput.Register(_hand);

            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            // A mode manager another fixture left alive would make Awake keep that one; this test's must be the one.
            typeof(InteractionModeManager).GetProperty("Instance").GetSetMethod(true).Invoke(null, new object[] { null });
            Invoke(_mode, "Awake");
            Assert.That(InteractionModeManager.Instance, Is.SameAs(_mode));
            _mode.SetMode(InteractionMode.SceneEdit);

            _eye = new GameObject("eye");
            _eye.transform.position = new Vector3(0f, 1.6f, 0f);
            _entity = new GameObject("entity");
            _entity.transform.SetPositionAndRotation(Start, Quaternion.identity);
            _entity.AddComponent<BoxCollider>();
            _interactable = _entity.AddComponent<RayInteractable>();
            _motif = _entity.AddComponent<ObjectPlacementMotif>();
            Set(_motif, "_entityTransform", _entity.transform, typeof(PlacementMotif));
            Set(_motif, "_centerEyeAnchor", _eye.transform, typeof(PlacementMotif));
            Set(_motif, "_clampToWalkable", false, typeof(PlacementMotif));
            Set(_motif, "_rayInteractable", _interactable, typeof(ObjectPlacementMotif));
            Set(_motif, "_collider", _entity.GetComponent<Collider>(), typeof(ObjectPlacementMotif));
            Invoke(_motif, "Subscribe");
        }

        [TearDown]
        public void TearDown()
        {
            if (_motif != null) Invoke(_motif, "OnDestroy");
            ObjectMoveHistory.Clear();
            LocomotionHold.ResetStatics();
            if (_entity != null) UnityEngine.Object.DestroyImmediate(_entity);
            if (_eye != null) UnityEngine.Object.DestroyImmediate(_eye);
            if (_other != null) UnityEngine.Object.DestroyImmediate(_other);
            if (_modeHost != null) UnityEngine.Object.DestroyImmediate(_modeHost);
            foreach (GameObject go in _parked) if (go != null) go.SetActive(true);
            _parked.Clear();
            InteractionMotif.FocusOverride = null;
            EditInput.ResetForTests();
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        // The undo and box tests scan the scene for tools at work; leftovers from other fixtures are set aside (as in ObjectMoveUndoTests).
        private void ParkLeftovers()
        {
            var leftovers = new HashSet<GameObject>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<BoxAnnotationMotif>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in UnityEngine.Object.FindObjectsByType<FloorVertexCollectionMotif>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in UnityEngine.Object.FindObjectsByType<ManipulationContext>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in UnityEngine.Object.FindObjectsByType<SpatialBlend.Samples.Scanning.PassthroughPanelManager>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in UnityEngine.Object.FindObjectsByType<GltfObjectControlPanelMotif>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in UnityEngine.Object.FindObjectsByType<InteractionMotif>(FindObjectsSortMode.None)) if (c.IsClaimed) leftovers.Add(c.gameObject);
            foreach (GameObject go in leftovers)
            {
                TestContext.WriteLine("[HandEditingTests] parked a leftover from another fixture: " + go.name);
                go.SetActive(false);
                _parked.Add(go);
            }
        }

        private void UseHands()
        {
            _controllerPresent = false;
            _handPresent = true;
        }

        private void NextFrame()
        {
            EditInput.EndFrame();
            _frame++;
            _time += 1f / 72f;
        }

        // The hand reaches out (+) or in (-) along the arm's direction from the shoulder.
        private void Reach(float metres) => _handOrigin = Shoulder + Vector3.forward * (Arm + metres);

        private Vector3 EntityPosition => _entity.transform.position;

        // ---- select, grab and move with the hand ray ----

        [Test]
        public void APinchOnTheHandRayGrabsAndCarriesAnObjectLikeTheTrigger()
        {
            UseHands();
            EditInput.RawHover(_hand, _interactable);
            EditInput.RawSelect(_hand, _interactable);
            Assert.That(_motif.IsHeld, Is.True, "the motif took the hand's select as a grab");

            // The hand sweeps the ray to the right: the object follows the ray at the distance it was grabbed.
            float distance = Vector3.Distance(_handOrigin, Start);
            _handForward = (new Vector3(1f, 1f, 2f) - _handOrigin).normalized;
            _motif.StepGrab(1f / 15f);
            Vector3 expected = _handOrigin + _handForward * distance;
            Assert.That(Vector3.Distance(EntityPosition, expected), Is.LessThan(1e-3f));

            EditInput.RawUnselect(_hand, _interactable);
            Assert.That(_motif.IsHeld, Is.False, "letting go of the pinch drops it");
            Assert.That(ObjectMoveHistory.Last, Is.SameAs(_motif), "the move is on the undo history like a controller's");
        }

        [Test]
        public void TheControllerTriggerStillGrabsTheSameWay()
        {
            EditInput.RawSelect(_controller, _interactable);
            Assert.That(_motif.IsHeld, Is.True);
            _controllerForward = (new Vector3(-1f, 1f, 2f) - _controllerOrigin).normalized;
            _motif.StepGrab(1f / 15f);
            Assert.That(EntityPosition.x, Is.LessThan(-0.5f));
            EditInput.RawUnselect(_controller, _interactable);
            Assert.That(_motif.IsHeld, Is.False);
        }

        [Test]
        public void AHandSelectIsNotAGrabInInteractionMode()
        {
            UseHands();
            _mode.SetMode(InteractionMode.Interaction);
            EditInput.RawSelect(_hand, _interactable);
            Assert.That(_motif.IsHeld, Is.False, "the mode gate holds for hands too");
        }

        // ---- rotate and push/pull ----

        [Test]
        public void TwistingTheWristWhilePinchingTurnsTheObjectAndKeepsItsYaw()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            _wrist = Quaternion.AngleAxis(60f, _fingerAxis); // clockwise roll seen from behind the hand
            Assert.That(EditInput.GrabAdjust(_hand).x, Is.EqualTo(1f).Within(1e-4f));

            _motif.StepGrab(0.5f);
            Assert.That(_entity.transform.eulerAngles.y, Is.EqualTo(45f).Within(0.01f), "full twist is the thumbstick's full turn: 90 degrees per second");
            Assert.That(_motif.KeepsUserYaw, Is.True);

            _wrist = Quaternion.AngleAxis(-60f, _fingerAxis);
            _motif.StepGrab(0.5f);
            Assert.That(_entity.transform.eulerAngles.y, Is.EqualTo(0f).Within(0.01f), "twisting back turns it back");
        }

        [Test]
        public void ASmallTwistOrAReachInsideTheDeadZoneDoesNothing()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            _wrist = Quaternion.AngleAxis(HandGrabAdjust.TwistDeadZoneDegrees - 1f, _fingerAxis);
            Reach(HandGrabAdjust.ReachDeadZoneMetres - 0.02f);
            Assert.That(EditInput.GrabAdjust(_hand), Is.EqualTo(Vector2.zero));
            float distance = _motif.GrabDistance;
            _motif.StepGrab(1f);
            Assert.That(_entity.transform.eulerAngles.y, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(_motif.GrabDistance, Is.EqualTo(distance).Within(1e-4f));
            Assert.That(_motif.KeepsUserYaw, Is.False);
        }

        [Test]
        public void JustPastTheDeadZoneTheOutputStartsAboveTheMotifsOwnStickDeadZone()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            _wrist = Quaternion.AngleAxis(HandGrabAdjust.TwistDeadZoneDegrees + 0.5f, _fingerAxis);
            float x = EditInput.GrabAdjust(_hand).x;
            Assert.That(x, Is.GreaterThan(ObjectPlacementMotif.StickDeadZone), "so the dead zone the user feels is the 12 degrees, not 12 plus the stick's 25%");
            float before = _entity.transform.eulerAngles.y;
            _motif.StepGrab(0.1f);
            Assert.That(_entity.transform.eulerAngles.y, Is.Not.EqualTo(before).Within(1e-4f));
        }

        [Test]
        public void ReachingOutWhilePinchingPushesAndReachingInPullsWithinThePanelsLimits()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            float start = _motif.GrabDistance;

            Reach(HandGrabAdjust.ReachFullMetres);
            _motif.StepGrab(0.4f);
            Assert.That(_motif.GrabDistance, Is.EqualTo(start + ObjectPlacementMotif.PushPullMetresPerSecond * 0.4f).Within(1e-3f));

            Reach(-HandGrabAdjust.ReachFullMetres);
            _motif.StepGrab(100f);
            Assert.That(_motif.GrabDistance, Is.EqualTo(ObjectPlacementMotif.MinGrabDistance), "clamped like a panel");
        }

        [Test]
        public void SweepingTheArmToAimIsNeitherAReachNorATwist()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            float distance = _motif.GrabDistance;
            // The arm pivots about the shoulder: 90 degrees to the right, the hand stays 0.45 m from it, and the wrist swings with the aim.
            for (int degrees = 0; degrees <= 90; degrees += 15)
            {
                var around = Quaternion.AngleAxis(degrees, Vector3.up);
                _handOrigin = Shoulder + around * Vector3.forward * Arm;
                _handForward = around * Vector3.forward;
                _wrist = around;
                Assert.That(EditInput.GrabAdjust(_hand), Is.EqualTo(Vector2.zero), degrees + " degrees: aiming is not an adjustment");
            }
            _motif.StepGrab(1f);
            Assert.That(_motif.GrabDistance, Is.EqualTo(distance).Within(1e-4f));
            Assert.That(_motif.KeepsUserYaw, Is.False);
        }

        [Test]
        public void TurningTheHeadAloneIsNotAReach()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            foreach (float yaw in new[] { 30f, 45f, 60f, -45f, -60f })
            {
                _head = new Pose(_head.position, Quaternion.AngleAxis(yaw, Vector3.up));
                Assert.That(EditInput.GrabAdjust(_hand).y, Is.EqualTo(0f), "head yaw " + yaw + " with the hand still");
            }
            // Walking is not a reach either: the head and the hand move together.
            _head = new Pose(_head.position + Vector3.forward, _head.rotation);
            _handOrigin += Vector3.forward;
            Assert.That(EditInput.GrabAdjust(_hand).y, Is.EqualTo(0f));
        }

        [Test]
        public void ABriefHeadGlanceIsNotAReachAndATurnOfTheBodyReadsRightOnceTheEstimateCaughtUp()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);

            // A glance: the head turns 45 degrees for a third of a second and back, the hand still.
            _head = new Pose(_head.position, Quaternion.AngleAxis(45f, Vector3.up));
            for (int i = 0; i < 24; i++) { _time += 1f / 72f; Assert.That(EditInput.GrabAdjust(_hand).y, Is.EqualTo(0f), "glance frame " + i); }
            _head = new Pose(_head.position, Quaternion.identity);
            _time += 1f / 72f;
            Assert.That(EditInput.GrabAdjust(_hand).y, Is.EqualTo(0f));

            // The body turns 60 degrees about a centre 10 cm behind the eye: head, shoulder and hand turn together, and stay turned.
            var centre = new Vector3(0f, 1.6f, -0.1f);
            var turn = Quaternion.AngleAxis(60f, Vector3.up);
            Vector3 headStart = _head.position, handStart = _handOrigin;
            _head = new Pose(centre + turn * (headStart - centre), turn);
            _handOrigin = centre + turn * (handStart - centre);
            float worst = 0f;
            for (int i = 0; i < 72 * 8; i++)
            {
                _time += 1f / 72f;
                float y = EditInput.GrabAdjust(_hand).y;
                if (i < 72 * 6) worst = Mathf.Max(worst, Mathf.Abs(y));
            }
            Assert.That(worst, Is.LessThanOrEqualTo(1f), "the catch-up may push or pull for a moment (documented)");
            Assert.That(EditInput.GrabAdjust(_hand).y, Is.EqualTo(0f), "eight seconds after the turn the estimate has caught up and the reach reads zero");
        }

        [Test]
        public void AimingDownAndSidewaysIsNotATwistButARollIsStillOneWhenTheRayIsPitchedDown()
        {
            UseHands();
            // The ray is pitched 45 degrees down (a floor object), and the wrist's finger axis points along it.
            _fingerAxis = (Vector3.forward + Vector3.down).normalized;
            _wrist = Quaternion.LookRotation(_fingerAxis, Vector3.up);
            EditInput.RawSelect(_hand, _interactable);
            Quaternion start = _wrist;

            _wrist = Quaternion.AngleAxis(40f, Vector3.up) * start; // aim 40 degrees to the side
            Assert.That(EditInput.GrabAdjust(_hand).x, Is.LessThan(ObjectPlacementMotif.StickDeadZone), "a sideways aim stays under the twist dead zone");

            _wrist = start * Quaternion.AngleAxis(60f, Vector3.forward); // a roll about the wrist's own axis, whatever way it points
            Assert.That(EditInput.GrabAdjust(_hand).x, Is.EqualTo(1f).Within(1e-4f));

            // After a 90 degree sweep a roll still reads: the axis moved with the hand.
            var swept = Quaternion.AngleAxis(90f, Vector3.up) * start;
            _wrist = swept * Quaternion.AngleAxis(60f, Vector3.forward);
            Assert.That(EditInput.GrabAdjust(_hand).x, Is.GreaterThan(0.9f), "wrist twist is measured in the wrist's frame");
        }

        [Test]
        public void TheStrongerOfTwistAndReachWinsSoAPushDoesNotAlsoTurn()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            _wrist = Quaternion.AngleAxis(30f, _fingerAxis); // part of the way to full
            Reach(HandGrabAdjust.ReachFullMetres);          // full
            _motif.StepGrab(0.2f);
            Assert.That(_entity.transform.eulerAngles.y, Is.EqualTo(0f).Within(1e-3f), "pushing, not turning");
        }

        [Test]
        public void TheThumbstickStillTurnsAndPushesForTheController()
        {
            EditInput.RawSelect(_controller, _interactable);
            _stick = new Vector2(1f, 0f);
            _motif.StepGrab(0.5f);
            Assert.That(_entity.transform.eulerAngles.y, Is.EqualTo(45f).Within(0.01f));
        }

        [Test]
        public void TheTwistIsMeasuredAboutTheRayNotAboutAnyOtherAxis()
        {
            Vector3 forward = Vector3.forward;
            Assert.That(HandGrabAdjust.TwistDegrees(Quaternion.identity, Quaternion.AngleAxis(40f, forward), forward), Is.EqualTo(40f).Within(1e-3f));
            Assert.That(HandGrabAdjust.TwistDegrees(Quaternion.identity, Quaternion.AngleAxis(-40f, forward), forward), Is.EqualTo(-40f).Within(1e-3f));
            // Aiming the hand elsewhere (a swing about another axis) is not a twist.
            Assert.That(HandGrabAdjust.TwistDegrees(Quaternion.identity, Quaternion.AngleAxis(50f, Vector3.up), forward), Is.EqualTo(0f).Within(1e-3f));
            // Both together: only the roll counts.
            var both = Quaternion.AngleAxis(50f, Vector3.up) * Quaternion.AngleAxis(25f, forward);
            Assert.That(HandGrabAdjust.TwistDegrees(Quaternion.identity, both, forward), Is.EqualTo(25f).Within(0.5f));
            // A constant offset of the start pose never matters.
            var offset = Quaternion.Euler(10f, 80f, -30f);
            Assert.That(HandGrabAdjust.TwistDegrees(offset, Quaternion.AngleAxis(33f, forward) * offset, forward), Is.EqualTo(33f).Within(1e-3f));
        }

        [Test]
        public void TheRampHasADeadZoneAndSaturates()
        {
            Assert.That(HandGrabAdjust.Ramp(5f, 12f, 50f), Is.Zero);
            Assert.That(HandGrabAdjust.Ramp(31f, 12f, 50f), Is.EqualTo(Mathf.Lerp(HandGrabAdjust.StickDeadZone + 0.01f, 1f, 0.5f)).Within(1e-4f));
            Assert.That(HandGrabAdjust.Ramp(-500f, 12f, 50f), Is.EqualTo(-1f));
            Vector2 rates = HandGrabAdjust.Rates(50f, -0.30f);
            Assert.That(rates.x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(rates.y, Is.EqualTo(-1f).Within(1e-5f));
        }

        // ---- no double handling: one input at a time ----

        [Test]
        public void WithBothPresentAndNeitherUsedTheControllerWinsAndHoverFollowsIt()
        {
            _controllerPresent = _handPresent = true;
            Assert.That(EditInput.ActiveSource, Is.EqualTo(EditInputSource.Controller));
            var seen = new List<EditInputSource>();
            Action<EditPointer, RayInteractable> record = (p, i) => seen.Add(p.Source);
            EditInput.Hovered += record;
            try
            {
                EditInput.RawHover(_hand, _interactable);
                Assert.That(seen, Is.Empty, "a resting hand's ray is not the input");
                EditInput.RawHover(_controller, _interactable);
                Assert.That(seen, Is.EqualTo(new[] { EditInputSource.Controller }));
            }
            finally { EditInput.Hovered -= record; }
        }

        [Test]
        public void WithBothPresentTheOneUsedLastIsTheActiveInputAndTheOtherCannotGrabAtTheSameTime()
        {
            _controllerPresent = _handPresent = true;
            EditInput.RawSelect(_controller, _interactable);
            EditInput.RawUnselect(_controller, _interactable);   // the controller was used
            Assert.That(_motif.IsHeld, Is.False);
            _time += 1f;
            EditInput.RawSelect(_hand, _interactable);           // then the hand pinched: it is the input now
            Assert.That(EditInput.ActiveSource, Is.EqualTo(EditInputSource.Hand));
            Assert.That(_motif.IsHeld, Is.True);
            EditInput.RawSelect(_controller, _interactable);     // a trigger pulled during the pinch is not a second grab
            EditInput.RawUnselect(_controller, _interactable);
            Assert.That(_motif.IsHeld, Is.True, "the hand's grab is untouched");
            EditInput.RawUnselect(_hand, _interactable);
            Assert.That(_motif.IsHeld, Is.False);
            _time += 1f;
            EditInput.RawSelect(_controller, _interactable);     // and back
            Assert.That(EditInput.ActiveSource, Is.EqualTo(EditInputSource.Controller));
            Assert.That(_motif.IsHeld, Is.True);
        }

        [Test]
        public void WhileOneInputHoldsAnObjectTheOtherCannotTakeItOrDropIt()
        {
            _controllerPresent = _handPresent = true;
            int selects = 0, unselects = 0;
            Action<EditPointer, RayInteractable> onSelect = (p, i) => selects++;
            Action<EditPointer, RayInteractable> onUnselect = (p, i) => unselects++;
            EditInput.Selected += onSelect;
            EditInput.Unselected += onUnselect;
            try
            {
                EditInput.RawSelect(_controller, _interactable);
                EditInput.RawSelect(_hand, _interactable);      // a second pointer on the same object: ignored
                EditInput.RawUnselect(_hand, _interactable);    // and its release does not drop the controller's grab
                Assert.That(_motif.IsHeld, Is.True);
                Assert.That(selects, Is.EqualTo(1));
                Assert.That(unselects, Is.Zero);
                EditInput.RawUnselect(_controller, _interactable);
                Assert.That(unselects, Is.EqualTo(1));
                Assert.That(_motif.IsHeld, Is.False);
            }
            finally
            {
                EditInput.Selected -= onSelect;
                EditInput.Unselected -= onUnselect;
            }
        }

        [Test]
        public void OnlyTheActiveInputsHoverReachesTheMotifs()
        {
            UseHands();
            var hovered = new List<EditInputSource>();
            Action<EditPointer, RayInteractable> record = (p, i) => hovered.Add(p.Source);
            EditInput.Hovered += record;
            try
            {
                EditInput.RawHover(_controller, _interactable); // a resting controller's ray
                EditInput.RawHover(_hand, _interactable);
                EditInput.RawHover(_hand, _interactable);       // the same hover twice is one hover
                Assert.That(hovered, Is.EqualTo(new[] { EditInputSource.Hand }));
            }
            finally { EditInput.Hovered -= record; }
        }

        [Test]
        public void SwitchingTheActiveInputHandsTheHoverOverWithoutALeftBehindHighlight()
        {
            _controllerPresent = _handPresent = true;
            _handHover = _interactable;
            var log = new List<string>();
            Action<EditPointer, RayInteractable> hover = (p, i) => log.Add("hover " + p.Source);
            Action<EditPointer, RayInteractable> unhover = (p, i) => log.Add("unhover " + p.Source);
            EditInput.Hovered += hover;
            EditInput.Unhovered += unhover;
            try
            {
                EditInput.RawHover(_controller, _interactable);       // controller active: its hover is the one the tools see
                _controllerPresent = false;                            // controller put down: the hand is the input now
                EditInput.Tick();
                Assert.That(log, Is.EqualTo(new[] { "hover Controller", "unhover Controller", "hover Hand" }));
            }
            finally
            {
                EditInput.Hovered -= hover;
                EditInput.Unhovered -= unhover;
            }
        }

        // ---- the object menu follows the hover of either ray ----

        [Test]
        public void TheHandRayHighlightsAnObjectForTheMenuLikeTheControllerRay()
        {
            UseHands();
            var menuObject = new GameObject("scan");
            _other = menuObject;
            menuObject.AddComponent<BoxCollider>();
            var ray = menuObject.AddComponent<RayInteractable>();
            var menu = menuObject.AddComponent<ManipulationPanelMotif>();
            Set(menu, "_rayInteractable", ray, typeof(ManipulationPanelMotif));
            Invoke(menu, "SubscribeToSceneRayInteractor");

            EditInput.RawHover(_hand, ray);
            Assert.That((bool)Get(menu, "_isHovered"), Is.True);
            EditInput.RawUnhover(_hand, ray);
            Assert.That((bool)Get(menu, "_isHovered"), Is.False);
            Invoke(menu, "OnDestroy");
        }

        // ---- the box: four points and the height with the pinch, undo with the button ----

        private BoxAnnotationMotif StartBox()
        {
            _other = new GameObject("box-motif");
            var box = _other.AddComponent<BoxAnnotationMotif>();
            box.BeginCreationFromExternal(new Vector3(0f, 0f, 2f), Vector3.up);
            Invoke(box, "Update"); // the frame the creation began on skips its input
            NextFrame();
            return box;
        }

        private int PointIndex(BoxAnnotationMotif box) => (int)Get(box, "_currentPointIndex");

        private void Pinch(bool down)
        {
            _pinch = down;
        }

        [Test]
        public void EachPinchLocksTheNextBoxPointJustAsEachTriggerPullDoes()
        {
            UseHands();
            var box = StartBox();
            int start = PointIndex(box);

            Pinch(true);
            Invoke(box, "Update");
            Assert.That(PointIndex(box), Is.EqualTo(start + 1), "pinch down locks a point");
            NextFrame();

            Invoke(box, "Update"); // still pinching: no second point
            Assert.That(PointIndex(box), Is.EqualTo(start + 1), "holding is not a second pinch");
            Pinch(false);
            NextFrame();
            Pinch(true);
            Invoke(box, "Update");
            Assert.That(PointIndex(box), Is.EqualTo(start + 2), "the next pinch locks the next point");
        }

        [Test]
        public void ThePinchThatPressesAModeButtonIsNotABoxPoint()
        {
            UseHands();
            var box = StartBox();
            int start = PointIndex(box);
            var buttonObject = new GameObject("chip");
            try
            {
                buttonObject.AddComponent<BoxCollider>();
                var button = buttonObject.AddComponent<RayInteractable>();
                buttonObject.AddComponent<SpatialBlend.Interactions.UI.ModeSwitchClickTarget>();
                _handHover = button;
                Pinch(true);
                Invoke(box, "Update");
                Assert.That(PointIndex(box), Is.EqualTo(start), "the Undo or chip button is pressed, no point is placed");
            }
            finally { UnityEngine.Object.DestroyImmediate(buttonObject); }
        }

        [Test]
        public void TheUndoButtonStepsABoxBackLikeB()
        {
            UseHands();
            var box = StartBox();
            Pinch(true);
            Invoke(box, "Update");
            NextFrame();
            Pinch(false);
            int placed = PointIndex(box);

            EditInput.RequestUndo();
            Invoke(box, "Update");
            Assert.That(PointIndex(box), Is.EqualTo(placed), "it reaches the tools in the next frame, not the same one");
            NextFrame();
            Invoke(box, "Update");
            Assert.That(PointIndex(box), Is.EqualTo(placed - 1));
            NextFrame();
            Invoke(box, "Update");
            Assert.That(PointIndex(box), Is.EqualTo(placed - 1), "one request undoes once");
        }

        [Test]
        public void BOnTheControllerStillStepsABoxBack()
        {
            var box = StartBox();
            _trigger = true;
            Invoke(box, "Update");
            NextFrame();
            _trigger = false;
            int placed = PointIndex(box);
            _bDown = true;
            Invoke(box, "Update");
            Assert.That(PointIndex(box), Is.EqualTo(placed - 1));
        }

        [Test]
        public void ABoxFloorPinchStartsABoxAtTheHandRaysFloorPoint()
        {
            UseHands();
            var surfaces = new GameObject("surfaces").AddComponent<EnvironmentSurfaceManager>();
            var anchorObject = new GameObject("floor");
            try
            {
                var anchor = anchorObject.AddComponent<MRUKAnchor>();
                anchorObject.AddComponent<BoxCollider>();
                var floor = anchorObject.AddComponent<RayInteractable>();
                var floors = (Dictionary<MRUKAnchor, RayInteractable>)Get(surfaces, "_floorInteractables");
                floors[anchor] = floor;
                Vector3? hit = new Vector3(1.5f, 0f, 2.5f);
                _hand.HitSource = () => hit;
                Vector3 selectedAt = Vector3.zero;
                surfaces.OnFloorSelected += (a, p) => selectedAt = p;

                surfaces.OnInteractorSelect(_hand, floor);

                Assert.That(selectedAt, Is.EqualTo(hit.Value), "the floor point comes from the pointer that pinched, not from the controller's ray");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(anchorObject);
                UnityEngine.Object.DestroyImmediate(surfaces.gameObject);
            }
        }

        // ---- undo of a move ----

        [Test]
        public void TheUndoButtonPutsTheLastMovedObjectBackWhereItWas()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            _handForward = (new Vector3(2f, 1f, 3f) - _handOrigin).normalized;
            _motif.StepGrab(1f / 15f);
            EditInput.RawUnselect(_hand, _interactable);
            Assert.That(Vector3.Distance(EntityPosition, Start), Is.GreaterThan(0.5f));
            NextFrame();

            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.True);
            EditInput.RequestUndo();
            Invoke(_motif, "Update");
            Assert.That(Vector3.Distance(EntityPosition, Start), Is.GreaterThan(0.5f), "the request lands in the next frame");
            NextFrame();
            Invoke(_motif, "Update");
            Assert.That(Vector3.Distance(EntityPosition, Start), Is.LessThan(1e-4f));
        }

        [Test]
        public void TheUndoRequestIsIgnoredInInteractionMode()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            _handForward = (new Vector3(2f, 1f, 3f) - _handOrigin).normalized;
            _motif.StepGrab(1f / 15f);
            EditInput.RawUnselect(_hand, _interactable);
            Vector3 moved = EntityPosition;
            _mode.SetMode(InteractionMode.Interaction);
            EditInput.RequestUndo();
            NextFrame();
            Invoke(_motif, "Update");
            Assert.That(EntityPosition, Is.EqualTo(moved));
        }

        // ---- accept: trigger or pinch ----

        [Test]
        public void AcceptIsTheTriggerForTheControllerAndThePinchForTheHand()
        {
            _trigger = true;
            Assert.That(EditInput.AcceptDown, Is.True);
            NextFrame();
            Assert.That(EditInput.AcceptDown, Is.False);
            _trigger = false;
            Assert.That(EditInput.AcceptUp, Is.True);
            NextFrame();

            UseHands();
            _trigger = true; // a trigger pulled on a controller that is not the input does nothing
            Assert.That(EditInput.AcceptHeld, Is.False);
            _pinch = true;
            Assert.That(EditInput.AcceptDown, Is.True);
            NextFrame();
            _pinch = false;
            Assert.That(EditInput.AcceptUp, Is.True);
        }

        [Test]
        public void TheToolsAimWithTheActivePointersRay()
        {
            _handOrigin = new Vector3(5f, 1f, 5f);
            Assert.That(EditInput.ActiveRay.Value.origin, Is.EqualTo(_controllerOrigin));
            UseHands();
            Assert.That(EditInput.ActiveRay.Value.origin, Is.EqualTo(_handOrigin));
        }

        // ---- the hands' Undo button and Menu button (ModeSwitchAssist) ----

        private ModeSwitchAssist MakeAssist(out bool[] usesHands)
        {
            var flag = new[] { true };
            usesHands = flag;
            var host = new GameObject("assist");
            _other = host;
            var assist = host.AddComponent<ModeSwitchAssist>();
            Invoke(assist, "Awake");
            Invoke(assist, "Start");
            assist.UsesHands = () => flag[0];
            assist.Haptic = _ => { };
            assist.Chime = _ => { };
            assist.ControllerOnlyInPlay = () => new List<string>();
            return assist;
        }

        [Test]
        public void HandsSeeAnUndoButtonOnlyInEditingOnlyWhenThereIsSomethingToUndo()
        {
            UseHands();
            var assist = MakeAssist(out var hands);
            assist.Step(0f);
            Assert.That(assist.Ui.UndoVisible, Is.False, "nothing to undo");

            // A move to undo, found by the assist's poll.
            EditInput.RawSelect(_hand, _interactable);
            _handForward = (new Vector3(2f, 1f, 3f) - _handOrigin).normalized;
            _motif.StepGrab(1f / 15f);
            EditInput.RawUnselect(_hand, _interactable);
            assist.Step(1f);
            assist.Step(2f);
            Assert.That(assist.Ui.UndoVisible, Is.True, "Editing, hands, a move to undo");

            hands[0] = false;
            assist.Step(3f);
            Assert.That(assist.Ui.UndoVisible, Is.False, "controller users have B");

            hands[0] = true;
            _mode.SetMode(InteractionMode.Interaction);
            assist.Step(4f);
            Assert.That(assist.Ui.UndoVisible, Is.False, "not in Interaction");
        }

        [Test]
        public void PressingTheUndoButtonRequestsAnUndoAndCountsAsEditingActivity()
        {
            UseHands();
            var assist = MakeAssist(out _);
            assist.Step(0f);
            Invoke(assist, "OnUndoClicked");
            NextFrame();
            Assert.That(EditInput.UndoDown, Is.True);
            NextFrame();
            Assert.That(EditInput.UndoDown, Is.False, "once");
        }

        [Test]
        public void HoveringAScanWithAHandFloatsTheMenuButtonOverItAndAControllerSeesNone()
        {
            UseHands();
            var scan = new GameObject("scan");
            var scanRay = scan.AddComponent<RayInteractable>();
            scan.AddComponent<BoxCollider>();
            scan.AddComponent<ManipulationPanelMotif>();
            var assist = MakeAssist(out var hands);
            try
            {
                assist.HoveredSource = () => scanRay;
                assist.SelectedSource = () => null;
                assist.Step(0f);
                assist.Step(0.1f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.False, "a pass over the object shows nothing");
                assist.Step(0.4f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True);
                Assert.That(assist.Ui.MenuPromptRoot, Is.SameAs(scan.transform));

                // The ray leaves the object and crosses to the button: the prompt waits for it.
                assist.HoveredSource = () => assist.Ui.MenuPromptObject.GetComponentInChildren<SpatialBlend.Interactions.UI.ModeSwitchClickTarget>().Interactable;
                assist.Step(0.6f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True);

                // A controller user: no button.
                assist.HoveredSource = () => scanRay;
                hands[0] = false;
                assist.Step(1f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(scan); }
        }

        [Test]
        public void TheMenuButtonLingersAfterTheHandLeavesSoItCanBeReached()
        {
            UseHands();
            var scan = new GameObject("scan");
            var scanRay = scan.AddComponent<RayInteractable>();
            scan.AddComponent<BoxCollider>();
            scan.AddComponent<ManipulationPanelMotif>();
            var assist = MakeAssist(out var hands);
            try
            {
                Assert.That(ModeSwitchAssist.DefaultHandMenuLingerSeconds, Is.EqualTo(3.0f));

                assist.HoveredSource = () => scanRay;
                assist.SelectedSource = () => null;
                float dwell = ModeSwitchAssist.HandMenuDwellSeconds;
                assist.Step(0f);
                assist.Step(dwell);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True);

                // The hand has left the object and is not on the button yet.
                assist.HoveredSource = () => null;
                assist.Step(dwell);
                assist.Step(dwell + 2.5f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True, "still there 2.5 s after the hand left");

                // Back on the object at 2.5 s: that hide is cancelled.
                assist.HoveredSource = () => scanRay;
                assist.Step(dwell + 2.5f);
                assist.HoveredSource = () => null;
                float left = dwell + 2.5f;
                assist.Step(left);
                assist.Step(left + 2.5f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True, "the linger restarted when the hand left again");

                // On the button itself it stays, past the 3 s hide.
                var button = assist.Ui.MenuPromptObject.GetComponentInChildren<SpatialBlend.Interactions.UI.ModeSwitchClickTarget>().Interactable;
                assist.HoveredSource = () => button;
                assist.Step(left + 3.0f + 5f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True, "the ray on the button keeps it");

                assist.HoveredSource = () => null;
                float leftButton = left + 3.0f + 5f;
                assist.Step(leftButton);
                assist.Step(leftButton + 2.5f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True);
                assist.Step(leftButton + 3.0f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.False, "it hides 3 s after the hand leaves the button");

                // Controllers never see it. Show it for the hand again, then switch input: it goes at once, not after the hand linger.
                float back = leftButton + 3.0f + 0.1f;
                assist.HoveredSource = () => scanRay;
                hands[0] = true;
                assist.Step(back);
                assist.Step(back + dwell);
                Assert.That(assist.Ui.MenuPromptVisible, Is.True);
                hands[0] = false;
                assist.Step(back + dwell + 0.1f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.False, "a controller keeps the A button; this affordance stays hidden");
            }
            finally { UnityEngine.Object.DestroyImmediate(scan); }
        }

        [Test]
        public void TheMenuButtonIsOfferedOnlyForObjectsWithAnObjectMenu()
        {
            UseHands();
            var plain = new GameObject("plain");
            var ray = plain.AddComponent<RayInteractable>();
            plain.AddComponent<BoxCollider>();
            var assist = MakeAssist(out _);
            try
            {
                assist.HoveredSource = () => ray;
                assist.SelectedSource = () => null;
                assist.Step(0f);
                assist.Step(1f);
                assist.Step(2f);
                Assert.That(assist.Ui.MenuPromptVisible, Is.False);
                Assert.That(ModeSwitchAssist.TryResolveMenuTarget(ray, out _, out _, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(plain); }
        }

        // ---- the hands' tools do not depend on the rest of the assist ----

        [Test]
        public void TheHandsToolsWorkWhileTheTutorialHasTheAssistSuspendedAndWhileTheAppHasItOff()
        {
            UseHands();
            var assist = MakeAssist(out _);
            var owner = new object();
            ModeSwitchAssist.Suspend(owner);
            try
            {
                var scan = new GameObject("scan");
                var scanRay = scan.AddComponent<RayInteractable>();
                scan.AddComponent<BoxCollider>();
                scan.AddComponent<ManipulationPanelMotif>();
                try
                {
                    // A move to undo, so the Undo button has a reason to show.
                    EditInput.RawSelect(_hand, _interactable);
                    _handForward = (new Vector3(2f, 1f, 3f) - _handOrigin).normalized;
                    _motif.StepGrab(1f / 15f);
                    EditInput.RawUnselect(_hand, _interactable);
                    assist.HoveredSource = () => scanRay;
                    assist.SelectedSource = () => null;

                    assist.Step(0f);
                    assist.Step(0.5f);
                    assist.Step(1f);
                    Assert.That(ModeSwitchAssist.IsSuspended, Is.True);
                    Assert.That(assist.Ui.UndoVisible, Is.True, "the tutorial's box task needs Undo on hands");
                    Assert.That(assist.Ui.MenuPromptVisible, Is.True, "the tutorial's scan step needs the Menu button on hands");
                    Assert.That(assist.Ui.ChipVisible, Is.False, "the rest of the assist stays suspended");

                    // The app has the assist off (before its session is ready): the tools still show.
                    ModeSwitchAssist.Resume(owner);
                    assist.SetActive(false);
                    assist.Step(2f);
                    assist.Step(2.5f);
                    Assert.That(assist.IsActive, Is.False);
                    Assert.That(assist.Ui.UndoVisible, Is.True);
                    Assert.That(assist.Ui.MenuPromptVisible, Is.True);
                    Assert.That(assist.Ui.ChipVisible, Is.False);
                }
                finally { UnityEngine.Object.DestroyImmediate(scan); }
            }
            finally { ModeSwitchAssist.Resume(owner); }
        }

        [Test]
        public void ThePressedMenuButtonOpensTheObjectsMenuLikeA()
        {
            var scan = new GameObject("scan");
            try
            {
                scan.AddComponent<BoxCollider>();
                scan.AddComponent<RayInteractable>();
                var menu = scan.AddComponent<ManipulationPanelMotif>();
                Assert.That(menu.Context, Is.Null);
                menu.ToggleMenu();
                Assert.That(menu.Context, Is.Not.Null, "the menu's context was made for this object whether or not the ray is on it");
                Assert.That(ModeSwitchAssist.TryResolveMenuTarget(scan.GetComponent<RayInteractable>(), out var root, out var toggle, out _), Is.True);
                Assert.That(root, Is.SameAs(scan.transform));
                Assert.That(toggle, Is.Not.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(scan); }
        }

        [Test]
        public void AGlbObjectsControlPanelIsAMenuTargetToo()
        {
            var obj = new GameObject("glb");
            try
            {
                obj.AddComponent<BoxCollider>();
                var ray = obj.AddComponent<RayInteractable>();
                obj.AddComponent<GltfObjectControlPanelMotif>();
                Assert.That(ModeSwitchAssist.TryResolveMenuTarget(ray, out var root, out var toggle, out bool open), Is.True);
                Assert.That(root, Is.SameAs(obj.transform));
                Assert.That(toggle, Is.Not.Null);
                Assert.That(open, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [Test]
        public void APointerThatGoesAwayMidGrabLetsGoOfItSoNothingStaysGrabbed()
        {
            UseHands();
            EditInput.RawSelect(_hand, _interactable);
            Assert.That(_motif.IsHeld, Is.True);
            EditInput.Unregister(_hand); // the rig is torn down
            Assert.That(_motif.IsHeld, Is.False);
            Assert.That(EditInput.SelectingPointer, Is.Null);
        }

        [Test]
        public void APanelsPushPullTakesAHandsReachAndTheThumbstickAlike()
        {
            var panelObject = new GameObject("panel");
            try
            {
                var panel = panelObject.AddComponent<PanelPlacementMotif>();
                Set(panel, "_distanceFromController", 2f, typeof(PanelPlacementMotif));
                panel.ApplyPushPull(1f, 0.4f);
                Assert.That(panel.DistanceFromController, Is.EqualTo(3f).Within(1e-4f));
                panel.ApplyPushPull(-1f, 100f);
                Assert.That(panel.DistanceFromController, Is.EqualTo(0.3f).Within(1e-4f), "clamped to the panel's minimum");

                // The hand's reach is read for the pointer that holds the panel.
                UseHands();
                EditInput.RawSelect(_hand, _interactable);
                Reach(HandGrabAdjust.ReachFullMetres);
                Assert.That(EditInput.GrabAdjust(_hand).y, Is.EqualTo(1f).Within(1e-4f));
            }
            finally { UnityEngine.Object.DestroyImmediate(panelObject); }
        }

        // ---- what a pinch does in the tutorial's practice objects and the seat aimer ----

        [Test]
        public void ThePracticeObjectIsCarriedByAPinchToo()
        {
            UseHands();
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                go.transform.position = Start;
                var grabbable = SpatialBlend.Samples.FreeformCollaboration.Tutorial.TutorialGrabbable.Attach(go);
                Invoke(grabbable, "Subscribe");
                bool grabbed = false;
                grabbable.Grabbed += () => grabbed = true;
                EditInput.RawSelect(_hand, grabbable.Interactable);
                Assert.That(grabbed, Is.True);
                Assert.That(grabbable.Held, Is.True);
                EditInput.RawUnselect(_hand, grabbable.Interactable);
                Assert.That(grabbable.Held, Is.False);
                Invoke(grabbable, "Unsubscribe");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // ---- helpers ----

        private static void Set(object target, string name, object value, Type declaring = null)
        {
            var field = (declaring ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        private static object Get(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }
            Assert.Fail("no field " + name);
            return null;
        }

        private static void Invoke(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null) { method.Invoke(target, null); return; }
            }
            Assert.Fail("no method " + name);
        }
    }
}
#endif
