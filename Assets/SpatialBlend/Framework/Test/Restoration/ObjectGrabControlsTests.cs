// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using UnityEngine;
using UnityEngine.TestTools;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A grabbed object turns with the right thumbstick left/right and moves along the ray with up/down (as a panel does), and an object
    /// the user turned keeps its yaw on release instead of turning to face whoever moved it. An object nobody turned behaves as before (v1).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ObjectGrabControlsTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _entity;
        private GameObject _eye;
        private ObjectPlacementMotif _motif;
        private bool _ignoreFailing;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // no controller or camera rig in an EditMode run
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_mode, null);
            _mode.SetMode(InteractionMode.SceneEdit);

            _entity = new GameObject("entity");
            _entity.transform.SetPositionAndRotation(new Vector3(0f, 1f, 2f), Quaternion.identity);
            _motif = _entity.AddComponent<ObjectPlacementMotif>();
            _eye = new GameObject("eye");
            _eye.transform.position = new Vector3(0f, 1.6f, 0f);
            Set("_entityTransform", _entity.transform, typeof(PlacementMotif));
            Set("_centerEyeAnchor", _eye.transform, typeof(PlacementMotif));
            Set("_clampToWalkable", false, typeof(PlacementMotif));
            Set("_grabDistance", 2f, typeof(ObjectPlacementMotif));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_entity);
            Object.DestroyImmediate(_eye);
            Object.DestroyImmediate(_modeHost);
            LocomotionHold.ResetStatics();
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        private void Grab(bool grabbed) => Set("_isGrabbed", grabbed, typeof(InteractionMotif));

        private float Yaw => _entity.transform.eulerAngles.y;

        [Test]
        public void StickRightTurnsTheGrabbedObjectClockwiseAtNinetyDegreesPerSecond()
        {
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(1f, 0f), 0.5f);
            Assert.That(Yaw, Is.EqualTo(45f).Within(0.01f));
            Assert.That(_motif.KeepsUserYaw, Is.True);
            _motif.ApplyGrabStick(new Vector2(-0.5f, 0f), 1f);
            Assert.That(Yaw, Is.EqualTo(0f).Within(0.01f), "left turns it back");
            Assert.That(_entity.transform.position, Is.EqualTo(new Vector3(0f, 1f, 2f)), "turning does not move it");
        }

        [Test]
        public void StickUpPushesAndDownPullsWithinThePanelsLimits()
        {
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(0f, 1f), 0.4f);
            Assert.That(_motif.GrabDistance, Is.EqualTo(3f).Within(1e-4f));
            _motif.ApplyGrabStick(new Vector2(0f, -1f), 10f);
            Assert.That(_motif.GrabDistance, Is.EqualTo(ObjectPlacementMotif.MinGrabDistance));
            _motif.ApplyGrabStick(new Vector2(0f, 1f), 100f);
            Assert.That(_motif.GrabDistance, Is.EqualTo(ObjectPlacementMotif.MaxGrabDistance));
            Assert.That(_motif.KeepsUserYaw, Is.False, "pushing is not turning");
        }

        [Test]
        public void TheStrongerAxisWinsAndTheDeadZoneIgnoresARestingThumb()
        {
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(0.3f, 0.9f), 1f);
            Assert.That(Yaw, Is.EqualTo(0f).Within(0.01f), "a push with some sideways drift does not turn");
            Assert.That(_motif.GrabDistance, Is.GreaterThan(2f));
            float distance = _motif.GrabDistance;
            _motif.ApplyGrabStick(new Vector2(0.2f, 0.1f), 1f);
            Assert.That(Yaw, Is.EqualTo(0f).Within(0.01f));
            Assert.That(_motif.GrabDistance, Is.EqualTo(distance));
            Assert.That(_motif.KeepsUserYaw, Is.False);
        }

        [Test]
        public void TheStickDoesNothingWithoutAGrab()
        {
            Grab(false);
            _motif.ApplyGrabStick(new Vector2(1f, 0f), 1f);
            _motif.ApplyGrabStick(new Vector2(0f, 1f), 1f);
            Assert.That(Yaw, Is.EqualTo(0f).Within(0.01f));
            Assert.That(_motif.GrabDistance, Is.EqualTo(2f));
        }

        [Test]
        public void InInteractionModeTheGrabIsDroppedSoTheStickCannotTurnAnything()
        {
            Grab(true);
            _mode.SetMode(InteractionMode.Interaction);
            Assert.That(_motif.ReleaseIfModeBlocked(), Is.True);
            // PlacementMotif.Update releases the exclusive claim when the mode blocks it; a released motif ignores the stick.
            Grab(false);
            _motif.ApplyGrabStick(new Vector2(1f, 0f), 1f);
            Assert.That(Yaw, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void ATurnedObjectKeepsItsYawOnAClampInPlaceRelease()
        {
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(1f, 0f), 1f); // 90 degrees
            Pose pose = _motif.ClampInPlacePose();
            Assert.That(pose.rotation.eulerAngles.y, Is.EqualTo(90f).Within(0.01f));
            Assert.That(pose.position, Is.EqualTo(_entity.transform.position));
        }

        [Test]
        public void AnObjectNobodyTurnedStillFacesTheViewerOnRelease()
        {
            _entity.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
            Grab(true);
            Pose pose = _motif.ClampInPlacePose();
            // The eye is at the origin side (-z) of the object: facing it is a yaw of 180 degrees (v1).
            Assert.That(pose.rotation.eulerAngles.y, Is.EqualTo(180f).Within(0.01f));
        }

        [Test]
        public void ALaterGrabThatOnlySlidesTheObjectKeepsTheEarlierTurn()
        {
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(1f, 0f), 1f); // 90 degrees
            Grab(false);
            // A new grab, a push only: the user's orientation stays.
            Invoke(typeof(ObjectPlacementMotif), "OnExclusiveClaimed");
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(0f, 1f), 0.2f);
            Assert.That(_motif.KeepsUserYaw, Is.True);
            Assert.That(_motif.ClampInPlacePose().rotation.eulerAngles.y, Is.EqualTo(90f).Within(0.01f));
        }

        [Test]
        public void ARecenterForgetsTheTurnAndFacesTheViewerAgain()
        {
            Grab(true);
            _motif.ApplyGrabStick(new Vector2(1f, 0f), 1f);
            Grab(false);
            _motif.PlaceInFrontOfUser();
            Assert.That(_motif.KeepsUserYaw, Is.False);
        }

        [Test]
        public void ReleaseRotationChoosesTheUsersYawOnlyWhenTurned()
        {
            Quaternion current = Quaternion.Euler(0f, 70f, 0f), facing = Quaternion.Euler(0f, 180f, 0f);
            Assert.That(ObjectPlacementMotif.ReleaseRotation(true, current, facing), Is.EqualTo(current));
            Assert.That(ObjectPlacementMotif.ReleaseRotation(false, current, facing), Is.EqualTo(facing));
        }

        private void Invoke(System.Type owner, string method) =>
            owner.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_motif, null);

        private void Set(string name, object value, System.Type owner) =>
            owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(_motif, value);
    }

    /// <summary>The rig's thumbstick locomotion is held off while a placement grab uses the thumbstick, and given back after.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class LocomotionHoldTests
    {
        private GameObject _rig;
        private Light _turner, _teleport, _alreadyOff;
        private System.Func<System.Collections.Generic.IEnumerable<Behaviour>> _finder;

        [SetUp]
        public void SetUp()
        {
            LocomotionHold.ResetStatics();
            _rig = new GameObject("rig");
            // Any Behaviour stands in for the SDK's locomotion components; the hold only flips enabled.
            _turner = new GameObject("turner").AddComponent<Light>();
            _teleport = new GameObject("teleport").AddComponent<Light>();
            _alreadyOff = new GameObject("off").AddComponent<Light>();
            _alreadyOff.enabled = false;
            _finder = () => new Behaviour[] { _turner, _teleport, _alreadyOff };
            LocomotionHold.Finder = _finder;
        }

        [TearDown]
        public void TearDown()
        {
            LocomotionHold.ResetStatics();
            foreach (var b in new Behaviour[] { _turner, _teleport, _alreadyOff }) if (b != null) Object.DestroyImmediate(b.gameObject);
            Object.DestroyImmediate(_rig);
        }

        [Test]
        public void AHoldDisablesTheLocomotionAndTheReleaseGivesBackOnlyWhatItDisabled()
        {
            var grab = new object();
            LocomotionHold.Hold(grab);
            Assert.That(LocomotionHold.IsHeld, Is.True);
            Assert.That(_turner.enabled, Is.False);
            Assert.That(_teleport.enabled, Is.False);

            LocomotionHold.Release(grab);
            Assert.That(LocomotionHold.IsHeld, Is.False);
            Assert.That(_turner.enabled, Is.True);
            Assert.That(_teleport.enabled, Is.True);
            Assert.That(_alreadyOff.enabled, Is.False, "what the app had off stays off");
        }

        [Test]
        public void TheLocomotionComesBackOnlyWhenTheLastHolderLetsGo()
        {
            object a = new object(), b = new object();
            LocomotionHold.Hold(a);
            LocomotionHold.Hold(b);
            LocomotionHold.Hold(a); // counts once
            LocomotionHold.Release(a);
            Assert.That(_turner.enabled, Is.False);
            LocomotionHold.Release(b);
            Assert.That(_turner.enabled, Is.True);
            LocomotionHold.Release(b); // a stray release changes nothing
            Assert.That(_turner.enabled, Is.True);
        }

        [Test]
        public void AnObjectGrabHoldsTheLocomotionAndItsReleaseOrDisableGivesItBack()
        {
            var entity = new GameObject("entity");
            try
            {
                var motif = entity.AddComponent<ObjectPlacementMotif>();
                typeof(InteractionMotif).GetMethod("ClaimExclusive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(motif, null);
                Assert.That(_turner.enabled, Is.False, "the grab took the stick");
                typeof(InteractionMotif).GetMethod("ReleaseExclusive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(motif, null);
                Assert.That(_turner.enabled, Is.True);

                typeof(InteractionMotif).GetMethod("ClaimExclusive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(motif, null);
                // A motif disabled mid-grab (its object hidden or destroyed) releases its claim, and with it the hold.
                typeof(InteractionMotif).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(motif, null);
                Assert.That(_turner.enabled, Is.True);
            }
            finally { Object.DestroyImmediate(entity); }
        }
    }
}
#endif
