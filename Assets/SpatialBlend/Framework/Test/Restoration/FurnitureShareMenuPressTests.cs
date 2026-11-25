// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>The furniture menu is hidden until the user presses the furniture with the ray (trigger or pinch); hovering shows nothing.</summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FurnitureShareMenuPressTests
    {
        private GameObject _menuObject, _anchor, _other;
        private FurnitureShareMenu _menu;
        private SceneAnchorRecord _record;
        private EditPointer _pointer;
        private bool _accept, _pinch;
        private Vector3 _origin, _forward;
        private RayInteractable _hovered;
        private Vector3? _hit;
        private GameObject _modeObject, _boxObject;

        [SetUp]
        public void SetUp()
        {
            EditInput.ResetForTests();
            // Another fixture's box may have finished this very frame (edit-mode tests share frames).
            typeof(BoxAnnotationManager).GetField("s_BusyUntilFrame", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, -1);
            typeof(BoxAnnotationManager).GetField("s_BoxActive", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
            _anchor = new GameObject("furniture");
            _anchor.transform.position = new Vector3(0f, 0f, 3f);
            _record = new SceneAnchorRecord { Label = "TABLE", Anchor = _anchor, LocalCenter = Vector3.zero, LocalSize = Vector3.one };
            _menuObject = new GameObject("menu");
            _menu = _menuObject.AddComponent<FurnitureShareMenu>();
            _menu.SetAnchors(new[] { _record });
            Physics.SyncTransforms();
            _origin = Vector3.zero; _forward = Vector3.forward; _hovered = null; _hit = null; _accept = _pinch = false;
        }

        [TearDown]
        public void TearDown()
        {
            EditInput.ResetForTests();
            typeof(InteractionModeManager).GetProperty("Instance").SetValue(null, null);
            RetireMotifs();
            foreach (var go in new[] { _menuObject, _anchor, _other, _modeObject, _boxObject }) if (go != null) Object.DestroyImmediate(go);
        }

        private void UseController()
        {
            _pointer = NewPointer(EditInputSource.Controller);
            EditInput.ControllerPresent = () => true;
            EditInput.HandPresent = () => false;
            EditInput.ControllerAcceptSource = () => _accept;
            EditInput.Register(_pointer);
        }

        private void UseHand()
        {
            _pointer = NewPointer(EditInputSource.Hand);
            _pointer.PinchSource = () => _pinch;
            EditInput.ControllerPresent = () => false;
            EditInput.HandPresent = () => true;
            EditInput.Register(_pointer);
        }

        private EditPointer NewPointer(EditInputSource source) => new EditPointer(source)
        {
            OriginSource = () => _origin, ForwardSource = () => _forward, HoveredSource = () => _hovered, HitSource = () => _hit,
        };

        // One frame as the menu's Update sees it: the press edge from EditInput, then the frame ends.
        private void Frame(float now, Vector3? user = null)
        {
            _menu.Step(EditInput.AcceptDown, EditInput.ActivePointer, now, user ?? Vector3.zero);
            EditInput.EndFrame();
        }

        private void Press(bool controller, float now)
        {
            if (controller) _accept = true; else _pinch = true;
            Frame(now);
        }

        private void Release(bool controller, float now)
        {
            if (controller) _accept = false; else _pinch = false;
            Frame(now);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void HoveringThePointedFurnitureShowsNoMenu(bool controller)
        {
            if (controller) UseController(); else UseHand();
            for (int i = 0; i < 5; ++i) Frame(i * 0.1f);
            Assert.That(_menu.Current, Is.Null, "pointing at the furniture is not enough");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PressingTheFurnitureOpensTheMenuWithEitherRay(bool controller)
        {
            if (controller) UseController(); else UseHand();
            Frame(0f);
            Press(controller, 0.1f);
            Assert.That(_menu.Current, Is.SameAs(_record));
            Release(controller, 0.2f);
            Assert.That(_menu.Current, Is.SameAs(_record), "letting go does not close it");
        }

        [Test]
        public void APressElsewhereClosesIt()
        {
            UseController();
            Press(true, 0f);
            Release(true, 0.1f);
            Assert.That(_menu.Current, Is.Not.Null);
            _forward = Vector3.back;   // away from the furniture
            Press(true, 0.5f);
            Assert.That(_menu.Current, Is.Null);
        }

        [Test]
        public void ItClosesAfterTheTimeoutAndWhenTheUserWalksAway()
        {
            UseController();
            Press(true, 0f);
            Release(true, 0.1f);
            Frame(FurnitureShareMenu.OpenSeconds - 1f);
            Assert.That(_menu.Current, Is.Not.Null, "still open before the timeout");
            Frame(FurnitureShareMenu.OpenSeconds + 1f);
            Assert.That(_menu.Current, Is.Null, "closed by the timeout");

            Press(true, 20f);
            Release(true, 20.1f);
            Assert.That(_menu.Current, Is.Not.Null);
            _menu.Step(false, _pointer, 21f, new Vector3(0f, 0f, 3f + FurnitureShareMenu.MoveAwayMeters + 1f));
            Assert.That(_menu.Current, Is.Null, "closed by walking away");
        }

        [Test]
        public void APressOnTheMenuItselfKeepsItOpenAndRestartsTheTimeout()
        {
            UseController();
            Press(true, 0f);
            Release(true, 0.1f);
            var own = (RayInteractable)typeof(FurnitureShareMenu).GetField("_interactable", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_menu);
            Assert.That(own, Is.Not.Null);
            _hovered = own;
            _forward = Vector3.back;   // the menu hovers wherever the ray is
            Press(true, 8f);
            Assert.That(_menu.Current, Is.SameAs(_record), "a press on the menu is the menu's");
            Release(true, 8.1f);
            Frame(15f);
            Assert.That(_menu.Current, Is.SameAs(_record), "the timeout restarted at the press");
        }

        [Test]
        public void APressWithAnotherInteractableInFrontOfTheFurnitureIsNotTheMenus()
        {
            UseController();
            _other = new GameObject("object");
            _hovered = _other.AddComponent<RayInteractable>();
            _hit = new Vector3(0f, 0f, 1f);   // 1 m away, the furniture is at 2.5 m (its near face)
            Press(true, 0f);
            Assert.That(_menu.Current, Is.Null, "the object's press, not the furniture's");
            Release(true, 0.1f);

            _hit = new Vector3(0f, 0f, 2.5f);   // the furniture's own collider: not in front of it
            Press(true, 0.5f);
            Assert.That(_menu.Current, Is.SameAs(_record));
        }

        // A started box leaves its motif behind (it subscribes to input and mode events); it must not outlive the test.
        private static void RetireMotifs()
        {
            foreach (var motif in Object.FindObjectsByType<SpatialBlend.Interactions.Annotation.BoxAnnotationMotif>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(motif.gameObject);
        }

        private void SetMode(InteractionMode mode)
        {
            if (_modeObject == null)
            {
                _modeObject = new GameObject("mode");
                var manager = _modeObject.AddComponent<InteractionModeManager>();
                typeof(InteractionModeManager).GetProperty("Instance").SetValue(null, manager);
            }
            typeof(InteractionModeManager).GetProperty("CurrentMode").SetValue(InteractionModeManager.Instance, mode);
        }

        private bool PressOnFloorBehindTheFurniture(BoxAnnotationManager boxes)
        {
            typeof(BoxAnnotationManager).GetMethod("OnFloorSelected", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(boxes, new object[] { null, new Vector3(0f, 0f, 6f) });   // the ray goes through the furniture to the floor beyond
            return typeof(BoxAnnotationManager).GetField("_activeMotif", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(boxes) != null;
        }

        [TestCase(InteractionMode.SceneEdit)]
        [TestCase(InteractionMode.Interaction)]
        public void APressOnFurnitureOpensTheMenuAndStartsNoAnnotationInEitherMode(InteractionMode mode)
        {
            UseController();
            SetMode(mode);
            _boxObject = new GameObject("boxes");
            var boxes = _boxObject.AddComponent<BoxAnnotationManager>();
            Assert.That(BoxAnnotationManager.RayFirstHitsFurnitureProbe(new Vector3(0f, 0f, 6f)), Is.True);
            Press(true, 0f);
            Assert.That(_menu.Current, Is.SameAs(_record), "the menu opens in " + mode);
            Assert.That(PressOnFloorBehindTheFurniture(boxes), Is.False, "and the same press starts no annotation");
        }

        [Test]
        public void APressOnOpenFloorInSceneEditStartsAnAnnotationAndNoMenu()
        {
            UseController();
            SetMode(InteractionMode.SceneEdit);
            _forward = Vector3.back;   // away from the furniture: open floor behind the user
            _boxObject = new GameObject("boxes");
            var boxes = _boxObject.AddComponent<BoxAnnotationManager>();
            Press(true, 0f);
            Assert.That(_menu.Current, Is.Null);
            typeof(BoxAnnotationManager).GetMethod("OnFloorSelected", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(boxes, new object[] { null, new Vector3(0f, 0f, -4f) });
            Assert.That(typeof(BoxAnnotationManager).GetField("_activeMotif", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(boxes), Is.Not.Null,
                "the annotation starts as before");
        }

        [Test]
        public void AVirtualObjectInFrontOfTheFurnitureStillWinsInSceneEdit()
        {
            UseController();
            SetMode(InteractionMode.SceneEdit);
            _other = new GameObject("object");
            _hovered = _other.AddComponent<RayInteractable>();
            _hit = new Vector3(0f, 0f, 1f);
            Press(true, 0f);
            Assert.That(_menu.Current, Is.Null, "the object's press, not the menu's");
        }

        [Test]
        public void ACornerPressThroughFurnitureWhileABoxIsDrawnOpensNoMenu()
        {
            UseController();
            SetMode(InteractionMode.SceneEdit);
            _boxObject = new GameObject("boxes");
            var boxes = _boxObject.AddComponent<BoxAnnotationManager>();
            _forward = Vector3.back;   // a box starts on open floor
            typeof(BoxAnnotationManager).GetMethod("OnFloorSelected", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(boxes, new object[] { null, new Vector3(0f, 0f, -4f) });
            Assert.That(BoxAnnotationManager.BoxBusy, Is.True, "a box is being drawn");
            _forward = Vector3.forward;   // the next corner is aimed past the furniture
            Press(true, 0f);
            Assert.That(_menu.Current, Is.Null, "the corner press is the box's");
            Release(true, 0.1f);
            RetireMotifs();
            Object.DestroyImmediate(_boxObject);
            _boxObject = null;
        }

        [Test]
        public void ThePressWhileTheCaptureCardHoldsTheTriggerOpensNoMenu()
        {
            UseController();
            var owner = new object();
            BoxAnnotationManager.HoldCreation(owner);
            try
            {
                Press(true, 0f);
                Assert.That(_menu.Current, Is.Null);
            }
            finally { BoxAnnotationManager.ReleaseCreation(owner); }
            Release(true, 0.1f);
            Press(true, 0.5f);
            Assert.That(_menu.Current, Is.SameAs(_record), "released: the menu works again");
        }

        [Test]
        public void APropInsideTheFurnituresVolumeIsSomethingInFront()
        {
            UseController();
            _other = new GameObject("prop");
            _hovered = _other.AddComponent<RayInteractable>();
            _hit = new Vector3(0f, 0f, 3f);   // the furniture's volume is 2.5 to 3.5 m: a prop at its middle, past the entering surface
            Press(true, 0f);
            Assert.That(_menu.Current, Is.Null, "the prop's press");
        }
    }
}
#endif
