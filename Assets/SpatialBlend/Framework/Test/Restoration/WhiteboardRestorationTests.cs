// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using WhiteboardInteractionMode = SpatialBlend.Samples.Objects.Whiteboard.InteractionMode;

namespace SpatialBlend.Tests
{
    [Category("FreeformRestoration")]
    public class WhiteboardRestorationTests
    {
        private GameObject _root;
        private PointerHandlerMotif _pointer;
        private BoxCollider _ownedCollider;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("whiteboard-test");
            _root.layer = 7;
            _ownedCollider = _root.AddComponent<BoxCollider>();
            _ownedCollider.center = new Vector3(0, 0, 0.8f);
            _ownedCollider.size = new Vector3(2, 2, 0.1f);
            _pointer = _root.AddComponent<PointerHandlerMotif>();
            SetField("targetColliders", new Collider[] { _ownedCollider });
            SetField("targetLayer", (LayerMask)(1 << 7));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
        }

        [Test]
        public void OwnedLayerSevenHitThenMissClearsCurrentObservation()
        {
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.forward));
            Assert.That(_pointer.HasValidHit, Is.True);
            _ownedCollider.center = new Vector3(0, 0, PointerHandlerMotif.MaxDrawDistanceMetres + 1f);
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.forward));
            Assert.That(_pointer.HasValidHit, Is.False, "a ray from across the room does not draw");
            _ownedCollider.center = new Vector3(0, 0, 0.8f);
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.forward));
            Assert.That(_pointer.HasValidHit, Is.True);
            SetAutoProperty("IsSelected", true);
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.right));
            Assert.That(_pointer.HasValidHit, Is.False);
            Assert.That(_pointer.IsSelected, Is.False);
        }

        [Test]
        public void WrongLayerAndInvalidRaysCannotLeaveStaleHit()
        {
            _ownedCollider.gameObject.layer = 0;
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.forward));
            Assert.That(_pointer.HasValidHit, Is.False);
            _ownedCollider.gameObject.layer = 7;
            _ownedCollider.enabled = false;
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.forward));
            Assert.That(_pointer.HasValidHit, Is.False);
            _ownedCollider.enabled = true;
            _pointer.RefreshHit(new Ray(new Vector3(float.NaN, 0, 0), Vector3.forward));
            Assert.That(_pointer.HasValidHit, Is.False);
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.zero));
            Assert.That(_pointer.HasValidHit, Is.False);
        }

        [Test]
        public void PrefabFusionMotifIsTheOnlyStaticWriterAndLocalBackendBindsNothing()
        {
            // T3.5, parity row O2: Fusion mode syncs strokes through WhiteboardManagerMotif.
            // The local backend adds its view and input at runtime, so the authored
            // prefab must carry none of them, and it must list the motif as a legacy
            // writer for FoundationLocal mode to suppress.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            Assert.That(prefab, Is.Not.Null);
            var components = prefab.GetComponentsInChildren<Component>(true);
            Assert.That(components.Any(c => c != null && c.GetType().Name == "WhiteboardManagerMotif"), Is.True);
            foreach (string localOnly in new[] { "WhiteboardContentView", "FreeformSceneWhiteboardInput" })
            {
                Assert.That(components.Any(c => c != null && c.GetType().Name == localOnly), Is.False,
                    localOnly + " belongs to the local backend and is never authored on the prefab.");
            }
            var definition = prefab.GetComponent("FreeformSceneWhiteboardDefinition");
            Assert.That(definition, Is.Not.Null);
            var legacy = (System.Collections.IEnumerable)definition.GetType()
                .GetProperty("LegacyContentBehaviours").GetValue(definition);
            bool listsMotif = false;
            foreach (UnityEngine.Object behaviour in legacy)
                listsMotif |= behaviour != null && behaviour.GetType().Name == "WhiteboardManagerMotif";
            Assert.That(listsMotif, Is.True, "FoundationLocal mode must be able to suppress the Fusion writer.");
        }

        [Test]
        public void DisableCallbackClearsHoverSelectionAndHitImmediately()
        {
            _pointer.RefreshHit(new Ray(Vector3.zero, Vector3.forward));
            SetAutoProperty("IsHovered", true);
            SetAutoProperty("IsSelected", true);
            _root.SetActive(false);
            // EditMode does not dispatch MonoBehaviour lifecycle messages for
            // this isolated object. Invoke the production callback directly.
            InvokeProductionOnDisable(_pointer);
            Assert.That(_pointer.IsHovered, Is.False);
            Assert.That(_pointer.IsSelected, Is.False);
            Assert.That(_pointer.HasValidHit, Is.False);
        }

        [Test]
        public void PrefabKeepsIsdkColliderOwnershipAndVirtualObjectLayer()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            Assert.That(prefab, Is.Not.Null);
            var pointer = prefab.GetComponentInChildren<PointerHandlerMotif>(true);
            Assert.That(pointer, Is.Not.Null);
            var serialized = new SerializedObject(pointer);
            var rayInteractable = serialized.FindProperty("_rayInteractable").objectReferenceValue;
            var targets = serialized.FindProperty("targetColliders");
            Assert.That(rayInteractable, Is.Not.Null);
            Assert.That(targets.arraySize, Is.EqualTo(1));
            var surface = new SerializedObject(rayInteractable).FindProperty("_surface").objectReferenceValue;
            Assert.That(surface, Is.Not.Null);
            var ownedCollider = new SerializedObject(surface).FindProperty("_collider").objectReferenceValue;
            Assert.That(ownedCollider, Is.Not.Null);
            Assert.That(targets.GetArrayElementAtIndex(0).objectReferenceValue, Is.SameAs(ownedCollider));
            Assert.That(pointer.gameObject.layer, Is.EqualTo(7));

#if FUSION2
            var drawing = pointer.GetComponent<PointerDrawingMotif>();
            Assert.That(drawing, Is.Not.Null);
            AssertNetworkObjectOwns(pointer.gameObject, drawing);
            var manager = prefab.GetComponentInChildren<WhiteboardManagerMotif>(true);
            Assert.That(manager, Is.Not.Null);
            AssertNetworkObjectOwns(manager.gameObject, manager);
            // Snapshot reception is a plain MonoBehaviour consumer. It exists
            // on the Whiteboard, but is not NetworkObject-owned behaviour.
            var snapshot = prefab.GetComponentInChildren<WhiteboardSnapshotReceiverMotif>(true);
            Assert.That(snapshot, Is.Not.Null);
            AssertNetworkObjectDoesNotOwn(snapshot.gameObject, snapshot);
#endif
        }

#if FUSION2
        [Test]
        public void UpdateBlocksTheDrawStartAtTheModeGate()
        {
            // Unity null: EditMode never dispatches OnDestroy, and the singleton has none, so a destroyed earlier
            // instance stays referenced. Its own Awake treats that as absent (Unity's == null), and so does this check.
            Assert.That(InteractionStateManagerMotif.Instance == null, Is.True,
                "This isolated production-Update test requires no live pre-existing interaction-state singleton.");
            var stateObject = new GameObject("interaction-state");
            var managerObject = new GameObject("whiteboard-manager");
            try
            {
                var interactionState = stateObject.AddComponent<InteractionStateManagerMotif>();
                InvokeProductionLifecycle(interactionState, "Awake");
                interactionState.SetMode(WhiteboardInteractionMode.PanelManipulation);
                var manager = managerObject.AddComponent<WhiteboardManagerMotif>();
                var drawing = _root.AddComponent<PointerDrawingMotif>();
                typeof(PointerDrawingMotif).GetField("m_isReady", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(drawing, true);
                typeof(PointerDrawingMotif).GetField("m_pointerHandlerMotif", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(drawing, _pointer);
                typeof(PointerDrawingMotif).GetField("m_whiteboardManagerMotif", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(drawing, manager);
                SetAutoProperty("HitPoint", new Vector3(0f, 0f, 2f));
                SetAutoProperty("IsHovered", true);
                SetAutoProperty("HasValidHit", true);
                SetAutoProperty("IsSelected", true);

                InvokeProductionLifecycle(drawing, "Update");
                Assert.That((bool)typeof(PointerDrawingMotif)
                    .GetField("m_isDrawing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(drawing), Is.False,
                    "the real mode gate blocks the draw start before authority or RPC work");
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(stateObject);
                // Leave no destroyed singleton behind for a later test.
                typeof(InteractionStateManagerMotif).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)
                    .SetValue(null, null);
            }
        }

        [Test]
        public void DrawingDisableCallbackReleasesAnInProgressStroke()
        {
            var drawing = _root.AddComponent<PointerDrawingMotif>();
            typeof(PointerDrawingMotif).GetField("m_isDrawing", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(drawing, true);
            drawing.enabled = false;
            // This is a direct OnDisable callback unit test, not a PlayMode
            // lifecycle-dispatch assertion.
            InvokeProductionOnDisable(drawing);
            Assert.That((bool)typeof(PointerDrawingMotif)
                .GetField("m_isDrawing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(drawing), Is.False);
        }

        [Test]
        public void DrawingDespawnStopsReadinessWithoutResettingAnUnrelatedMode()
        {
            Assert.That(InteractionStateManagerMotif.Instance, Is.Null,
                "This isolated lifecycle test requires no pre-existing interaction-state singleton.");
            var managerObject = new GameObject("interaction-state");
            try
            {
                var manager = managerObject.AddComponent<InteractionStateManagerMotif>();
                InvokeProductionLifecycle(manager, "Awake");
                Assert.That(InteractionStateManagerMotif.Instance, Is.SameAs(manager));
                var drawing = _root.AddComponent<PointerDrawingMotif>();
                typeof(PointerDrawingMotif).GetField("m_isDrawing", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(drawing, true);
                typeof(PointerDrawingMotif).GetField("m_isReady", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(drawing, true);
                manager.SetMode(WhiteboardInteractionMode.PanelManipulation);
                drawing.StopForDespawn();
                Assert.That((bool)typeof(PointerDrawingMotif)
                    .GetField("m_isDrawing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(drawing), Is.False);
                Assert.That(manager.CurrentMode, Is.EqualTo(WhiteboardInteractionMode.PanelManipulation));
                Assert.That((bool)typeof(PointerDrawingMotif)
                    .GetField("m_isReady", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(drawing), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }
#endif

        private void SetField(string name, object value)
        {
            typeof(PointerHandlerMotif).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_pointer, value);
        }

        private void SetAutoProperty(string name, object value)
        {
            typeof(PointerHandlerMotif).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                .GetSetMethod(true).Invoke(_pointer, new[] { value });
        }

        private static void InvokeProductionOnDisable(Component component)
        {
            InvokeProductionLifecycle(component, "OnDisable");
        }

        private static void InvokeProductionLifecycle(Component component, string methodName)
        {
            var callback = component.GetType().GetMethod(
                methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(callback, Is.Not.Null);
            callback.Invoke(component, null);
        }

#if FUSION2
        private static void AssertNetworkObjectOwns(GameObject gameObject, Component behaviour)
        {
            var networkObject = gameObject.GetComponent("NetworkObject");
            Assert.That(networkObject, Is.Not.Null);
            var behaviours = new SerializedObject(networkObject).FindProperty("NetworkedBehaviours");
            Assert.That(behaviours, Is.Not.Null);
            var found = false;
            for (var i = 0; i < behaviours.arraySize; i++)
            {
                found |= behaviours.GetArrayElementAtIndex(i).objectReferenceValue == behaviour;
            }
            Assert.That(found, Is.True);
        }

        private static void AssertNetworkObjectDoesNotOwn(GameObject gameObject, Component behaviour)
        {
            var networkObject = gameObject.GetComponent("NetworkObject");
            Assert.That(networkObject, Is.Not.Null);
            var behaviours = new SerializedObject(networkObject).FindProperty("NetworkedBehaviours");
            Assert.That(behaviours, Is.Not.Null);
            for (var i = 0; i < behaviours.arraySize; i++)
            {
                Assert.That(behaviours.GetArrayElementAtIndex(i).objectReferenceValue,
                    Is.Not.SameAs(behaviour));
            }
        }
#endif
    }
}
#endif
