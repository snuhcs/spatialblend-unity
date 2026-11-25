// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Interactions;
using UnityEngine;
using UnityEngine.TestTools;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// As in v1, the floor RayInteractables are live only in
    /// SceneEdit. In Interaction mode a ray at the floor must not hover or select it, so no box starts.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EnvironmentSurfaceModeTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _managerHost;
        private EnvironmentSurfaceManager _manager;
        private GameObject _floor;
        private RayInteractable _floorRay;
        private bool _ignoreFailing;

        [SetUp]
        public void SetUp()
        {
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            Invoke(_mode, "Awake"); // EditMode does not run Awake; it publishes InteractionModeManager.Instance
            _managerHost = new GameObject("surface-manager");
            _manager = _managerHost.AddComponent<EnvironmentSurfaceManager>();
            // Start() is what subscribes the mode event; EditMode does not run it.
            Invoke(_manager, "Start");

            _floor = new GameObject("floor");
            var anchor = _floor.AddComponent<MRUKAnchor>();
            _floorRay = _floor.AddComponent<RayInteractable>();
            var floors = (System.Collections.IDictionary)GetField(_manager, "_floorInteractables");
            floors[anchor] = _floorRay;
        }

        [TearDown]
        public void TearDown()
        {
            if (_floor != null) UnityEngine.Object.DestroyImmediate(_floor);
            // EditMode does not call OnDestroy for these components; the static mode event must not leak.
            if (_managerHost != null) { RunOnDestroyWithoutAnchors(); UnityEngine.Object.DestroyImmediate(_managerHost); }
            if (_modeHost != null) UnityEngine.Object.DestroyImmediate(_modeHost);
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        [Test]
        public void TheFloorIsLiveInSceneEditAndOffInInteractionMode()
        {
            _floorRay.enabled = true;

            _mode.SetMode(InteractionMode.Interaction);
            Assert.That(_floorRay.enabled, Is.False, "Interaction mode turns the floor rays off");

            _mode.SetMode(InteractionMode.SceneEdit);
            Assert.That(_floorRay.enabled, Is.True, "SceneEdit turns them back on");
        }

        [Test]
        public void TheFloorFollowsEveryLaterModeChange()
        {
            for (int round = 0; round < 3; round++)
            {
                _mode.SetMode(InteractionMode.Interaction);
                Assert.That(_floorRay.enabled, Is.False, "round " + round);
                _mode.SetMode(InteractionMode.SceneEdit);
                Assert.That(_floorRay.enabled, Is.True, "round " + round);
            }
        }

        [Test]
        public void ADestroyedManagerNoLongerListensToTheModeEvent()
        {
            RunOnDestroyWithoutAnchors();
            UnityEngine.Object.DestroyImmediate(_managerHost);
            _managerHost = null;
            // Registered again after the teardown: only the event subscription could still reach it.
            var anchor = _floor.GetComponent<MRUKAnchor>();
            ((System.Collections.IDictionary)GetField(_manager, "_floorInteractables"))[anchor] = _floorRay;
            _floorRay.enabled = true;

            _mode.SetMode(InteractionMode.Interaction);

            Assert.That(_floorRay.enabled, Is.True, "the unsubscribed manager does not touch the floor");
        }

        [Test]
        public void TheFloorCanBeSwitchedDirectly()
        {
            _manager.SetFloorInteractionEnabled(false);
            Assert.That(_floorRay.enabled, Is.False);
            _manager.SetFloorInteractionEnabled(true);
            Assert.That(_floorRay.enabled, Is.True);
        }

        private void RunOnDestroyWithoutAnchors()
        {
            // OnDestroy also Destroy()s the ISDK components it added, which EditMode refuses.
            ((System.Collections.IDictionary)GetField(_manager, "_floorInteractables")).Clear();
            Invoke(_manager, "OnDestroy");
        }

        private static object GetField(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            throw new MissingFieldException(name);
        }

        private static void Invoke(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(name);
            method.Invoke(target, null);
        }
    }
}
#endif
