// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A trigger that just misses an object in Editing lands on the floor and starts a box, and B is the only way out, but nothing said
    /// so. A box in progress shows a "B: undo" tag beside its first point; with no point placed yet the next B drops the box, and the tag
    /// says "B: cancel". It is gone when the box is finished, cancelled or left.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class BoxUndoHintTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private Camera _cam;

        [SetUp]
        public void SetUp()
        {
            var camGo = new GameObject("view", typeof(Camera));
            _spawned.Add(camGo);
            _cam = camGo.GetComponent<Camera>();
            _cam.transform.position = new Vector3(0f, 1.6f, -1.5f);
            AnnotationVisual.ViewCameraOverride = _cam;
            EditInput.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            AnnotationVisual.ViewCameraOverride = null;
            EditInput.ResetForTests();
            foreach (var go in _spawned) if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private BoxAnnotationMotif NewMotif()
        {
            var go = new GameObject("motif");
            _spawned.Add(go);
            var motif = go.AddComponent<BoxAnnotationMotif>();
            for (var type = motif.GetType(); type != null; type = type.BaseType)
            {
                var eye = type.GetField("_centerEyeAnchor", Any | BindingFlags.DeclaredOnly);
                if (eye != null) { eye.SetValue(motif, _cam.transform); break; }
            }
            return motif;
        }

        // Found in the motif's hierarchy, the way a user sees it, not through the motif's own accessor.
        private static BoxUndoHint HintOf(BoxAnnotationMotif motif) => motif.GetComponentInChildren<BoxUndoHint>(true);

        private static void Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, Any).Invoke(target, args);

        private static void SetPointIndex(BoxAnnotationMotif motif, int index) =>
            typeof(BoxAnnotationMotif).GetField("_currentPointIndex", Any).SetValue(motif, index);

        private static void Render(BoxAnnotationMotif motif) => Call(motif, "RenderCurrentState");

        private static void Start(BoxAnnotationMotif motif, Vector3 point)
        {
            motif.BeginCreationFromExternal(point, Vector3.up);
        }

        [Test]
        public void ABoxInProgressShowsBUndoAboveItsFirstPoint()
        {
            var motif = NewMotif();
            Start(motif, new Vector3(0.4f, 0f, 1f));
            Render(motif);

            Assert.That(HintOf(motif), Is.Not.Null);
            Assert.That(HintOf(motif).Visible, Is.True);
            Assert.That(HintOf(motif).Text, Is.EqualTo("B: undo"), "the trigger that started the box placed its first point");
            Assert.That(HintOf(motif).transform.position.x, Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(HintOf(motif).transform.position.y, Is.GreaterThan(0.05f), "above the floor point");
            Assert.That(HintOf(motif).transform.position.z, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void WithNoPointPlacedTheTagSaysCancelBecauseTheNextBDropsTheBox()
        {
            var motif = NewMotif();
            Start(motif, Vector3.forward);
            SetPointIndex(motif, 0);   // B took the first point back
            Render(motif);

            Assert.That(HintOf(motif).Visible, Is.True);
            Assert.That(HintOf(motif).Text, Is.EqualTo("B: cancel"));
        }

        [Test]
        public void ForHandsTheTagPointsAtTheUndoButtonNotAtB()
        {
            EditInput.ControllerPresent = () => false;   // hands only: no B
            EditInput.HandPresent = () => true;
            var motif = NewMotif();
            Start(motif, Vector3.forward);
            Render(motif);
            Assert.That(HintOf(motif).Text, Is.EqualTo("Tap Undo"));

            SetPointIndex(motif, 0);
            Render(motif);
            Assert.That(HintOf(motif).Text, Is.EqualTo("Tap Undo to cancel"));

            EditInput.ControllerPresent = () => true;    // a controller is back: B again
            EditInput.HandPresent = () => false;
            Render(motif);
            Assert.That(HintOf(motif).Text, Is.EqualTo("B: cancel"));
        }

        [Test]
        public void TheWordsFollowTheInputSource()
        {
            Assert.That(BoxUndoHint.LabelFor(1), Is.EqualTo("B: undo"));
            Assert.That(BoxUndoHint.LabelFor(1, EditInputSource.Hand), Is.EqualTo("Tap Undo"));
            Assert.That(BoxUndoHint.LabelFor(0, EditInputSource.Controller), Is.EqualTo("B: cancel"));
            Assert.That(BoxUndoHint.LabelFor(0, EditInputSource.Hand), Is.EqualTo("Tap Undo to cancel"));
        }

        [Test]
        public void TheTagStaysThroughEveryCreationStep()
        {
            var motif = NewMotif();
            Start(motif, Vector3.forward);
            for (int step = 1; step <= 3; step++)
            {
                SetPointIndex(motif, step);
                Render(motif);
                Assert.That(HintOf(motif).Visible, Is.True, "step " + step);
                Assert.That(HintOf(motif).Text, Is.EqualTo("B: undo"), "step " + step);
            }
        }

        [Test]
        public void TheTagTurnsToTheViewerAndGrowsWithTheDistance()
        {
            var motif = NewMotif();
            Start(motif, new Vector3(0f, 0f, 1f));
            Render(motif);
            float near = HintOf(motif).Canvas.transform.lossyScale.x;
            Assert.That(Vector3.Dot(HintOf(motif).transform.forward, Vector3.forward), Is.GreaterThan(0.99f), "a world canvas is read from its back side of forward: the viewer at -z sees its front");

            _cam.transform.position = new Vector3(0f, 1.6f, -6f);
            Render(motif);
            Assert.That(HintOf(motif).Canvas.transform.lossyScale.x, Is.GreaterThan(near), "far away it is larger so it stays readable");
        }

        [Test]
        public void TheTagIsGoneWhenTheBoxIsCancelledOrTheMotifIsDisabled()
        {
            var motif = NewMotif();
            Start(motif, Vector3.forward);
            Render(motif);
            Assert.That(HintOf(motif).Visible, Is.True);

            motif.CancelAnnotation();
            Assert.That(HintOf(motif).Visible, Is.False, "cancelled: no box in progress");

            Start(motif, Vector3.forward);
            Render(motif);
            Assert.That(HintOf(motif).Visible, Is.True);
            motif.enabled = false;
            Call(motif, "OnDisable");
            Assert.That(HintOf(motif).Visible, Is.False, "left (released by a mode change)");
        }

        [Test]
        public void AMotifThatNeverStartedABoxHasNoTag()
        {
            var motif = NewMotif();
            Render(motif);
            Assert.That(HintOf(motif), Is.Null);
        }

        [Test]
        public void TheTagIsBuiltInTheModeChipsStyleAndHasNoOffThemeColours()
        {
            var hint = BoxUndoHint.Create(NewMotif().transform);
            _spawned.Add(hint.gameObject);
            hint.Show(Vector3.zero, 1, _cam.transform);
            var style = SpatialBlend.Interactions.UI.ModeSwitchStyle.Current;
            var label = hint.GetComponentInChildren<TMPro.TMP_Text>(true);
            Assert.That(label.color, Is.EqualTo(style.Text));
            Assert.That(hint.Canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
        }
    }
}
#endif
