// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class ManagerConnectionContractTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private TestManager _manager;

        [SetUp]
        public void SetUp()
        {
            var managerObject = NewObject("connection-manager");
            managerObject.SetActive(false);
            _manager = managerObject.AddComponent<TestManager>();
            SetField(_manager, typeof(SpatialBlendManager), "_connectOnAwake", false);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
            {
                // Explicit retirement also covers never-activated EditMode objects.
                _objects[i].GetComponent<SpatialBlendManager>()?.OnDestroy();
                UnityEngine.Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }

        [TestCase("library")]
        [TestCase("session")]
        [TestCase("space")]
        [TestCase("callbacks")]
        [TestCase("wait")]
        public void FailedSetupNeverPublishesSpaceOrStartsAppsAndReleasesOwnedHandles(string stage)
        {
            var app = NewApp();
            int published = 0;
            Action<SpatialBlendManager> observer = manager => { if (manager == _manager) published++; };
            SpatialBlendManager.OnSpaceCreated += observer;
            try
            {
                _manager.Failure = stage;
                _manager.Host(12345);
                StartApps();
                Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Failed));
                Assert.That(_manager.ConnectionError, Is.Not.Empty);
                Assert.That(_manager.Initialized, Is.False);
                Assert.That(_manager.SpaceCreated, Is.False);
                Assert.That(_manager.ID, Is.EqualTo(-1));
                Assert.That(published, Is.Zero);
                Assert.That(app.Starts, Is.Zero);
                Assert.That(_manager.DestroyedSessions.Count,
                    Is.EqualTo(stage == "library" || stage == "session" ? 0 : 1));
                Assert.That(_manager.UnregisteredSpaces.Count,
                    Is.EqualTo(stage == "callbacks" || stage == "wait" ? 1 : 0));
                Assert.That(_manager.NativeLibraryCompatible, Is.EqualTo(stage != "library"));
            }
            finally { SpatialBlendManager.OnSpaceCreated -= observer; }
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(65536)]
        public void InvalidAdvertisedPortIsRejectedBeforeAnyNativeAllocation(int port)
        {
            _manager.Host(port);
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Failed));
            Assert.That(_manager.CreatedSessions, Is.Empty);
            Assert.That(_manager.HostCalls, Is.Zero);
        }

        [Test]
        public void FailedConnectRetriesWithFreshHandlesAndPublishesOnlySuccessfulEndpoint()
        {
            _manager.Failure = "wait";
            _manager.Connect("first.invalid", 10001);
            long failedGeneration = _manager.ConnectionGeneration;
            long failedSession = _manager.CreatedSessions[0];
            _manager.Failure = null;
            _manager.Connect("  localhost  ", 10002);

            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Connected));
            Assert.That(_manager.Initialized && _manager.SpaceCreated, Is.True);
            Assert.That(_manager.ConnectionGeneration, Is.GreaterThan(failedGeneration));
            Assert.That(_manager.CreatedSessions[1], Is.Not.EqualTo(failedSession));
            CollectionAssert.AreEqual(new[] { failedSession }, _manager.DestroyedSessions);
            Assert.That(_manager.HostIP, Is.EqualTo("localhost"));
            Assert.That(_manager.Port, Is.EqualTo(10002));
            Assert.That(_manager.IsHost(), Is.False);
            Assert.That(_manager.ConnectionError, Is.Empty);
        }

        [Test]
        public void BlankClientAddressDoesNotCallNativeConnect()
        {
            _manager.Connect(" \t", 10001);
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Failed));
            Assert.That(_manager.CreatedSessions, Is.Empty);
            Assert.That(_manager.ConnectCalls, Is.Zero);
        }

        [Test]
        public void AppGateStartsOnceIncludingLateRegistrationAndStopsOncePerSession()
        {
            var first = NewApp();
            StartApps();
            Assert.That(first.Starts, Is.Zero);
            _manager.Host(12345);
            Assert.That(first.Starts, Is.Zero, "Manual/Fusion mode requires its explicit readiness gate.");
            StartApps();
            StartApps();
            var late = NewApp();
            Assert.That(first.Starts, Is.EqualTo(1));
            Assert.That(late.Starts, Is.EqualTo(1));
            _manager.Disconnect();
            _manager.Disconnect();
            Assert.That(first.Stops, Is.EqualTo(1));
            Assert.That(late.Stops, Is.EqualTo(1));
            _manager.Host(12345);
            StartApps();
            Assert.That(first.Starts, Is.EqualTo(2));
            Assert.That(late.Starts, Is.EqualTo(2));
        }

        [Test]
        public void AnAppStartedByAnAlreadyConnectedManagerWaitsForSceneGeometryBeforeItStarts()
        {
            // A manager that is already connected starts the app from its Start (RegisterApp), before any
            // later line of the app's Start could raise the flag.
            _manager.Host(12345);
            StartApps();
            var app = NewObject("scene-app").AddComponent<RecordingApp>();
            app.WaitsForScene = true;
            bool waitingAtStart = false;
            app.StartAction = () => waitingAtStart = _manager.WaitsForLocalSceneGeometry;
            SetField(app, typeof(SbApp), "_spaceManager", _manager);
            app.Start();
            Assert.That(app.Starts, Is.EqualTo(1));
            Assert.That(waitingAtStart, Is.True);
        }

        [Test]
        public void ConnectOnAwakeAppRegistrationCannotBypassFailure()
        {
            SetField(_manager, typeof(SpatialBlendManager), "_connectOnAwake", true);
            _manager.Failure = "wait";
            _manager.Host(12345);
            var app = NewApp();
            Assert.That(app.Starts, Is.Zero);
            _manager.Failure = null;
            _manager.Host(12345);
            Assert.That(app.Starts, Is.EqualTo(1));
            app.Start(); // Registering the same app again is idempotent.
            Assert.That(app.Starts, Is.EqualTo(1));
        }

        [Test]
        public void ConnectingObserverCanCancelBeforeNativeAllocation()
        {
            _manager.ConnectionStateChanged += state =>
            {
                if (state == SpatialBlendConnectionState.Connecting) _manager.Disconnect();
            };
            _manager.Host(12345);
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Disconnected));
            Assert.That(_manager.CreatedSessions, Is.Empty);
            Assert.That(_manager.Initialized, Is.False);
        }

        [Test]
        public void ConnectedObserverDisconnectDoesNotDeliverStaleConnectedOrStartApps()
        {
            var app = NewApp();
            SetField(_manager, typeof(SpatialBlendManager), "_connectOnAwake", true);
            var laterNotifications = new List<SpatialBlendConnectionState>();
            _manager.ConnectionStateChanged += state =>
            {
                if (state == SpatialBlendConnectionState.Connected) _manager.Disconnect();
            };
            _manager.ConnectionStateChanged += laterNotifications.Add;
            _manager.Host(12345);
            CollectionAssert.AreEqual(new[]
            {
                SpatialBlendConnectionState.Connecting, SpatialBlendConnectionState.Disconnected
            }, laterNotifications);
            Assert.That(app.Starts, Is.Zero);
            Assert.That(_manager.DestroyedSessions, Has.Count.EqualTo(1));
        }

        [Test]
        public void AppStartReentrantDisconnectStopsStartedAppAndDoesNotStartLaterApps()
        {
            var first = NewApp();
            var later = NewApp();
            first.StartAction = _manager.Disconnect;
            _manager.Host(12345);
            StartApps();
            Assert.That(first.Starts, Is.EqualTo(1));
            Assert.That(first.Stops, Is.EqualTo(1));
            Assert.That(later.Starts, Is.Zero);
            Assert.That(_manager.Initialized, Is.False);
        }

        [Test]
        public void CallbackUnregisterFailureStillDestroysOwnedSessionAndAllowsRetry()
        {
            _manager.Host(12345);
            _manager.Failure = "unregister";
            _manager.Disconnect();
            Assert.That(_manager.DestroyedSessions, Has.Count.EqualTo(1));
            Assert.That(_manager.ID, Is.EqualTo(-1));
            _manager.Failure = null;
            _manager.Host(12345);
            Assert.That(_manager.Initialized, Is.True);
        }

        [Test]
        public void DestroyIsIdempotentAndCannotBeReusedAsRetry()
        {
            _manager.Host(12345);
            _manager.OnDestroy();
            _manager.OnDestroy();
            _manager.Host(12345);
            Assert.That(_manager.CreatedSessions, Has.Count.EqualTo(1));
            Assert.That(_manager.DestroyedSessions, Has.Count.EqualTo(1));
            Assert.That(_manager.ConnectionState, Is.EqualTo(SpatialBlendConnectionState.Disconnected));
        }

        [Test]
        public void DisconnectOfOneManagerPreservesOtherManagerReadiness()
        {
            var otherObject = NewObject("other-manager");
            otherObject.SetActive(false);
            var other = otherObject.AddComponent<TestManager>();
            SetField(other, typeof(SpatialBlendManager), "_connectOnAwake", false);
            _manager.Host(12345);
            other.Host(12346);
            _manager.Disconnect();
            Assert.That(other.Initialized && other.SpaceCreated, Is.True);
            Assert.That(other.DestroyedSessions, Is.Empty);
            Assert.That(other.UnregisteredSpaces, Is.Empty);
        }

        private GameObject NewObject(string name)
        {
            var value = new GameObject(name);
            _objects.Add(value);
            return value;
        }

        private RecordingApp NewApp()
        {
            var app = NewObject("connection-app").AddComponent<RecordingApp>();
            SetField(app, typeof(SbApp), "_spaceManager", _manager);
            app.Start();
            return app;
        }

        private void StartApps() => typeof(SpatialBlendManager).GetMethod("StartApps",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_manager, null);

        private static void SetField(object target, Type type, string name, object value) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        public sealed class RecordingApp : SbApp
        {
            public int Starts;
            public int Stops;
            public bool WaitsForScene;
            public Action StartAction;
            protected override bool WaitsForLocalSceneGeometry => WaitsForScene;
            public override void OnAppStart() { Starts++; StartAction?.Invoke(); }
            public override void OnAppStop() { Stops++; }
            public override OnShareResponse OnShare(GameObject value) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject value, OnObjectAddedType type) { }
            public override void OnRemove(GameObject value) { }
        }

        public sealed class TestManager : SpatialBlendManager
        {
            private static long _nextHandle = 800000;
            public string Failure;
            public int HostCalls;
            public int ConnectCalls;
            public readonly List<long> CreatedSessions = new List<long>();
            public readonly List<long> DestroyedSessions = new List<long>();
            public readonly List<long> UnregisteredSpaces = new List<long>();

            protected override void InitializeNativeLibrary()
            {
                if (Failure == "library") throw new InvalidOperationException("Test ABI mismatch.");
            }
            protected override long CreateNativeSession()
            {
                if (Failure == "session") return 0;
                long value = ++_nextHandle;
                CreatedSessions.Add(value);
                return value;
            }
            protected override long AddNativeSpace(long session) => Failure == "space" ? 0 : ++_nextHandle;
            protected override void ConfigureNativeSpace(long space, long generation)
            {
                if (Failure == "callbacks") throw new InvalidOperationException("Test callback registration failure.");
            }
            protected override long HostNativeSpace(long space, int port) { HostCalls++; return -1; }
            protected override long ConnectNativeSpace(long space, string host, int port) { ConnectCalls++; return -1; }
            protected override bool WaitForNativeConnection(long handle, out string error)
            {
                error = string.Empty; // Native v2's failure error string can be empty.
                return Failure != "wait";
            }
            protected override void UnregisterNativeSpace(long space)
            {
                UnregisteredSpaces.Add(space);
                if (Failure == "unregister") throw new InvalidOperationException("Test unregister failure.");
            }
            protected override void DestroyNativeSession(long session) => DestroyedSessions.Add(session);
        }
    }
}
#endif
