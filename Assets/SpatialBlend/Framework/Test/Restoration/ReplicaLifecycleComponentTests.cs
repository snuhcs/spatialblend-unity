// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class ReplicaLifecycleComponentTests
    {
        private GameObject _managerObject;
        private RecordingReplicaManager _manager;

        [SetUp]
        public void SetUp()
        {
            _managerObject = new GameObject("replica-manager");
            _manager = _managerObject.AddComponent<RecordingReplicaManager>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_managerObject);
        }

        [Test]
        public void ExactTrackedAddAsNewReplica_IsRetiredOnNativeRemoval()
        {
            var replicaObject = new GameObject("reconstructed-native");
            var replica = replicaObject.AddComponent<SbNativeEntity>();
            try
            {
                MarkRemoteReplica(replica);
                Attach(replica, 41);
                Track(replica, 41);

                QueueRemoval(41);
                _manager.Update();

                CollectionAssert.AreEqual(new[] { replicaObject }, _manager.DestroyedReplicas);
                Assert.That(replica.IsAttached(), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replicaObject);
            }
        }

        [Test]
        public void AuthoredAndLinkedLocalObjects_AreNeverReplicaDestructionCandidates()
        {
            var localObject = new GameObject("authored-local");
            var local = localObject.AddComponent<SbLocalEntity>();
            try
            {
                Attach(local, 42);
                QueueRemoval(42);
                _manager.Update();

                Assert.That(_manager.DestroyedReplicas, Is.Empty);
                Assert.That(localObject, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(localObject);
            }
        }

        [Test]
        public void ReattachedReplicaDuringRemoval_IsNotRetiredByOldRegistration()
        {
            var appObject = new GameObject("replica-reentry-app");
            var replicaObject = new GameObject("reconstructed-native-reentry");
            var app = appObject.AddComponent<ReattachingApp>();
            var replica = replicaObject.AddComponent<SbNativeEntity>();
            try
            {
                RegisterApp(app);
                MarkRemoteReplica(replica);
                Attach(replica, 43);
                Track(replica, 43);
                app.OnRemoved = _ =>
                {
                    Invoke(replica, "Detach");
                    Attach(replica, 99);
                };

                QueueRemoval(43);
                _manager.Update();

                Assert.That(replica.IsAttached(), Is.True);
                Assert.That(replica.ID, Is.EqualTo(99));
                Assert.That(_manager.DestroyedReplicas, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replicaObject);
                UnityEngine.Object.DestroyImmediate(appObject);
            }
        }

        [Test]
        public void ReattachedThenDetachedReplica_IsNotDestroyedByOldProvenance()
        {
            var replicaObject = new GameObject("reconstructed-native-version-reentry");
            var replica = replicaObject.AddComponent<SbNativeEntity>();
            try
            {
                MarkRemoteReplica(replica);
                Attach(replica, 46);
                Track(replica, 46);
                long oldToken = (long)Invoke(_manager, "GetEntityRegistrationToken", replica, 46L);
                long generation = (long)GetField(_manager, "_lifecycleGeneration");

                // The same component has a newer attachment incarnation, but is
                // detached by the time stale old-provenance cleanup resumes.
                Invoke(replica, "Detach");
                Attach(replica, 47);
                Invoke(replica, "Detach");
                Invoke(_manager, "RetireOwnedNativeReplica", replica, 46L, oldToken, generation);

                Assert.That(_manager.DestroyedReplicas, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replicaObject);
            }
        }

        [Test]
        public void NewerAddAsNewReplica_SupersedesOnlyTheOlderTrackedReplica()
        {
            var oldObject = new GameObject("old-reconstructed-native");
            var newObject = new GameObject("new-reconstructed-native");
            var oldReplica = oldObject.AddComponent<SbNativeEntity>();
            var newReplica = newObject.AddComponent<SbNativeEntity>();
            try
            {
                MarkRemoteReplica(oldReplica);
                Attach(oldReplica, 44);
                Track(oldReplica, 44);

                MarkRemoteReplica(newReplica);
                Attach(newReplica, 44);
                Track(newReplica, 44);

                CollectionAssert.AreEqual(new[] { oldObject }, _manager.DestroyedReplicas);
                Assert.That(oldReplica.IsAttached(), Is.False);
                Assert.That(newReplica.IsAttached(), Is.True);
                Assert.That(newReplica.ID, Is.EqualTo(44));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(newObject);
                UnityEngine.Object.DestroyImmediate(oldObject);
            }
        }

        [Test]
        public void TrackedReplica_IsRetiredDuringManagedTeardownBeforeReconnect()
        {
            var replicaObject = new GameObject("teardown-reconstructed-native");
            var replica = replicaObject.AddComponent<SbNativeEntity>();
            try
            {
                MarkRemoteReplica(replica);
                Attach(replica, 45);
                Track(replica, 45);

                Invoke(_manager, "BeginManagedTeardown");

                CollectionAssert.AreEqual(new[] { replicaObject }, _manager.DestroyedReplicas);
                Assert.That(replica.IsAttached(), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replicaObject);
            }
        }

        [Test]
        public void RetiredReplicaInvalidatesLateContentEpochAndSuppressesReadiness()
        {
            var replicaObject = new GameObject("late-content-replica");
            var replica = replicaObject.AddComponent<SbNativeEntity>();
            int notifications = 0;
            try
            {
                MarkRemoteReplica(replica);
                byte[] bytes = { 1, 2, 3 };
                replica.SetContentData(bytes);
                long epoch = (long)GetField(replica, "_contentEpoch");
                replica.ContentReady += _ => ++notifications;
                Invoke(replica, "RetireReconstructedReplica");

                Assert.That((bool)Invoke(replica, "IsContentLoadCurrent", epoch, bytes), Is.False);
                replica.SetContentData(new byte[] { 4 });
                Assert.That(notifications, Is.EqualTo(0));
                Assert.That(replica.GetContentData(), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replicaObject);
            }
        }

        private void Attach(SbEntity entity, long id) => Invoke(entity, "Attach", _manager, id, false);
        private void Track(SbNativeEntity entity, long id) =>
            Invoke(_manager, "TrackReconstructedNativeReplica", entity, id);
        private void QueueRemoval(long id)
        {
            long generation = (long)GetField(_manager, "_lifecycleGeneration");
            Invoke(_manager, "QueueElementRemoval", id, generation);
        }
        private void RegisterApp(SbApp app) => Invoke(_manager, "RegisterApp", app);

        private static void MarkRemoteReplica(SbNativeEntity entity) =>
            Invoke(entity, "MarkRemoteReplica");

        private static object GetField(object instance, string name)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(instance);
            }
            Assert.Fail($"Missing production field {name}");
            return null;
        }

        private static object Invoke(object instance, string name, params object[] args)
        {
            Type type = instance.GetType();
            MethodInfo method = null;
            while (type != null && method == null)
            {
                foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (candidate.Name == name && candidate.GetParameters().Length == args.Length)
                    {
                        method = candidate;
                        break;
                    }
                }
                type = type.BaseType;
            }
            Assert.That(method, Is.Not.Null, $"Missing production seam {name}");
            return method.Invoke(instance, args);
        }

        private sealed class RecordingReplicaManager : SpatialBlendManager
        {
            public readonly List<GameObject> DestroyedReplicas = new List<GameObject>();

            protected override void InitializeNativeLibrary() { }

            protected override void DestroyOwnedReconstructedNativeReplica(GameObject replica)
            {
                DestroyedReplicas.Add(replica);
            }
        }

        private sealed class ReattachingApp : SbApp
        {
            public Action<GameObject> OnRemoved;
            public override void OnAppStart() { }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, SbApp.OnObjectAddedType type) { }
            public override void OnRemove(GameObject gameObject) => OnRemoved?.Invoke(gameObject);
        }
    }
}
#endif
