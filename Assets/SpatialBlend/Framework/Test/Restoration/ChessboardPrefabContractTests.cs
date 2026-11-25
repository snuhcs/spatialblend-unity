// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using Oculus.Interaction;
using SpatialBlend.Interactions;
using SpatialBlend.Samples.Objects;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// Guards the Chess sample's ownership boundary: the root is the movable,
    /// shareable object; the scaled board is visual/gameplay content only.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class ChessboardPrefabContractTests
    {
        private const string PrefabPath =
            "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";

        [Test]
        [Category("AssetContract")]
        public void ChessRoot_OwnsShareAffordanceNetworkAndNavigation()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Assert.That(root.name, Is.EqualTo("Chess"));
                Assert.That(root.GetComponent<SbNetworkEntity>(), Is.Not.Null);
                Assert.That(root.GetComponent<NetworkObject>(), Is.Not.Null);
                Assert.That(root.GetComponent<SbSpatialAuthoring>(), Is.Not.Null);
                Assert.That(root.GetComponent<AffordanceVolume>(), Is.Null, "T3.6: the declaration replaces the volume.");
                Assert.That(root.GetComponent<NavMeshObstacle>(), Is.Not.Null);

                var board = root.transform.Find("Chess Board");
                Assert.That(board, Is.Not.Null);
                Assert.That(board.GetComponent<SbNetworkEntity>(), Is.Null,
                    "RegisterGameObject must own the complete movable Chess root.");
                Assert.That(board.GetComponent<NetworkObject>(), Is.Null);
                Assert.That(board.GetComponent<AffordanceVolume>(), Is.Null);

                // The v1 PRESET polygon is now the declared extent on the root.
                var authoring = root.GetComponent<SbSpatialAuthoring>();
                Assert.That(authoring.declaredExtent, Is.Not.Null.And.Not.Empty);
                Assert.That(authoring.declaredExtent.Max(point => point.y), Is.EqualTo(0.9f).Within(1e-4f));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        [Category("AssetContract")]
        public void ChessFusionMotifIsTheOnlyStaticWriterAndLocalBackendBindsNothing()
        {
            // T3.5, parity row O1: Fusion mode syncs the 32 pieces through the
            // ChessBoardHandlerMotif NetworkArray. The local backend attaches its
            // view and input at runtime, so the authored prefab never carries them,
            // and it lists the motif as a legacy writer for local mode to suppress.
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var components = root.GetComponentsInChildren<Component>(true);
                Assert.That(components.Any(c => c != null && c.GetType().Name == "ChessBoardHandlerMotif"), Is.True);
                foreach (string localOnly in new[] { "ChessContentView", "FreeformSceneChessInput" })
                {
                    Assert.That(components.Any(c => c != null && c.GetType().Name == localOnly), Is.False,
                        localOnly + " belongs to the local backend and is never authored on the prefab.");
                }
                var definition = root.GetComponent("FreeformSceneChessDefinition");
                Assert.That(definition, Is.Not.Null);
                var legacy = (System.Collections.IEnumerable)definition.GetType()
                    .GetProperty("LegacyContentBehaviours").GetValue(definition);
                bool listsMotif = false;
                foreach (Object behaviour in legacy)
                    listsMotif |= behaviour != null && behaviour.GetType().Name == "ChessBoardHandlerMotif";
                Assert.That(listsMotif, Is.True, "FoundationLocal mode must be able to suppress the Fusion writer.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        [Category("AssetContract")]
        public void ChessRoot_RetainsInteractionLayerAndPlacementContract()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Assert.That(root.layer, Is.EqualTo(7));
                Assert.That(root.GetComponentsInChildren<PolygonCollider2D>(true), Is.Empty,
                    "No volume helper collider is generated for the prefab.");

                foreach (var transform in root.GetComponentsInChildren<Transform>(true)
                    .Where(transform => (transform.gameObject.hideFlags & HideFlags.DontSave) == 0))
                {
                    Assert.That(transform.gameObject.layer, Is.EqualTo(7), transform.name);
                }

                var interactionColliders = root.GetComponentsInChildren<Collider>(true);
                Assert.That(interactionColliders.Length, Is.GreaterThan(0));
                foreach (var collider in interactionColliders)
                {
                    Assert.That(collider.gameObject.layer, Is.EqualTo(7), collider.name);
                }

                var pointables = root.GetComponentsInChildren<MonoBehaviour>(true)
                    .Where(component => component is IPointable)
                    .ToArray();
                Assert.That(pointables.Length, Is.GreaterThan(0));
                foreach (var pointable in pointables)
                {
                    Assert.That(pointable.gameObject.layer, Is.EqualTo(7), pointable.name);
                }

                var placement = root.GetComponent<ObjectPlacementMotif>();
                Assert.That(placement, Is.Not.Null);
                var placementSerialized = new SerializedObject(placement);
                Assert.That(placementSerialized.FindProperty("_positionOffset").vector3Value,
                    Is.EqualTo(new Vector3(0f, 0.1f, 0f)));
                Assert.That(placementSerialized.FindProperty("_hoverDistance").floatValue,
                    Is.EqualTo(0.5f));

                var anchorSpawner = root.GetComponent<SpawnOnMRUKAnchor>();
                Assert.That(anchorSpawner, Is.Not.Null);
                Assert.That(new SerializedObject(anchorSpawner)
                    .FindProperty("_targetLabel").intValue, Is.EqualTo(8));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
