// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SpatialBlend;
using UnityEngine;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public class ManagerLifecycleContractTests
    {
        private GameObject _managerObject;
        private SpatialBlendManager _manager;
        private GameObject _entityObject;
        private SbLocalEntity _entity;
        private GameObject _appObject;
        private RecordingApp _app;
        private readonly List<string> _events = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _events.Clear();
            _managerObject = new GameObject("manager-lifecycle-test");
            _manager = _managerObject.AddComponent<SpatialBlendManager>();
            _entityObject = new GameObject("ordinary-whiteboard");
            _entity = _entityObject.AddComponent<SbLocalEntity>();
            _appObject = new GameObject("recording-app");
            _app = _appObject.AddComponent<RecordingApp>();
            Invoke(_manager, "RegisterApp", _app);
            Attach(_entity, 41);
            _entity.SetOnRemoveCallback(() => _events.Add("entity"));
            _app.OnRemoved = _ => _events.Add("app");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_appObject);
            UnityEngine.Object.DestroyImmediate(_entityObject);
            UnityEngine.Object.DestroyImmediate(_managerObject);
        }

        [Test]
        public void OrdinaryReceivedObjectsAreReferentsUnlessTheyHaveExplicitAvatarIdentity()
        {
            var shouldCreateActor = typeof(SpatialBlendManager).GetMethod(
                "ShouldCreateActorForReceivedObject",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(shouldCreateActor, Is.Not.Null);

            foreach (var name in new[] { "whiteboard", "chess-piece", "furniture" })
            {
                var ordinary = new GameObject(name);
                try
                {
                    Assert.That((bool)shouldCreateActor.Invoke(null, new object[] { ordinary }), Is.False);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(ordinary);
                }
            }

            var avatar = new GameObject("avatar") { tag = "Avatar" };
            try
            {
                Assert.That((bool)shouldCreateActor.Invoke(null, new object[] { avatar }), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void QueuedRemovalNotifiesAppBeforeEntityAndOnlyOnce()
        {
            QueueRemoval(41);
            QueueRemoval(41);
            _manager.Update();

            CollectionAssert.AreEqual(new[] { "app", "entity" }, _events);
        }

        [Test]
        public void ReentrantRemovalDoesNotRepeatCallbacks()
        {
            _app.OnRemoved = _ =>
            {
                _events.Add("app");
                QueueRemoval(41);
            };
            QueueRemoval(41);
            _manager.Update();

            CollectionAssert.AreEqual(new[] { "app", "entity" }, _events);
        }

        [Test]
        public void AppReattachOfSameObjectAndIdDoesNotRetireReplacementRegistration()
        {
            _app.OnRemoved = _ =>
            {
                _events.Add("app");
                Invoke(_entity, "Detach");
                Attach(_entity, 41);
            };
            QueueRemoval(41);
            _manager.Update();

            // This is an old duplicate delivered after the first removal drained.
            // The native-id tombstone must protect the manual same-id reattach.
            QueueRemoval(41);
            _manager.Update();

            CollectionAssert.AreEqual(new[] { "app" }, _events);
            Assert.That(_entity.IsAttached(), Is.True);
            Assert.That(_entity.ID, Is.EqualTo(41));
        }

        [Test]
        public void QueuedDuplicateRemovalCannotRetireSameIdReattachment()
        {
            _app.OnRemoved = _ =>
            {
                _events.Add("app");
                Invoke(_entity, "Detach");
                Attach(_entity, 41);
            };
            QueueRemoval(41);
            QueueRemoval(41);
            _manager.Update();

            CollectionAssert.AreEqual(new[] { "app" }, _events);
            Assert.That(_entity.IsAttached(), Is.True);
            Assert.That(_entity.ID, Is.EqualTo(41));
        }

        [Test]
        public void RemovalAfterQueuedAttachDetachesTheEarlierIngressRegistration()
        {
            var addedObject = new GameObject("queued-added");
            var addedEntity = addedObject.AddComponent<SbLocalEntity>();
            try
            {
                addedEntity.SetOnRemoveCallback(() => _events.Add("entity"));
                QueueProductionTask(() => Attach(addedEntity, 99));
                QueueRemoval(99);
                _manager.Update();

                CollectionAssert.AreEqual(new[] { "app", "entity" }, _events);
                Assert.That(addedEntity.IsAttached(), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(addedObject);
            }
        }

        [Test]
        public void StaleQueuedRemovalIsDiscardedAfterManagedTeardown()
        {
            QueueRemoval(41);
            Invoke(_manager, "BeginManagedTeardown");
            _manager.Update();

            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void RestartGenerationRejectsOldAddedWorkAndAcceptsNewQueuedWork()
        {
            long oldGeneration = (long)GetField(_manager, "_lifecycleGeneration");
            IntPtr payload = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(payload, 1);
                Invoke(_manager, "BeginManagedTeardown");
                Invoke(_manager, "BeginManagedLifecycle");
                long newGeneration = (long)GetField(_manager, "_lifecycleGeneration");
                Assert.That(newGeneration, Is.GreaterThan(oldGeneration));

                Invoke(_manager, "OnElementAdded", -1L, 99L, 0L, "object", payload, 1L,
                    oldGeneration);
                Assert.That(GetCount(GetField(_manager, "taskQueue")), Is.EqualTo(0));

                Invoke(_manager, "OnElementAdded", -1L, 100L, 0L, "object", payload, 1L,
                    newGeneration);
                Assert.That(GetCount(GetField(_manager, "taskQueue")), Is.EqualTo(1));
                Invoke(_manager, "BeginManagedTeardown");
            }
            finally
            {
                Marshal.FreeHGlobal(payload);
            }
        }

        [Test]
        public void TeardownDiscardsQueuedAddedTargetsAndRegistrationBeforeNativeWork()
        {
            long generation = (long)GetField(_manager, "_lifecycleGeneration");
            IntPtr payload = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(payload, 1);
                Invoke(_manager, "OnElementAdded", -1L, 99L, 0L, "object", payload, 1L,
                    generation);
                Invoke(_manager, "OnRealizationTargets", -1L, 0L, null, generation);
                _manager.RegisterAnchor(_entity.gameObject, "hand", Vector3.zero);
                Assert.That(GetCount(GetField(_manager, "taskQueue")), Is.GreaterThan(0),
                    "The fixture must queue production work before testing stale teardown.");
                Invoke(_manager, "BeginManagedTeardown");
                _manager.Update();

                Assert.That(_events, Is.Empty);
                Assert.That(((IList)GetField(_manager, "_pendingShares")).Count, Is.EqualTo(0));
                Assert.That(((IDictionary)GetField(_manager, "_v2ActorByEntity")).Count, Is.EqualTo(0));
            }
            finally
            {
                Marshal.FreeHGlobal(payload);
            }
        }

        [Test]
        public void RemovalDropsPendingReconstructionBeforeItCanBeRetried()
        {
            AddPendingShare(41, new byte[] { 1 });
            QueueRemoval(41);
            _manager.Update();

            var pending = (IList)GetField(_manager, "_pendingShares");
            Assert.That(pending.Count, Is.EqualTo(0));
            Assert.DoesNotThrow(() => Invoke(_manager, "ReconstructShare", 41L, new byte[] { 1 }));
        }

        private void QueueRemoval(long entityId)
        {
            long generation = (long)GetField(_manager, "_lifecycleGeneration");
            Invoke(_manager, "QueueElementRemoval", entityId, generation);
        }

        [Test]
        public void DelayedAddedCopyCannotEraseNewerRemovalTombstone()
        {
            long generation = (long)GetField(_manager, "_lifecycleGeneration");
            QueueRemoval(41);
            _manager.Update();
            var tombstones = (IDictionary)GetField(_manager, "_removedElementReceipts");
            long removedAt = (long)tombstones[41L];
            Assert.That((bool)Invoke(_manager, "AcceptAddedReceipt", 41L,
                removedAt - 1, generation), Is.False);
            Assert.That(tombstones.Contains(41L), Is.True);
            Assert.That((bool)Invoke(_manager, "AcceptAddedReceipt", 41L,
                removedAt + 1, generation), Is.True);
            Assert.That(tombstones.Contains(41L), Is.False);
        }

        private void Attach(SbEntity entity, long entityId)
        {
            Invoke(entity, "Attach", _manager, entityId, false);
        }

        private void QueueProductionTask(Action task)
        {
            var method = typeof(SpatialBlendManager).GetMethod("PushMainThreadTask",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Action) }, null);
            Assert.That(method, Is.Not.Null);
            method.Invoke(_manager, new object[] { task });
        }

        private void AddPendingShare(long entityId, byte[] payload)
        {
            var field = typeof(SpatialBlendManager).GetField("_pendingShares",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pending = (IList)field.GetValue(_manager);
            var pendingType = field.FieldType.GetGenericArguments()[0];
            object item = Activator.CreateInstance(pendingType);
            pendingType.GetField("entityId").SetValue(item, entityId);
            pendingType.GetField("payload").SetValue(item, payload);
            pending.Add(item);
        }

        private static object GetField(object instance, string name)
        {
            return instance.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        }

        private static int GetCount(object collection)
        {
            return (int)collection.GetType().GetProperty("Count").GetValue(collection);
        }

        private static object Invoke(object instance, string name, params object[] arguments)
        {
            MethodInfo method = null;
            foreach (var candidate in instance.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (candidate.Name == name && candidate.GetParameters().Length == arguments.Length)
                {
                    method = candidate;
                    break;
                }
            }
            Assert.That(method, Is.Not.Null, $"Missing production seam {name}");
            return method.Invoke(instance, arguments);
        }

        private sealed class RecordingApp : SbApp
        {
            public Action<GameObject> OnRemoved;
            public override void OnAppStart() { }
            public override OnShareResponse OnShare(GameObject gameObject) => OnShareResponse.AddAsNew();
            public override void OnObjectAdded(GameObject gameObject, OnObjectAddedType onObjectAddedType) { }
            public override void OnRemove(GameObject gameObject) => OnRemoved?.Invoke(gameObject);
        }
    }
}
#endif
