// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using Oculus.Interaction;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.Objects.Whiteboard;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialBlend.Tests
{
    // Loads serialized production scene wiring without entering Play Mode,
    // connecting to Photon/native services, or starting an XR device.
    [Category("FreeformRestoration")]
    public class FreeformSceneRestorationTests
    {
        private const string ScenePath = "Assets/SpatialBlend/Samples/Scenes/FreeformCollaboration.unity";
        private Scene _scene;
        private bool _openedScene;

        [SetUp]
        public void OpenScene()
        {
            _scene = SceneManager.GetSceneByPath(ScenePath);
            _openedScene = !_scene.isLoaded;
            if (_openedScene)
            {
                _scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            }
        }

        [TearDown]
        public void CloseScene()
        {
            if (_openedScene && _scene.isLoaded)
            {
                EditorSceneManager.CloseScene(_scene, true);
            }
        }

        [Test]
        public void WhiteboardUsesSharedIsdkInteractorAndItsOwnSurface()
        {
            var board = InstanceOf("Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab");
            var pointer = board.GetComponentInChildren<PointerHandlerMotif>(true);
            Assert.That(pointer, Is.Not.Null);
            var serialized = new SerializedObject(pointer);
            Assert.That(serialized.FindProperty("_rayInteractable").objectReferenceValue,
                Is.InstanceOf<RayInteractable>());
            var colliders = serialized.FindProperty("targetColliders");
            Assert.That(colliders.arraySize, Is.GreaterThan(0));
            for (int i = 0; i < colliders.arraySize; ++i)
            {
                var collider = colliders.GetArrayElementAtIndex(i).objectReferenceValue as Collider;
                Assert.That(collider, Is.Not.Null);
                Assert.That(collider.transform.IsChildOf(board.transform), Is.True);
                Assert.That(collider.gameObject.layer, Is.EqualTo(7));
            }
            Assert.That(serialized.FindProperty("targetLayer").intValue, Is.EqualTo(1 << 7));
            Assert.That(LayerMask.LayerToName(7), Is.EqualTo("VirtualObject"));

            var managers = _scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<InteractionModeManager>(true)).ToArray();
            Assert.That(managers.Length, Is.EqualTo(1));
            Assert.That(new SerializedObject(managers[0]).FindProperty("_interactor").objectReferenceValue,
                Is.InstanceOf<RayInteractor>());
            var modifications = PrefabUtility.GetPropertyModifications(board);
            Assert.That(modifications.Any(mod => mod.propertyPath == "leftHand" ||
                mod.propertyPath == "rightHand"), Is.False, "Obsolete raw-ray input overrides remain.");
        }

        [Test]
        public void ChessSceneOverrideTargetsTheRootNetworkOwner()
        {
            var chess = InstanceOf("Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab");
            Assert.That(chess.GetComponent<SbNetworkEntity>(), Is.Not.Null);
            var network = chess.GetComponent<NetworkObject>();
            Assert.That(network, Is.Not.Null);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(network);
            var sortKeys = PrefabUtility.GetPropertyModifications(chess)
                .Where(mod => mod.propertyPath == "SortKey").ToArray();
            Assert.That(sortKeys.Length, Is.EqualTo(1));
            Assert.That(sortKeys[0].target, Is.EqualTo(source));
            Assert.That(chess.transform.Find("Chess Board").GetComponent<SbNetworkEntity>(), Is.Null);
        }

        private GameObject InstanceOf(string prefabPath)
        {
            var instances = _scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(transform => transform.gameObject)
                .Where(gameObject => PrefabUtility.IsAnyPrefabInstanceRoot(gameObject) &&
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject) == prefabPath)
                .ToArray();
            Assert.That(instances.Length, Is.EqualTo(1), prefabPath);
            return instances[0];
        }
    }
}
#endif
