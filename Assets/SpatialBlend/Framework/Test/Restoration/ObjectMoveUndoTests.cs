// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using UnityEngine;
using UnityEngine.TestTools;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// B in Editing puts the last moved object back where it was before that grab. One level; never while a box or region is being
    /// drawn (B is their own undo), with an object menu open, during a grab, or in Interaction mode.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ObjectMoveUndoTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _entity;
        private ObjectPlacementMotif _motif;
        private GameObject _other;
        private bool _ignoreFailing;
        private readonly System.Collections.Generic.List<GameObject> _parked = new System.Collections.Generic.List<GameObject>();

        private static readonly Vector3 Start = new Vector3(0.5f, 0.8f, 2f);
        private static readonly Quaternion StartRotation = Quaternion.Euler(0f, 40f, 0f);

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            ObjectMoveHistory.Clear();
            ParkLeftovers();
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            // A mode manager another fixture left alive would make Awake keep that one (and its mode); this test's manager must be the one.
            typeof(InteractionModeManager).GetProperty("Instance").GetSetMethod(true).Invoke(null, new object[] { null });
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_mode, null);
            Assert.That(InteractionModeManager.Instance, Is.SameAs(_mode));
            _mode.SetMode(InteractionMode.SceneEdit);
            _entity = new GameObject("entity");
            _entity.transform.SetPositionAndRotation(Start, StartRotation);
            _motif = _entity.AddComponent<ObjectPlacementMotif>();
            typeof(PlacementMotif).GetField("_entityTransform", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_motif, _entity.transform);
        }

        [TearDown]
        public void TearDown()
        {
            ObjectMoveHistory.Clear();
            LocomotionHold.ResetStatics();
            if (_entity != null) Object.DestroyImmediate(_entity);
            if (_other != null) Object.DestroyImmediate(_other);
            Object.DestroyImmediate(_modeHost);
            foreach (GameObject go in _parked) if (go != null) go.SetActive(true);
            _parked.Clear();
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        // The undo gate scans the scene for tools at work. Objects another fixture left behind in the shared EditMode scene (a box motif, a
        // menu context, a claimed motif) would make every "can undo" case here fail, so they are set inactive for this test (the scan only
        // sees active objects) and restored after it. Their names are written to the test output.
        private void ParkLeftovers()
        {
            var leftovers = new System.Collections.Generic.HashSet<GameObject>();
            foreach (var c in Object.FindObjectsByType<BoxAnnotationMotif>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in Object.FindObjectsByType<FloorVertexCollectionMotif>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in Object.FindObjectsByType<ManipulationContext>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in Object.FindObjectsByType<SpatialBlend.Samples.Scanning.PassthroughPanelManager>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in Object.FindObjectsByType<GltfObjectControlPanelMotif>(FindObjectsSortMode.None)) leftovers.Add(c.gameObject);
            foreach (var c in Object.FindObjectsByType<InteractionMotif>(FindObjectsSortMode.None)) if (c.IsClaimed) leftovers.Add(c.gameObject);
            foreach (GameObject go in leftovers)
            {
                TestContext.WriteLine("[ObjectMoveUndoTests] parked a leftover from another fixture: " + go.name);
                go.SetActive(false);
                _parked.Add(go);
            }
        }

        // A grab as the select handler does it: claim (which records), move, release.
        private void GrabMoveRelease(Vector3 to, Quaternion rotation)
        {
            Invoke(typeof(InteractionMotif), "ClaimExclusive");
            _entity.transform.SetPositionAndRotation(to, rotation);
            Invoke(typeof(InteractionMotif), "ReleaseExclusive");
        }

        [Test]
        public void UndoPutsTheObjectBackWhereItWasBeforeTheGrab()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), Quaternion.Euler(0f, 120f, 0f));
            Assert.That(ObjectMoveHistory.Last, Is.SameAs(_motif));
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.True);

            Assert.That(ObjectMoveHistory.TryUndo(), Is.True);

            Assert.That(Vector3.Distance(_entity.transform.position, Start), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(_entity.transform.rotation, StartRotation), Is.LessThan(0.01f));
        }

        [Test]
        public void ThereIsOneLevelOnly()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            Assert.That(ObjectMoveHistory.TryUndo(), Is.True);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False);
            Assert.That(ObjectMoveHistory.TryUndo(), Is.False, "a second B does nothing until the next grab");
        }

        [Test]
        public void TheLatestGrabReplacesTheRecordedOne()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            GrabMoveRelease(new Vector3(3f, 1f, 3f), StartRotation);
            ObjectMoveHistory.TryUndo();
            Assert.That(Vector3.Distance(_entity.transform.position, new Vector3(2f, 1f, 3f)), Is.LessThan(1e-5f), "undo goes back one grab");
        }

        [Test]
        public void NotInInteractionMode()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            _mode.SetMode(InteractionMode.Interaction);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False);
        }

        [Test]
        public void NotDuringAGrab()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            Invoke(typeof(InteractionMotif), "ClaimExclusive");
            Assert.That(_motif.IsHeld, Is.True);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False);
        }

        [Test]
        public void NotWhileAnotherObjectOrPanelIsBeingDragged()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            _other = new GameObject("panel");
            var panel = _other.AddComponent<PanelPlacementMotif>();
            typeof(InteractionMotif).GetMethod("ClaimExclusive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False);
            typeof(InteractionMotif).GetMethod("ReleaseExclusive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.True);
        }

        [Test]
        public void NotWhileARegionIsBeingCollectedEvenOnTheFrameBCancelledIt()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            _other = new GameObject("region");
            var region = _other.AddComponent<AffordanceAnnotationMotif>();
            // B removed its last point this frame: it went idle now, and is destroyed only at the end of the frame.
            SetRegionMode(region, "Create");
            SetRegionMode(region, "Idle");
            Assert.That(region.IdledFrame, Is.EqualTo(Time.frameCount));
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False, "the same B press must not also undo a move");
        }

        [Test]
        public void ARegionMotifLeftIdleInTheSceneDoesNotBlockUndoForGood()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            _other = new GameObject("region");
            var region = _other.AddComponent<AffordanceAnnotationMotif>();
            // Released by a mode change before its first point (ForceRelease from WaitingForStart), on an earlier frame; the context keeps it.
            SetRegionMode(region, "WaitingForStart");
            SetRegionMode(region, "Idle");
            typeof(FloorVertexCollectionMotif).GetProperty("IdledFrame").GetSetMethod(true).Invoke(region, new object[] { Time.frameCount - 1 });
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.True);
            SetRegionMode(region, "Create");
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False, "collecting points again, B is the region's");
        }

        private static void SetRegionMode(FloorVertexCollectionMotif region, string mode)
        {
            PropertyInfo property = typeof(FloorVertexCollectionMotif).GetProperty("CurrentMode", BindingFlags.Instance | BindingFlags.NonPublic);
            property.SetValue(region, System.Enum.Parse(property.PropertyType, mode));
        }

        [Test]
        public void APressWithoutAMoveDoesNotReplaceTheMoveToUndo()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            Invoke(typeof(InteractionMotif), "ClaimExclusive");
            _entity.transform.position += new Vector3(0.004f, 0f, 0f); // a press with a tremor, under 1 cm
            Invoke(typeof(InteractionMotif), "ReleaseExclusive");
            ObjectMoveHistory.TryUndo();
            Assert.That(Vector3.Distance(_entity.transform.position, Start), Is.LessThan(1e-5f), "B still undoes the real move");
        }

        [Test]
        public void ARecenterForgetsTheMove()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            var eye = new GameObject("eye");
            try
            {
                typeof(PlacementMotif).GetField("_centerEyeAnchor", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_motif, eye.transform);
                typeof(PlacementMotif).GetField("_clampToWalkable", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_motif, false);
                _motif.PlaceInFrontOfUser();
                Assert.That(ObjectMoveHistory.Last, Is.Null, "undoing the grab would also undo the recenter");
            }
            finally { Object.DestroyImmediate(eye); }
        }

        [Test]
        public void NotWhileABoxToolOwnsB()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            _other = new GameObject("box");
            _other.AddComponent<BoxAnnotationMotif>();
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False, "B is the box's own undo");
        }

        [Test]
        public void NotWithAnObjectMenuOpen()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            _other = new GameObject("menu-target");
            var context = _other.AddComponent<ManipulationContext>();
            typeof(ManipulationContext).GetField("_isPanelShowing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, true);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False);
            typeof(ManipulationContext).GetField("_isPanelShowing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, false);
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.True);
        }

        [Test]
        public void ADestroyedObjectIsIgnored()
        {
            GrabMoveRelease(new Vector3(2f, 1f, 3f), StartRotation);
            Object.DestroyImmediate(_entity);
            _entity = null;
            Assert.That(ObjectMoveHistory.CanUndoNow(), Is.False);
            Assert.That(ObjectMoveHistory.TryUndo(), Is.False);
        }

        [Test]
        public void AGrabThatCatchesASettleUndoesToWhereItWasSettling()
        {
            var target = new Pose(new Vector3(1f, 0.5f, 2f), Quaternion.Euler(0f, 180f, 0f));
            var caught = new Pose(new Vector3(1.4f, 0.9f, 2.2f), Quaternion.Euler(0f, 150f, 0f));
            Assert.That(ObjectPlacementMotif.PoseBeforeGrab(true, target, caught).position, Is.EqualTo(target.position),
                "the object was on its way there: that is where it was put");
            Assert.That(ObjectPlacementMotif.PoseBeforeGrab(false, target, caught).position, Is.EqualTo(caught.position),
                "a settled object is undone to where it stood");
            Assert.That(ObjectPlacementMotif.PoseBeforeGrab(true, null, caught).position, Is.EqualTo(caught.position));
        }

        private void Invoke(System.Type owner, string method) =>
            owner.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_motif, null);
    }
}
#endif
