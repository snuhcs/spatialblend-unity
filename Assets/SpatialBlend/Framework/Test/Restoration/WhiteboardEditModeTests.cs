// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SpatialBlend;
using SpatialBlend.Interactions;
using Oculus.Interaction;
using SpatialBlend.Samples.Objects;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using InteractionMode = SpatialBlend.Interactions.InteractionMode;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// In Scene Edit ("Editing") a trigger-drag on the whiteboard moves the board, so the pen must not draw at the same time.
    /// Drawing is a use of the board and belongs to Interaction mode.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class WhiteboardEditModeTests
    {
        private GameObject _root;
        private PointerHandlerMotif _pointer;
        private GameObject _modeHost;
        private InteractionModeManager _modes;
        private GameObject _rayHost;

        private static readonly Ray OnBoard = new Ray(Vector3.zero, Vector3.forward);

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("whiteboard-edit-mode");
            _root.layer = 7;
            var surface = _root.AddComponent<BoxCollider>();
            surface.center = new Vector3(0, 0, 0.8f);
            surface.size = new Vector3(2, 2, 0.1f);
            _pointer = _root.AddComponent<PointerHandlerMotif>();
            Field("targetColliders").SetValue(_pointer, new Collider[] { surface });
            Field("targetLayer").SetValue(_pointer, (LayerMask)(1 << 7));

            _modeHost = new GameObject("modes");
            _modes = _modeHost.AddComponent<InteractionModeManager>();
            // EditMode does not run Awake, and Awake is what publishes InteractionModeManager.Instance.
            typeof(InteractionModeManager).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_modes, null);
            Assert.That(InteractionModeManager.Instance, Is.SameAs(_modes));
            // EditMode does not run OnEnable either; it subscribes the pointer to mode changes.
            Lifecycle("OnEnable");
        }

        [TearDown]
        public void TearDown()
        {
            SetLocalInteracting(false);
            if (_pointer != null) Lifecycle("OnDisable");
            if (_root != null) Object.DestroyImmediate(_root);
            if (_modeHost != null) Object.DestroyImmediate(_modeHost);
            if (_rayHost != null) Object.DestroyImmediate(_rayHost);
        }

        [Test]
        public void ThePenDrawsInInteractionMode()
        {
            _modes.SetMode(InteractionMode.Interaction);

            _pointer.Observe(OnBoard, triggerDown: true);

            Assert.That(PointerHandlerMotif.PenHeldByEditMode, Is.False);
            Assert.That(_pointer.HasValidHit, Is.True);
            Assert.That(_pointer.IsSelected, Is.True, "the trigger on the board draws in Interaction");
        }

        [Test]
        public void ThePenIsHeldInSceneEditEvenWithTheTriggerOnTheBoard()
        {
            _modes.SetMode(InteractionMode.SceneEdit);

            _pointer.Observe(OnBoard, triggerDown: true);

            Assert.That(PointerHandlerMotif.PenHeldByEditMode, Is.True);
            Assert.That(_pointer.IsSelected, Is.False, "in Editing the trigger moves the board; it must not also draw");
            Assert.That(_pointer.HasValidHit, Is.False, "no pen dot on the board while editing");
        }

        [Test]
        public void AStrokeInProgressEndsTheFrameTheModeBecomesSceneEdit()
        {
            _modes.SetMode(InteractionMode.Interaction);
            _pointer.Observe(OnBoard, triggerDown: true);
            Assert.That(_pointer.IsSelected, Is.True);

            _modes.SetMode(InteractionMode.SceneEdit);
            _pointer.Observe(OnBoard, triggerDown: true);

            // PointerDrawingMotif and FreeformSceneWhiteboardInput both end a stroke when the pointer stops reporting a selection.
            Assert.That(_pointer.IsSelected, Is.False);

            _modes.SetMode(InteractionMode.Interaction);
            _pointer.Observe(OnBoard, triggerDown: false);
            _pointer.Observe(OnBoard, triggerDown: true);
            Assert.That(_pointer.IsSelected, Is.True, "back in Interaction a fresh press draws again");
        }

        [Test]
        public void TheModeChangeEndsTheSelectionAtOnceWithoutWaitingForTheNextFrame()
        {
            _modes.SetMode(InteractionMode.Interaction);
            _pointer.Observe(OnBoard, triggerDown: true);
            Assert.That(_pointer.IsSelected, Is.True);

            _modes.SetMode(InteractionMode.SceneEdit);

            // No Observe in between: the drawing motif may update before the pointer this frame, and must already read no selection.
            Assert.That(_pointer.IsSelected, Is.False);
            Assert.That(_pointer.HasValidHit, Is.False);
        }

        [Test]
        public void ATriggerHeldFromEditingIntoInteractionDoesNotDrawUntilPressedAgain()
        {
            _modes.SetMode(InteractionMode.SceneEdit);
            _pointer.Observe(OnBoard, triggerDown: true); // dragging the board in Editing

            _modes.SetMode(InteractionMode.Interaction); // the chip or the idle return, with the trigger still held
            _pointer.Observe(OnBoard, triggerDown: true);
            Assert.That(_pointer.IsSelected, Is.False, "a held trigger never starts a stroke on its own");
            Assert.That(_pointer.HasValidHit, Is.True, "the pen dot is back");

            _pointer.Observe(OnBoard, triggerDown: false);
            _pointer.Observe(OnBoard, triggerDown: true);
            Assert.That(_pointer.IsSelected, Is.True);
        }

        [Test]
        public void TheTriggerOffTheBoardNeverSelects()
        {
            _modes.SetMode(InteractionMode.Interaction);
            _pointer.Observe(new Ray(Vector3.zero, Vector3.right), triggerDown: true);
            Assert.That(_pointer.IsSelected, Is.False);
            _pointer.Observe(OnBoard, triggerDown: false);
            Assert.That(_pointer.IsSelected, Is.False);
        }

        [Test]
        public void UpdateObservesAnOwnedBoardRayWhenGlobalAvatarInteractionIsFalse()
        {
            _modes.SetMode(InteractionMode.Interaction);
            _rayHost = new GameObject("ray");
            var ray = _rayHost.AddComponent<AimedRayInteractor>();
            ray.Aim(Vector3.zero, Vector3.forward);
            SetAutoProperty("IsHovered", true);
            Field("_activeInteractor").SetValue(_pointer, ray);

            // This drives Update rather than the public Observe seam. v1 board input
            // did not depend on the global avatar interaction/display state.
            SetLocalInteracting(false);
            Lifecycle("Update");

            Assert.That(_pointer.HasValidHit, Is.True,
                "a valid owned board ray must not be cleared by unrelated avatar-transfer state");
            Assert.That(_pointer.IsSelected, Is.False, "this fixture has no trigger input");
        }

        [Test]
        public void WithNoModeManagerThePenIsNeverHeld()
        {
            Object.DestroyImmediate(_modeHost);
            _modeHost = null;

            _pointer.Observe(OnBoard, triggerDown: true);

            Assert.That(PointerHandlerMotif.PenHeldByEditMode, Is.False, "the Foundation local build has no mode manager");
            Assert.That(_pointer.IsSelected, Is.True);
        }

        private void Lifecycle(string method) =>
            typeof(PointerHandlerMotif).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_pointer, null);

        private static FieldInfo Field(string name) =>
            typeof(PointerHandlerMotif).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private void SetAutoProperty(string name, object value) =>
            typeof(PointerHandlerMotif).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                .GetSetMethod(true).Invoke(_pointer, new[] { value });

        private static void SetLocalInteracting(bool value) =>
            typeof(SbAvatarBehaviourFusion).GetProperty(nameof(SbAvatarBehaviourFusion.IsLocalInteracting),
                    BindingFlags.Static | BindingFlags.Public)
                .GetSetMethod(true).Invoke(null, new object[] { value });

        /// <summary>EditMode stand-in for the controller ray. Origin and Forward are protected.</summary>
        private sealed class AimedRayInteractor : RayInteractor
        {
            public void Aim(Vector3 origin, Vector3 forward)
            {
                Origin = origin;
                Forward = forward;
            }
        }
    }

    /// <summary>
    /// The scene once added a second PanelPlacementMotif and SpawnOnMRUKAnchor to the whiteboard on top of the prefab's own pair, so two
    /// motifs (with different hover heights and wall offsets) steered the board during one grab. The scene's whiteboard has exactly the prefab's.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class WhiteboardScenePlacementTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private const string PrefabPath = "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab";
        private Scene _scene;
        private bool _openedScene;

        [SetUp]
        public void OpenScene()
        {
            _scene = SceneManager.GetSceneByPath(ScenePath);
            _openedScene = !_scene.isLoaded;
            if (_openedScene) _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        [TearDown]
        public void CloseScene()
        {
            if (_openedScene && _scene.isLoaded) EditorSceneManager.CloseScene(_scene, true);
        }

        [Test]
        public void TheSceneWhiteboardHasOnePlacementMotifAndOneSpawnerWithThePrefabsValues()
        {
            GameObject[] boards = _scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(t => t.gameObject)
                .Where(go => PrefabUtility.IsAnyPrefabInstanceRoot(go) && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == PrefabPath)
                .ToArray();
            Assert.That(boards.Length, Is.EqualTo(1));

            PanelPlacementMotif[] motifs = boards[0].GetComponentsInChildren<PanelPlacementMotif>(true);
            SpawnOnMRUKAnchor[] spawners = boards[0].GetComponentsInChildren<SpawnOnMRUKAnchor>(true);
            Assert.That(motifs.Length, Is.EqualTo(1), "one placement motif steers the board");
            Assert.That(spawners.Length, Is.EqualTo(1), "one spawner places it on the wall");
            Assert.That(PrefabUtility.IsAddedComponentOverride(motifs[0]), Is.False, "it is the prefab's own motif, not a scene addition");
            Assert.That(PrefabUtility.IsAddedComponentOverride(spawners[0]), Is.False);

            var serialized = new SerializedObject(motifs[0]);
            Assert.That(serialized.FindProperty("_hoverDistance").floatValue, Is.EqualTo(1.4f).Within(1e-4f));
            Assert.That(serialized.FindProperty("_wallOffset").floatValue, Is.EqualTo(-0.1f).Within(1e-4f));
            Assert.That(serialized.FindProperty("_panelHighlight").objectReferenceValue, Is.Not.Null);
        }
    }
}
#endif
