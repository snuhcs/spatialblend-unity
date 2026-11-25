// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Oculus.Interaction;
using TMPro;
using SpatialBlend.Samples;
using SpatialBlend.Samples.FreeformCollaboration;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class FreeformSceneLocalBackendTests
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
        public void PlayerBuildProfileIsFusionAndTheEditorDefaultStaysFoundationLocal()
        {
            FreeformCollaborationApp app = FindInScene<FreeformCollaborationApp>();
            Assert.That(app, Is.Not.Null);
            var serialized = (FreeformCollaborationRuntimeProfile)
                new SerializedObject(app).FindProperty("_runtimeProfile").enumValueIndex;
            Assert.That(serialized, Is.EqualTo(FreeformCollaborationRuntimeProfile.FoundationLocal),
                "The checked-in scene keeps the Editor default.");

            // Player build: Fusion whatever the scene serializes or an Editor override says.
            Assert.That(FreeformCollaborationApp.ResolveProfile(serialized, false, -1),
                Is.EqualTo(FreeformCollaborationRuntimeProfile.Fusion));
            Assert.That(FreeformCollaborationApp.ResolveProfile(serialized, false, 1),
                Is.EqualTo(FreeformCollaborationRuntimeProfile.Fusion));

            // Editor: the serialized default, unless the menu stored a valid override.
            Assert.That(FreeformCollaborationApp.ResolveProfile(serialized, true, -1),
                Is.EqualTo(FreeformCollaborationRuntimeProfile.FoundationLocal));
            Assert.That(FreeformCollaborationApp.ResolveProfile(serialized, true, 0),
                Is.EqualTo(FreeformCollaborationRuntimeProfile.Fusion));
            Assert.That(FreeformCollaborationApp.ResolveProfile(serialized, true, 99),
                Is.EqualTo(FreeformCollaborationRuntimeProfile.FoundationLocal));

            // The menu and the app agree on the stored key.
            string menu = System.IO.File.ReadAllText("Assets/SpatialBlend/Editor/FreeformProfileMenu.cs");
            Assert.That(menu, Does.Contain("\"" + FreeformCollaborationApp.EditorProfilePrefKey + "\""));
        }

        [Test]
        public void OriginalSceneUsesExplicitFoundationLocalDefinitionsAndLegacyProfileRemainsSelectable()
        {
            FreeformCollaborationApp app = FindInScene<FreeformCollaborationApp>();
            FreeformSceneLocalBackend backend = FindInScene<FreeformSceneLocalBackend>();
            Assert.That(app, Is.Not.Null);
            Assert.That(backend, Is.Not.Null);
            Assert.That(app.RuntimeProfile,
                Is.EqualTo(FreeformCollaborationRuntimeProfile.FoundationLocal));
            Assert.That(app.FoundationLocalBackend, Is.SameAs(backend));
            Assert.That(app.FoundationLocalGate, Is.Not.Null);
            Assert.That(app.FoundationLocalGate.SuppressFoundationLocalTransport(), Is.True);
            Assert.That(app.FoundationLocalGate.FoundationTransportSuppressed, Is.True,
                "The serialized legacy bootstrap must be suppressed before local startup.");
            var gateSerialized = new SerializedObject(app.FoundationLocalGate);
            var bootstrap = gateSerialized.FindProperty("_fusionBootstrap").objectReferenceValue;
            Assert.That(bootstrap, Is.Not.Null);
            Assert.That(new SerializedObject(bootstrap).FindProperty("StartMode").intValue, Is.EqualTo(2),
                "The authored legacy Fusion route remains explicitly Manual; local mode suppresses it, not rewrites it.");

            var legacyProbe = new GameObject("legacy-profile-probe").AddComponent<FreeformCollaborationApp>();
            try
            {
                var legacySerialized = new SerializedObject(legacyProbe);
                legacySerialized.FindProperty("_runtimeProfile").enumValueIndex =
                    (int)FreeformCollaborationRuntimeProfile.Fusion;
                legacySerialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(legacyProbe.TryStartFoundationLocal(out string legacyError), Is.False);
                Assert.That(legacyError, Does.Contain("Fusion profile"));
            }
            finally
            {
                Object.DestroyImmediate(legacyProbe.gameObject);
            }

            var whiteboard = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            var chess = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            Assert.That(whiteboard.GetComponent<FreeformSceneWhiteboardDefinition>(), Is.Not.Null);
            Assert.That(chess.GetComponent<FreeformSceneChessDefinition>(), Is.Not.Null);

            FreeformSceneWhiteboardDefinition sceneWhiteboard = FindInScene<FreeformSceneWhiteboardDefinition>();
            FreeformSceneChessDefinition sceneChess = FindInScene<FreeformSceneChessDefinition>();
            var backendSerialized = new SerializedObject(backend);
            Assert.That(backendSerialized.FindProperty("_whiteboard").objectReferenceValue,
                Is.SameAs(sceneWhiteboard));
            Assert.That(backendSerialized.FindProperty("_chess").objectReferenceValue,
                Is.SameAs(sceneChess));
            AssertSceneInstance(sceneWhiteboard, _scene);
            AssertSceneInstance(sceneWhiteboard.MovableRoot, _scene);
            AssertSceneInstance(sceneWhiteboard.SurfaceRenderer, _scene);
            AssertSceneInstance(sceneChess, _scene);
            AssertSceneInstance(sceneChess.MovableRoot, _scene);
            AssertSceneInstance(sceneChess.Board, _scene);
            foreach (FreeformSceneChessPieceReference binding in sceneChess.Pieces)
            {
                AssertSceneInstance(binding.Piece, _scene);
                AssertSceneInstance(binding.Interaction, _scene);
            }
        }

        [Test]
        public void OriginalPrefabBindingsUseExactSurfaceAndAllThirtyTwoAuthoredPieces()
        {
            var whiteboard = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            var boardDefinition = whiteboard.GetComponent<FreeformSceneWhiteboardDefinition>();
            Assert.That(boardDefinition.MovableRoot, Is.SameAs(whiteboard.transform));
            Assert.That(boardDefinition.SurfaceRenderer, Is.Not.Null);
            Assert.That(boardDefinition.SurfaceRenderer.transform.IsChildOf(whiteboard.transform), Is.True);
            Assert.That(boardDefinition.Pointer, Is.InstanceOf<PointerHandlerMotif>());
            Assert.That(boardDefinition.Palette.Length, Is.EqualTo(4));
            CollectionAssert.AreEquivalent(new[]
            {
                "SbNetworkEntity", "WhiteboardManagerMotif", "PointerDrawingMotif",
                "WhiteboardSnapshotReceiverMotif"
            }, boardDefinition.LegacyContentBehaviours.Where(item => item != null)
                .Select(item => item.GetType().Name).ToArray());

            var chess = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            var chessDefinition = chess.GetComponent<FreeformSceneChessDefinition>();
            Assert.That(chessDefinition.MovableRoot, Is.SameAs(chess.transform));
            Assert.That(chessDefinition.Board, Is.Not.Null);
            Assert.That(chessDefinition.Pieces.Length, Is.EqualTo(32));
            Assert.That(chessDefinition.Pieces.Select(item => item.PieceId).Distinct().Count(), Is.EqualTo(32));
            Assert.That(chessDefinition.Pieces.Select(item => item.Piece).Distinct().Count(), Is.EqualTo(32));
            // The prefab's root-level ISDK_RayGrabInteraction moves the complete
            // board.  Piece wrappers are exactly the authored Board subtree.
            var authoredWrappers = chessDefinition.Board
                .GetComponentsInChildren<InteractableUnityEventWrapper>(true).ToArray();
            Assert.That(authoredWrappers.Length, Is.EqualTo(32));
            CollectionAssert.AreEquivalent(authoredWrappers.Select(item => item.transform),
                chessDefinition.Pieces.Select(item => item.Piece),
                "Every stable local ID must bind exactly one original ISDK wrapper transform.");
            foreach (FreeformSceneChessPieceReference binding in chessDefinition.Pieces)
            {
                Assert.That(binding.Piece.IsChildOf(chessDefinition.MovableRoot), Is.True);
                Assert.That(binding.Interaction, Is.Not.Null);
                Assert.That(binding.Interaction.transform, Is.SameAs(binding.Piece));
                Assert.That(authoredWrappers.Single(item => item == binding.Interaction).transform,
                    Is.SameAs(binding.Piece));
            }
            CollectionAssert.AreEquivalent(new[] { "SbNetworkEntity", "ChessBoardHandlerMotif" },
                chessDefinition.LegacyContentBehaviours.Where(item => item != null)
                    .Select(item => item.GetType().Name).ToArray());
        }

        [Test]
        public void ActualControlPanelInstanceHasExplicitLocalAppAndConnectLabelBindings()
        {
            FreeformCollaborationApp app = FindInScene<FreeformCollaborationApp>();
            ControlPanel panel = _scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<ControlPanel>(true))
                .Single(candidate => candidate.enabled);
            Assert.That(panel, Is.Not.Null);
            var serialized = new SerializedObject(panel);
            Assert.That(serialized.FindProperty("_freeformApp").objectReferenceValue, Is.SameAs(app));
            Button connect = serialized.FindProperty("_connect").objectReferenceValue as Button;
            TMP_Text label = serialized.FindProperty("_connectLabel").objectReferenceValue as TMP_Text;
            Assert.That(connect, Is.Not.Null);
            Assert.That(label, Is.SameAs(connect.GetComponentInChildren<TMP_Text>(true)),
                "The local label must be the actual Connect child on this scene instance, not an asset reference.");
            Assert.That(serialized.FindProperty("_fusionBootstrap").objectReferenceValue, Is.Not.Null,
                "The legacy route remains serialized but is not selected by FoundationLocal Connect.");
        }

        private T FindInScene<T>() where T : Component => _scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).SingleOrDefault();

        private static void AssertSceneInstance(Component component, Scene expectedScene)
        {
            Assert.That(component, Is.Not.Null);
            Assert.That(component.gameObject.scene, Is.EqualTo(expectedScene));
            Assert.That(EditorUtility.IsPersistent(component), Is.False,
                "Local configuration must not point at a prefab asset.");
        }

        private static void AssertSceneInstance(Transform transform, Scene expectedScene)
        {
            Assert.That(transform, Is.Not.Null);
            Assert.That(transform.gameObject.scene, Is.EqualTo(expectedScene));
            Assert.That(EditorUtility.IsPersistent(transform), Is.False,
                "Local configuration must not point at a prefab asset.");
        }
    }
}
#endif
