// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FreeformSceneActorDefinitionTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private Scene _scene;
        private bool _opened;
        private readonly List<GameObject> _created = new List<GameObject>();

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
            for (int index = _created.Count - 1; index >= 0; --index)
            {
                if (_created[index] != null)
                    UnityEngine.Object.DestroyImmediate(_created[index]);
            }
            _created.Clear();
            if (_opened && _scene.isLoaded)
                EditorSceneManager.CloseScene(_scene, true);
        }

        [Test]
        public void OriginalSceneSerializesOneExplicitLocalActorChartAndOriginalAvatarPrefab()
        {
            FreeformSceneLocalBackend backend = Find<FreeformSceneLocalBackend>();
            FreeformSceneActorDefinition definition = Find<FreeformSceneActorDefinition>();
            Assert.That(backend, Is.Not.Null);
            Assert.That(definition, Is.Not.Null);
            Assert.That(new SerializedObject(backend).FindProperty("_actors").objectReferenceValue,
                Is.SameAs(definition));

            AssertScene(definition.SourceRoot);
            AssertScene(definition.ReceiverRoot);
            Assert.That(definition.SourceRoot, Is.Not.SameAs(definition.ReceiverRoot));
            Assert.That(definition.SourceActor.gameObject, Is.SameAs(definition.SourceRoot.gameObject));
            Assert.That(definition.ReceiverActor.gameObject, Is.SameAs(definition.ReceiverRoot.gameObject));
            Assert.That(definition.Input.gameObject, Is.SameAs(definition.SourceRoot.gameObject));
            Assert.That(definition.Replay.gameObject, Is.SameAs(definition.SourceRoot.gameObject));
            Assert.That(definition.Replay.AutoAdvanceOnUpdate, Is.False,
                "The backend is the only replay driver; the player must not also advance itself in Update.");
            Assert.That(definition.Targets.gameObject, Is.SameAs(definition.ReceiverRoot.gameObject));
            Assert.That(definition.SourceActor.GetComponent<SpatialBlendObject>(), Is.Null,
                "Native owners are fresh runtime children, not hidden authored actor ownership.");
            Assert.That(definition.ReceiverActor.GetComponent<SpatialBlendObject>(), Is.Null);

            var serialized = new SerializedObject(definition);
            Assert.That(serialized.FindProperty("_actorId").stringValue, Is.Not.Empty);
            Assert.That(serialized.FindProperty("_sourceSpaceId").stringValue,
                Is.Not.EqualTo(serialized.FindProperty("_receiverSpaceId").stringValue));
            Assert.That(serialized.FindProperty("_committedJsonlPath").stringValue, Is.Empty);
            Assert.That(serialized.FindProperty("_committedManifestPath").stringValue, Is.Empty,
                "No recording is downloaded or implicitly selected by the original scene.");
            Assert.That(serialized.FindProperty("_localAvatarPrefab").objectReferenceValue,
                Is.Not.Null);
            Assert.That(EditorUtility.IsPersistent(
                serialized.FindProperty("_localAvatarPrefab").objectReferenceValue), Is.True,
                "The local factory receives the original prefab asset, never a scene avatar or primitive.");

            AssertChart(serialized, "_source", definition.SourceRoot);
            AssertChart(serialized, "_receiver", definition.ReceiverRoot);
            Assert.That(definition.AnchorIds, Is.EqualTo(new[]
            {
                FreeformSceneActorDefinition.RootAnchorId,
                FreeformSceneActorDefinition.HeadAnchorId,
                FreeformSceneActorDefinition.LeftHandAnchorId,
                FreeformSceneActorDefinition.RightHandAnchorId
            }));
            Assert.That(serialized.FindProperty("_sourceSeatContactAnchor"), Is.Not.Null);
            Assert.That(serialized.FindProperty("_receiverSeatContactAnchor"), Is.Not.Null);
        }

        [Test]
        public void OptionalSeatContactPair_ProducesExactSpatialBodyAnchors_AndRejectsInvalidPairs()
        {
            FreeformSceneActorDefinition definition = Find<FreeformSceneActorDefinition>();
            Assert.That(definition, Is.Not.Null);
            FreeformSceneActorAnchorReference previousSource =
                GetField<FreeformSceneActorAnchorReference>(definition, "_sourceSeatContactAnchor");
            FreeformSceneActorAnchorReference previousReceiver =
                GetField<FreeformSceneActorAnchorReference>(definition, "_receiverSeatContactAnchor");
            try
            {
                SetSeatPair(definition, new FreeformSceneActorAnchorReference(),
                            new FreeformSceneActorAnchorReference());
                Assert.That(ValidateForLoadedScene(definition, _scene, out string error), Is.True, error);
                Assert.That(TryCreateSourceAnchors(definition, out SpatialBlendActorAnchor[] blankSource,
                                                   out error), Is.True, error);
                Assert.That(blankSource, Has.Length.EqualTo(4));
                Transform sourceContact = CreateChild(definition.SourceRoot, "source-seat-contact",
                                                      new Vector3(.2f, .4f, .6f));
                Transform receiverContact = CreateChild(definition.ReceiverRoot, "receiver-seat-contact",
                                                        new Vector3(-.3f, .2f, .5f));
                SpatialBlendObject sourceScope = CreateScope(definition.SourceRoot, "source-seat-scope",
                                                           SpatialBlendObjectKind.Affordance);
                SpatialBlendObject receiverScope = CreateScope(definition.ReceiverRoot, "receiver-seat-scope",
                                                             SpatialBlendObjectKind.Affordance);
                FreeformSceneActorAnchorReference source =
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, sourceContact, sourceScope);
                FreeformSceneActorAnchorReference receiver =
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, receiverContact, receiverScope);
                SetSeatPair(definition, source, receiver);

                Assert.That(ValidateForLoadedScene(definition, _scene, out error), Is.True, error);
                Assert.That(TryCreateSourceAnchors(definition, out SpatialBlendActorAnchor[] sourceAnchors,
                                                   out error), Is.True, error);
                Assert.That(TryCreateReceiverAnchors(definition, out SpatialBlendActorAnchor[] receiverAnchors,
                                                     out error), Is.True, error);
                Assert.That(sourceAnchors, Has.Length.EqualTo(5));
                Assert.That(receiverAnchors, Has.Length.EqualTo(5));
                AssertSeatAnchor(sourceAnchors[4], definition.SourceRoot, sourceContact, sourceScope);
                AssertSeatAnchor(receiverAnchors[4], definition.ReceiverRoot, receiverContact, receiverScope);
                Assert.That(definition.AnchorIds, Does.Contain(FreeformSceneActorDefinition.SeatContactAnchorId));

                SetSeatPair(definition, source, null);
                AssertSeatRejected(definition, "one-sided seat reference");

                SetSeatPair(definition,
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, null, null), receiver);
                AssertSeatRejected(definition, "partial seat reference");

                SetSeatPair(definition, SeatReference("pelvis", sourceContact, sourceScope),
                            SeatReference("pelvis", receiverContact, receiverScope));
                AssertSeatRejected(definition, "wrong seat ID");

                Transform unrelated = CreateInScene("unrelated-seat-reference").transform;
                SetSeatPair(definition,
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, unrelated, sourceScope),
                    receiver);
                AssertSeatRejected(definition, "non-child seat reference");

                Scene foreign = EditorSceneManager.NewPreviewScene();
                GameObject foreignObject = null;
                try
                {
                    foreignObject = new GameObject("foreign-seat-support");
                    SceneManager.MoveGameObjectToScene(foreignObject, foreign);
                    SpatialBlendObject foreignScope = foreignObject.AddComponent<SpatialBlendObject>();
                    SetField(foreignScope, "_kind", SpatialBlendObjectKind.Affordance);
                    SetSeatPair(definition,
                        SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, sourceContact,
                                      foreignScope), receiver);
                    AssertSeatRejected(definition, "cross-scene scope");
                }
                finally
                {
                    if (foreignObject != null)
                        UnityEngine.Object.DestroyImmediate(foreignObject);
                    if (foreign.IsValid())
                        EditorSceneManager.ClosePreviewScene(foreign);
                }

                SetSeatPair(definition,
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, sourceContact, null),
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, receiverContact, null));
                AssertSeatRejected(definition, "missing scope");

                SpatialBlendObject nonAffordanceSource = CreateScope(definition.SourceRoot, "source-not-seat",
                                                                   SpatialBlendObjectKind.Object);
                SpatialBlendObject nonAffordanceReceiver = CreateScope(definition.ReceiverRoot, "receiver-not-seat",
                                                                     SpatialBlendObjectKind.Object);
                SetSeatPair(definition,
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, sourceContact,
                                  nonAffordanceSource),
                    SeatReference(FreeformSceneActorDefinition.SeatContactAnchorId, receiverContact,
                                  nonAffordanceReceiver));
                AssertSeatRejected(definition, "non-Affordance scope");

                Assert.That(definition.AnchorIds, Is.EqualTo(new[]
                {
                    FreeformSceneActorDefinition.RootAnchorId,
                    FreeformSceneActorDefinition.HeadAnchorId,
                    FreeformSceneActorDefinition.LeftHandAnchorId,
                    FreeformSceneActorDefinition.RightHandAnchorId
                }));
            }
            finally
            {
                SetSeatPair(definition, previousSource, previousReceiver);
            }
        }

        [Test]
        public void FoundationLocalGateAlsoOwnsLegacyAvatarSpawnerSuppression()
        {
            FreeformCollaborationApp app = Find<FreeformCollaborationApp>();
            FreeformSceneFoundationLocalGate gate = Find<FreeformSceneFoundationLocalGate>();
            Assert.That(app, Is.Not.Null);
            Assert.That(gate, Is.Not.Null);
            var serialized = new SerializedObject(gate);
            var spawner = serialized.FindProperty("_avatarSpawner").objectReferenceValue as SbAvatarSpawnerFusion;
            Assert.That(spawner, Is.Not.Null);
            AssertScene(spawner);
            Assert.That(app.UsesFoundationLocal, Is.True);
            Assert.That(gate.SuppressFoundationLocalTransport(), Is.True);
            Assert.That(gate.FoundationTransportSuppressed, Is.True,
                "The selected local profile suppresses both legacy transport and Fusion avatar spawning.");
        }

        private void AssertChart(SerializedObject definition, string prefix, Transform root)
        {
            string[] names = { "RootAnchor", "HeadAnchor", "LeftHandAnchor", "RightHandAnchor" };
            string[] ids = { "root", "head", "hand_l", "hand_r" };
            for (int index = 0; index < names.Length; ++index)
            {
                SerializedProperty anchor = definition.FindProperty(prefix + names[index]);
                Assert.That(anchor.FindPropertyRelative("_anchorId").stringValue, Is.EqualTo(ids[index]));
                Transform reference = anchor.FindPropertyRelative("_reference").objectReferenceValue as Transform;
                AssertScene(reference);
                Assert.That(reference == root || reference.IsChildOf(root), Is.True);
                Assert.That(anchor.FindPropertyRelative("_support").objectReferenceValue, Is.Null,
                    "The default scene has reference charts only; it does not assert a support/MRUK mapping.");
            }
        }

        private void AssertSeatRejected(FreeformSceneActorDefinition definition, string label)
        {
            Assert.That(ValidateForLoadedScene(definition, _scene, out _), Is.False, label);
            Assert.That(TryCreateSourceAnchors(definition, out _, out _), Is.False, label);
            Assert.That(TryCreateReceiverAnchors(definition, out _, out _), Is.False, label);
        }

        private static bool ValidateForLoadedScene(FreeformSceneActorDefinition definition, Scene scene,
                                                   out string error)
        {
            MethodInfo method = typeof(FreeformSceneActorDefinition).GetMethod("ValidateForLoadedScene",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { scene, null };
            bool result = (bool)method.Invoke(definition, arguments);
            error = arguments[1] as string;
            return result;
        }

        private static bool TryCreateSourceAnchors(FreeformSceneActorDefinition definition,
                                                   out SpatialBlendActorAnchor[] anchors, out string error)
        {
            return TryCreateAnchors(definition, "TryCreateSourceAnchors", out anchors, out error);
        }

        private static bool TryCreateReceiverAnchors(FreeformSceneActorDefinition definition,
                                                     out SpatialBlendActorAnchor[] anchors, out string error)
        {
            return TryCreateAnchors(definition, "TryCreateReceiverAnchors", out anchors, out error);
        }

        private static bool TryCreateAnchors(FreeformSceneActorDefinition definition, string methodName,
                                             out SpatialBlendActorAnchor[] anchors, out string error)
        {
            MethodInfo method = typeof(FreeformSceneActorDefinition).GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { null, null };
            bool result = (bool)method.Invoke(definition, arguments);
            anchors = arguments[0] as SpatialBlendActorAnchor[];
            error = arguments[1] as string;
            return result;
        }

        private void AssertSeatAnchor(SpatialBlendActorAnchor anchor, Transform root,
                                      Transform reference, SpatialBlendObject scope)
        {
            Assert.That(anchor.Name, Is.EqualTo(FreeformSceneActorDefinition.SeatContactAnchorId));
            Assert.That(anchor.Role, Is.EqualTo(SpatialBlendActorAnchorRole.Body));
            Assert.That(anchor.PositionDimension,
                Is.EqualTo(SpatialBlendActorAnchorPositionDimension.Spatial3D));
            Assert.That(anchor.ParentName, Is.EqualTo(FreeformSceneActorDefinition.RootAnchorId));
            Assert.That(anchor.Support, Is.SameAs(scope));
            Vector3 expected = Quaternion.Inverse(root.rotation) * (reference.position - root.position);
            Assert.That(anchor.LocalOffset, Is.EqualTo(expected));
        }

        private FreeformSceneActorAnchorReference SeatReference(string id, Transform reference,
                                                                 SpatialBlendObject scope)
        {
            var value = new FreeformSceneActorAnchorReference();
            SetField(value, "_anchorId", id);
            SetField(value, "_reference", reference);
            SetField(value, "_support", scope);
            return value;
        }

        private void SetSeatPair(FreeformSceneActorDefinition definition,
                                 FreeformSceneActorAnchorReference source,
                                 FreeformSceneActorAnchorReference receiver)
        {
            SetField(definition, "_sourceSeatContactAnchor", source);
            SetField(definition, "_receiverSeatContactAnchor", receiver);
        }

        private SpatialBlendObject CreateScope(Transform root, string name, SpatialBlendObjectKind kind)
        {
            GameObject value = CreateInScene(name);
            value.transform.SetParent(root, false);
            SpatialBlendObject scope = value.AddComponent<SpatialBlendObject>();
            SetField(scope, "_kind", kind);
            return scope;
        }

        private Transform CreateChild(Transform root, string name, Vector3 localPosition)
        {
            GameObject value = CreateInScene(name);
            value.transform.SetParent(root, false);
            value.transform.localPosition = localPosition;
            return value.transform;
        }

        private GameObject CreateInScene(string name)
        {
            var value = new GameObject(name);
            SceneManager.MoveGameObjectToScene(value, _scene);
            _created.Add(value);
            return value;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(target);
        }

        private T Find<T>() where T : Component => _scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).SingleOrDefault();

        private void AssertScene(Component value) => AssertScene(value != null ? value.transform : null);

        private void AssertScene(Transform value)
        {
            Assert.That(value, Is.Not.Null);
            Assert.That(value.gameObject.scene, Is.EqualTo(_scene));
            Assert.That(EditorUtility.IsPersistent(value), Is.False);
        }
    }
}
#endif
