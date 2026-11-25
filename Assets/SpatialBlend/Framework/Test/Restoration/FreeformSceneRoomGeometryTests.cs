// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using SpatialBlend.Samples.FreeformCollaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FreeformSceneRoomGeometryTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private Scene _scene;
        private bool _opened;

        [SetUp]
        public void OpenScene()
        {
            _scene = SceneManager.GetSceneByPath(ScenePath);
            _opened = !_scene.isLoaded;
            if (_opened)
                _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        [TearDown]
        public void CloseScene()
        {
            if (_opened && _scene.isLoaded)
                EditorSceneManager.CloseScene(_scene, true);
        }

        [Test]
        public void OriginalSceneDeclaresItsConfiguredMrukSourceAndPhysicalReceiverSpace()
        {
            FreeformSceneLocalBackend backend = Find<FreeformSceneLocalBackend>();
            FreeformSceneRoomGeometryDefinition definition = Find<FreeformSceneRoomGeometryDefinition>();
            FreeformSceneActorDefinition actors = Find<FreeformSceneActorDefinition>();
            Assert.That(backend, Is.Not.Null);
            Assert.That(definition, Is.Not.Null);
            Assert.That(actors, Is.Not.Null);
            Assert.That(new SerializedObject(backend).FindProperty("_roomGeometry").objectReferenceValue,
                Is.SameAs(definition));
            Assert.That(definition.Mruk, Is.Not.Null);
            Assert.That(definition.Mruk.gameObject.name, Is.EqualTo("[BuildingBlock] MR Utility Kit"));
            Assert.That(definition.Mruk.gameObject.scene, Is.EqualTo(_scene));
            Assert.That(EditorUtility.IsPersistent(definition.Mruk), Is.False,
                "FoundationLocal must bind the declared scene component, never a prefab asset or ambient singleton.");
            Assert.That(definition.Mruk.SceneSettings, Is.Not.Null);
            Assert.That(definition.Mruk.SceneSettings.DataSource,
                Is.EqualTo(MRUK.SceneDataSource.DeviceWithPrefabFallback));
            Assert.That(definition.Mruk.SceneSettings.LoadSceneOnStartup, Is.True,
                "The configured MRUK source may load its declared device/prefab path; a room is still optional.");
            Assert.That(definition.ScopeId, Is.EqualTo("freeform-scene-local-room"));
            Assert.That(new SerializedObject(backend).FindProperty("_spaceId").stringValue,
                Is.EqualTo(actors.ReceiverSpaceId));
            Assert.That(actors.SourceSpaceId, Is.Not.EqualTo(actors.ReceiverSpaceId),
                "Recorded source motion remains distinct from the physical receiver room space.");
        }

        [Test]
        public void WatcherSwitchMutationFailedDeleteRetryAndStaleCallbackAreExact()
        {
            var roomAGo = new GameObject("room-a");
            var roomBGo = new GameObject("room-b");
            var anchorGo = new GameObject("observed-anchor");
            roomAGo.SetActive(false);
            roomBGo.SetActive(false);
            anchorGo.SetActive(false);
            MRUKRoom roomA = roomAGo.AddComponent<MRUKRoom>();
            MRUKRoom roomB = roomBGo.AddComponent<MRUKRoom>();
            MRUKAnchor anchor = anchorGo.AddComponent<MRUKAnchor>();
            try
            {
                ConfigureObservedAnchor(anchor);
                bool live = true;
                var sourceSink = new RecordingSink();
                var receiverSink = new RecordingSink();
                var watcher = new MRUKSceneGeometryWatcher();
                Assert.That(watcher.Bind(roomA, sourceSink, () => live), Is.True);
                roomA.AnchorCreatedEvent.Invoke(anchor);
                roomA.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(sourceSink.Upserts, Has.Count.EqualTo(2));

                Assert.That(watcher.Bind(roomB, receiverSink, () => live), Is.True);
                CollectionAssert.AreEqual(new[] { MRUKSceneGeometryAdapter.StableKey(anchor) }, sourceSink.Removed,
                    "A room switch removes exactly the first room's owned descriptor.");
                roomA.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(sourceSink.Upserts, Has.Count.EqualTo(2),
                    "A detached room callback must not mutate the replacement binding.");

                roomB.AnchorCreatedEvent.Invoke(anchor);
                Assert.That(receiverSink.Upserts, Has.Count.EqualTo(1));
                receiverSink.FailRemove = true;
                roomB.AnchorRemovedEvent.Invoke(anchor);
                Assert.That(watcher.RetryPendingRemovals(), Is.False);
                receiverSink.FailRemove = false;
                Assert.That(watcher.RetryPendingRemovals(), Is.True,
                    "The watcher retains and retries this exact failed deletion without changing its room binding.");

                live = false;
                roomB.AnchorUpdatedEvent.Invoke(anchor);
                Assert.That(receiverSink.Upserts, Has.Count.EqualTo(1),
                    "A stale lifetime callback cannot write into a newer scope.");
                Assert.That(watcher.Dispose(), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(anchorGo);
                Object.DestroyImmediate(roomBGo);
                Object.DestroyImmediate(roomAGo);
            }
        }

        private T Find<T>() where T : Component => _scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).SingleOrDefault();

        private static void ConfigureObservedAnchor(MRUKAnchor anchor)
        {
            Set(anchor, "Label", MRUKAnchor.SceneLabels.OTHER);
            Set(anchor, "VolumeBounds", (Bounds?)new Bounds(Vector3.zero, Vector3.one));
            // MRUK puts every anchor in a room before it raises its events; a UUID-less anchor is not staged before that.
            if (s_anchorRoom == null)
            {
                var go = new GameObject("anchor-room");
                go.SetActive(false);
                go.hideFlags = HideFlags.HideAndDontSave;
                s_anchorRoom = go.AddComponent<MRUKRoom>();
            }
            Set(anchor, "Room", s_anchorRoom);
        }

        private static MRUKRoom s_anchorRoom;

        private static void Set(MRUKAnchor anchor, string property, object value) =>
            typeof(MRUKAnchor).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)
                .SetValue(anchor, value);

        private sealed class RecordingSink : IMutableLocalSceneGeometrySink
        {
            public readonly List<SceneGeometryDescriptor> Upserts = new List<SceneGeometryDescriptor>();
            public readonly List<int> Removed = new List<int>();
            public bool FailRemove;

            public bool RegisterLocalSceneGeometry(SceneGeometryDescriptor descriptor) =>
                UpsertLocalSceneGeometry(descriptor);

            public bool UpsertLocalSceneGeometry(SceneGeometryDescriptor descriptor)
            {
                Upserts.Add(descriptor);
                return true;
            }

            public bool RemoveLocalSceneGeometry(int sourceKey)
            {
                Removed.Add(sourceKey);
                return !FailRemove;
            }
        }
    }
}
#endif
