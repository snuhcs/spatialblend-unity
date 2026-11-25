// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
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
    /// As in v1, outlines show only in SceneEdit and follow the <c>_visualizeAffordanceVolumes</c> switch on
    /// InteractionModeManager (shipped on). A declared region's
    /// outline appears when the ray hovers it in SceneEdit only; with the switch off no outline is built at all.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class DeclaredRegionOutlineModeTests
    {
        private static readonly Vector2[] Square =
        {
            new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f),
        };

        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _region;
        private DeclaredRegionVisualizer _visualizer;
        private RayInteractable _ray;
        private bool _ignoreFailing;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // Initialize logs a line per outline
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_mode, null);
            Assert.That(InteractionModeManager.Instance, Is.SameAs(_mode));

            _region = new GameObject("region");
            _visualizer = _region.AddComponent<DeclaredRegionVisualizer>();
            _ray = _region.AddComponent<RayInteractable>();
            SetField(_visualizer, "_rayInteractable", _ray);
            SetField(_visualizer, "_showOnHoverOnly", false); // no fade coroutines in EditMode
        }

        [TearDown]
        public void TearDown()
        {
            if (_region != null) UnityEngine.Object.DestroyImmediate(_region);
            if (_modeHost != null) UnityEngine.Object.DestroyImmediate(_modeHost);
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        [Test]
        public void TheSwitchIsOnByDefault()
        {
            Assert.That(_mode._visualizeAffordanceVolumes, Is.True);
            Assert.That(InteractionModeManager.VisualizeAffordanceVolumes, Is.True);
        }

        [Test]
        public void HoveringARegionInSceneEditMarksItHovered()
        {
            Invoke(_visualizer, "OnInteractorHover", null, _ray);
            Assert.That(GetField(_visualizer, "_isHovered"), Is.EqualTo(true));
        }

        [Test]
        public void HoveringARegionInInteractionModeDoesNothing()
        {
            _mode.SetMode(InteractionMode.Interaction);

            Invoke(_visualizer, "OnInteractorHover", null, _ray);

            Assert.That(GetField(_visualizer, "_isHovered"), Is.EqualTo(false), "no outline in the user-study view");
        }

        [Test]
        public void AnotherInteractablesHoverIsStillIgnored()
        {
            var other = new GameObject("other").AddComponent<RayInteractable>();
            try
            {
                Invoke(_visualizer, "OnInteractorHover", null, other);
                Assert.That(GetField(_visualizer, "_isHovered"), Is.EqualTo(false));
            }
            finally { UnityEngine.Object.DestroyImmediate(other.gameObject); }
        }

        [Test]
        public void WithTheSwitchOffNoOutlineIsBuiltAndNothingIsHovered()
        {
            _mode._visualizeAffordanceVolumes = false;
            var authoring = _region.GetComponent<SbSpatialAuthoring>();
            authoring.declaredExtent = Square;

            _visualizer.Initialize(authoring, Color.cyan, 0.02f);
            Invoke(_visualizer, "OnInteractorHover", null, _ray);

            Assert.That(GetField(_visualizer, "_visual"), Is.Null, "no graphics are built with the switch off");
            Assert.That(GetField(_visualizer, "_isHovered"), Is.EqualTo(false));
            Assert.That(((System.Collections.ICollection)GetField(_visualizer, "_localVertices")).Count, Is.EqualTo(4),
                "the vertices are still read so the data stays current");
        }

        [Test]
        public void WithTheSwitchOnInitializeBuildsTheOutline()
        {
            var authoring = _region.GetComponent<SbSpatialAuthoring>();
            authoring.declaredExtent = Square;

            _visualizer.Initialize(authoring, Color.cyan, 0.02f);

            Assert.That(GetField(_visualizer, "_visual"), Is.Not.Null);
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            throw new MissingFieldException(name);
        }

        private static object GetField(object target, string name) => FindField(target.GetType(), name).GetValue(target);
        private static void SetField(object target, string name, object value) => FindField(target.GetType(), name).SetValue(target, value);

        private static void Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(name);
            method.Invoke(target, args);
        }
    }
}
#endif
