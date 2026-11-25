// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using SpatialBlend.Interactions;
using UnityEditor;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// As v1: (1) ObjectPlacementMotif releases on a mode block
    /// and only takes a grab while the mode allows it;
    /// PanelPlacementMotif gates on the mode, not on a flag nothing sets.
    /// (2) The entity prefabs lose the ISDK Grabbable that moved the entity a second way beside the ray
    /// placement (Chess.prefab, Globe.prefab, ObjectPreset.prefab).
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class PlacementModeReleaseTests
    {
        private GameObject _modeHost;
        private InteractionModeManager _mode;
        private GameObject _entity;
        private bool _ignoreFailing;

        [SetUp]
        public void SetUp()
        {
            // The motifs log an error when no controller or camera rig exists in an EditMode run.
            _ignoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            _modeHost = new GameObject("mode");
            _mode = _modeHost.AddComponent<InteractionModeManager>();
            // EditMode does not run Awake, and Awake is what publishes InteractionModeManager.Instance.
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_mode, null);
            Assert.That(InteractionModeManager.Instance, Is.SameAs(_mode));
            Assert.That(InteractionModeManager.IsMode(InteractionMode.SceneEdit), Is.True);
            _entity = new GameObject("entity");
        }

        [TearDown]
        public void TearDown()
        {
            if (_entity != null) UnityEngine.Object.DestroyImmediate(_entity);
            if (_modeHost != null) UnityEngine.Object.DestroyImmediate(_modeHost);
            LogAssert.ignoreFailingMessages = _ignoreFailing;
        }

        [Test]
        public void ObjectPlacementDropsItsGrabStateWhenTheModeBlocksIt()
        {
            var motif = _entity.AddComponent<ObjectPlacementMotif>();
            SetField(motif, "_targetPose", new Pose(Vector3.up, Quaternion.identity));
            Assert.That(motif.ReleaseIfModeBlocked(), Is.False, "SceneEdit allows placement");
            Assert.That(GetField(motif, "_targetPose"), Is.Not.Null, "an allowed mode keeps the settle target");

            _mode.SetMode(InteractionMode.Interaction);

            Assert.That(motif.ReleaseIfModeBlocked(), Is.True);
            Assert.That(GetField(motif, "_targetPose"), Is.Null, "the pending settle target is dropped");
            Assert.That(GetField(motif, "_selectingPointer"), Is.Null);
            Assert.That(GetField(motif, "_placementCoroutine"), Is.Null);
        }

        [Test]
        public void PlacementUpdateGivesUpTheExclusiveClaimWhenTheModeBlocksIt()
        {
            // PlacementMotif.Update (the base of both motifs) releases the claim; each motif's Update
            // then runs ReleaseIfModeBlocked. Only the focus gate keeps a batch run from calling Update itself.
            var motif = _entity.AddComponent<ProbePlacement>();
            Invoke(motif, "ClaimExclusive");
            _mode.SetMode(InteractionMode.Interaction);

            Invoke(motif, "Update");

            Assert.That(IsActiveByMe(motif), Is.False);
        }

        [Test]
        public void ObjectPlacementDoesNotTakeASelectionWhileTheModeBlocksIt()
        {
            var motif = _entity.AddComponent<ObjectPlacementMotif>();
            Assert.That(motif.AcceptsSelection(true), Is.True, "SceneEdit with focus accepts it");
            Assert.That(motif.AcceptsSelection(false), Is.False, "no focus, no grab");

            _mode.SetMode(InteractionMode.Interaction);

            Assert.That(motif.AcceptsSelection(true), Is.False, "a blocked mode never starts a grab");
        }

        [Test]
        public void PanelPlacementReleasesAGrabWhenTheModeBlocksIt()
        {
            var motif = _entity.AddComponent<PanelPlacementMotif>();
            Invoke(motif, "ClaimExclusive");
            Assert.That(IsActiveByMe(motif), Is.True);
            Assert.That(motif.ReleaseIfModeBlocked(), Is.False, "SceneEdit keeps the grab");
            Assert.That(IsActiveByMe(motif), Is.True);

            _mode.SetMode(InteractionMode.Interaction);

            Assert.That(motif.ReleaseIfModeBlocked(), Is.True);
            Assert.That(IsActiveByMe(motif), Is.False, "the grab in progress is released");
        }

        [Test]
        public void PanelPlacementDoesNotTakeASelectionWhileTheModeBlocksIt()
        {
            var motif = _entity.AddComponent<PanelPlacementMotif>();
            Assert.That(motif.AcceptsSelection(true), Is.True);
            _mode.SetMode(InteractionMode.Interaction);
            Assert.That(motif.AcceptsSelection(true), Is.False);
        }

        private sealed class ProbePlacement : PlacementMotif { }

        private static System.Collections.IEnumerator Forever() { while (true) yield return null; }

        // An object disabled while it settles is not left "in motion" forever (SpatialBlendManager skips the pose
        // push of an object in motion): the settle ends at its target and the object is at rest, so its pose is pushed.
        [Test]
        public void DisablingAnObjectMidSettleEndsTheSettleAtItsTarget()
        {
            var motif = _entity.AddComponent<ObjectPlacementMotif>();
            SetField(motif, "_entityTransform", _entity.transform);
            var target = new Pose(new Vector3(1f, 0.2f, 2f), Quaternion.Euler(0f, 30f, 0f));
            SetField(motif, "_targetPose", target);
            SetField(motif, "_placementCoroutine", motif.StartCoroutine(Forever()));
            Assert.That(motif.InMotion, Is.True, "settling");

            Invoke(motif, "OnDisable");

            Assert.That(motif.InMotion, Is.False, "at rest after the disable");
            Assert.That(GetField(motif, "_placementCoroutine"), Is.Null);
            Assert.That(Vector3.Distance(_entity.transform.position, target.position), Is.LessThan(1e-5f), "finished at its target");
        }

        // A grab does not outlive a pause or a loss of focus (the object would stay "in motion", never pushed).
        [Test]
        public void APauseLetsGoOfAGrab()
        {
            var motif = _entity.AddComponent<ObjectPlacementMotif>();
            SetField(motif, "_entityTransform", _entity.transform);
            Invoke(motif, "ClaimExclusive");
            Assert.That(motif.InMotion, Is.True);

            Invoke(motif, "OnApplicationPause", true);

            Assert.That(motif.InMotion, Is.False);
            Assert.That(motif.IsClaimed, Is.False);
            Assert.That(GetField(motif, "_selectingPointer"), Is.Null);
        }

        [Test]
        public void NoMotifCarriesTheDeadAllowInteractionFlag()
        {
            // v1 deleted the flag; nothing in abi4 ever set it false, so it only shadowed the mode gate.
            foreach (Type type in new[] { typeof(PlacementMotif), typeof(ObjectPlacementMotif), typeof(PanelPlacementMotif),
                         typeof(SpatialBlend.Interactions.Annotation.BoxAnnotationMotif) })
            {
                Assert.That(type.GetProperty("AllowInteraction", BindingFlags.Instance | BindingFlags.Public), Is.Null, type.Name);
            }
        }

        [TestCase("Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab", true)]
        [TestCase("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab", false)]
        public void TheEntityPrefabsAreMovedByTheRayPlacementOnly(string path, bool noGrabbableAtAll)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            var grabbables = prefab.GetComponentsInChildren<Grabbable>(true);
            if (noGrabbableAtAll)
            {
                Assert.That(grabbables, Is.Empty, path + ": the ISDK Grabbable moves the entity a second way");
            }
            foreach (Grabbable grabbable in grabbables)
            {
                // Chess pieces keep their own (v1 kept 32); none may move the entity root.
                var target = new SerializedObject(grabbable).FindProperty("_targetTransform").objectReferenceValue as Transform;
                Assert.That(target, Is.Not.SameAs(prefab.transform), path + ": a Grabbable still moves the entity root");
            }
            // The RayInteractable the placement motif listens to feeds no Grabbable.
            var motif = prefab.GetComponentInChildren<ObjectPlacementMotif>(true);
            Assert.That(motif, Is.Not.Null, path);
            var ray = new SerializedObject(motif).FindProperty("_rayInteractable").objectReferenceValue as RayInteractable;
            if (ray != null)
            {
                Assert.That(new SerializedObject(ray).FindProperty("_pointableElement").objectReferenceValue, Is.Null,
                    path + ": the entity's ray interactable still feeds a pointable element");
            }
        }

        private static bool IsActiveByMe(object motif) =>
            (bool)typeof(InteractionMotif).GetProperty("IsActiveByMe", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(motif);

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
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (method != null) { method.Invoke(target, args); return; }
            }
            throw new MissingMethodException(name);
        }
    }
}
#endif
