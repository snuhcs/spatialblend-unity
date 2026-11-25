// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Interactions;
using SpatialBlend.Interactions.Annotation;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The v1 "point selection release problem". A box motif that is mid-creation when the user
    /// switches to Interaction mode must cancel, tell its manager, and give up its exclusive claim, so the
    /// manager does not keep a dead motif that blocks every later box. An idle motif the manager still holds
    /// must not block a new box either.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class BoxAnnotationPointSelectionReleaseTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _motifHost;
        private BoxAnnotationMotif _motif;

        [SetUp]
        public void SetUp()
        {
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            // EditMode does not run Awake, and Awake is what publishes InteractionModeManager.Instance.
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_mode, null);
            _motifHost = new GameObject("box-motif");
            _motif = _motifHost.AddComponent<BoxAnnotationMotif>();
            Assert.That(InteractionModeManager.Instance, Is.SameAs(_mode));
            Assert.That(InteractionModeManager.IsMode(InteractionMode.SceneEdit), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            if (_motifHost != null) UnityEngine.Object.DestroyImmediate(_motifHost);
            if (_modeHost != null) UnityEngine.Object.DestroyImmediate(_modeHost);
        }

        [Test]
        public void SwitchingToInteractionModeMidCreationCancelsTheBoxAndReleasesTheClaim()
        {
            _motif.BeginCreationFromExternal(new Vector3(1f, 0f, 2f), Vector3.up);
            Assert.That(_motif.IsIdleState, Is.False, "creation started");
            Assert.That(IsActiveByMe(_motif), Is.True, "the motif holds the exclusive claim");
            var completions = new System.Collections.Generic.List<bool>();
            _motif.OnAnnotationComplete += (_, success) => completions.Add(success);

            Assert.That(_motif.ReleaseIfModeBlocked(), Is.False, "SceneEdit keeps the box in progress");
            Assert.That(_motif.IsIdleState, Is.False);

            _mode.SetMode(InteractionMode.Interaction);
            Assert.That(_motif.ReleaseIfModeBlocked(), Is.True);

            Assert.That(_motif.IsIdleState, Is.True, "the box is cancelled");
            Assert.That(IsActiveByMe(_motif), Is.False, "the claim is released");
            Assert.That(completions, Is.EqualTo(new[] { false }),
                "the manager is told exactly once that the annotation failed, so it can drop the motif");
        }

        [Test]
        public void ABlockedIdleMotifDoesNotRepeatTheReleaseEveryFrame()
        {
            _mode.SetMode(InteractionMode.Interaction);
            int completions = 0;
            _motif.OnAnnotationComplete += (_, __) => completions++;

            for (int frame = 0; frame < 5; frame++) Assert.That(_motif.ReleaseIfModeBlocked(), Is.True);

            Assert.That(completions, Is.Zero, "nothing was active, so nothing is cancelled or announced");
        }

        [Test]
        public void UndoingTheFirstPointCancelsAndNotifiesInsteadOfSilentlyResetting()
        {
            _motif.BeginCreationFromExternal(Vector3.zero, Vector3.up);
            var completions = new System.Collections.Generic.List<bool>();
            _motif.OnAnnotationComplete += (_, success) => completions.Add(success);

            // BeginCreationFromExternal locked point 0 already, so the first undo steps back to it.
            Invoke(_motif, "UndoLastPoint");
            Assert.That(_motif.IsIdleState, Is.False, "stepping back from point 1 is not a cancel");
            Assert.That(completions, Is.Empty);
            Invoke(_motif, "UndoLastPoint");

            Assert.That(_motif.IsIdleState, Is.True);
            Assert.That(completions, Is.EqualTo(new[] { false }), "the manager hears about the undo");
        }

        [Test]
        public void AnIdleMotifTheManagerStillHoldsDoesNotBlockANewBox()
        {
            var managerHost = new GameObject("box-manager");
            bool ignore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // Destroy() of the stale motif logs in edit mode
            try
            {
                var manager = managerHost.AddComponent<BoxAnnotationManager>();
                var stale = new GameObject("stale").AddComponent<BoxAnnotationMotif>();
                Assert.That(stale.IsIdleState, Is.True);
                SetField(manager, "_activeMotif", stale);

                Invoke(manager, "OnFloorSelected", null, new Vector3(0f, 0f, 3f));

                var active = (BoxAnnotationMotif)GetField(manager, "_activeMotif");
                Assert.That(active, Is.Not.Null);
                Assert.That(active, Is.Not.SameAs(stale), "the stale idle motif was cleaned up and replaced");
                Assert.That(active.IsIdleState, Is.False, "the new box started");
                UnityEngine.Object.DestroyImmediate(active.gameObject);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = ignore;
                UnityEngine.Object.DestroyImmediate(managerHost);
            }
        }

        [Test]
        public void AMotifThatIsBusyStillBlocksAnotherBox()
        {
            var managerHost = new GameObject("box-manager");
            try
            {
                var manager = managerHost.AddComponent<BoxAnnotationManager>();
                _motif.BeginCreationFromExternal(Vector3.zero, Vector3.up);
                SetField(manager, "_activeMotif", _motif);

                Invoke(manager, "OnFloorSelected", null, new Vector3(5f, 0f, 5f));

                Assert.That(GetField(manager, "_activeMotif"), Is.SameAs(_motif), "a box in progress is not replaced");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(managerHost);
            }
        }

        private static bool IsActiveByMe(object motif) =>
            (bool)typeof(InteractionMotif).GetProperty("IsActiveByMe", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(motif);

        private static FieldInfo FindField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(name);
        }

        private static object GetField(object target, string name) => FindField(target.GetType(), name).GetValue(target);

        private static void SetField(object target, string name, object value) =>
            FindField(target.GetType(), name).SetValue(target, value);

        private static void Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(name);
            method.Invoke(target, args);
        }
    }
}
#endif
