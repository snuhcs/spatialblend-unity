// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T3.7: the Chess, Whiteboard, GLB content prefabs and the TABLE anchor each carry an
    /// <see cref="SbSpatialAuthoring"/> declaration with a real extent, footprint and tagged surfaces.
    /// Each value is derived from the geometry the prefab already holds.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class SpatialAuthoringPrefabContractTests
    {
        private const string ChessPath = "Assets/SpatialBlend/Samples/Objects/Chessboard/Chess.prefab";
        private const string WhiteboardPath = "Assets/SpatialBlend/Samples/Objects/Whiteboard/WhiteboardPrefab.prefab";
        private const string ContentPath = "Assets/SpatialBlend/Samples/Objects/ObjectPreset.prefab";

        [Test]
        [Category("AssetContract")]
        public void AuthoringSitsOnTheOneSharedEntityOfEachContentPrefab()
        {
            foreach (string path in new[] { ChessPath, WhiteboardPath, ContentPath })
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    // The manager reads the declaration from the registered entity's own GameObject.
                    SbEntity[] entities = root.GetComponentsInChildren<SbEntity>(true);
                    Assert.That(entities, Has.Length.EqualTo(1), path);
                    SbSpatialAuthoring[] authoring = root.GetComponentsInChildren<SbSpatialAuthoring>(true);
                    Assert.That(authoring, Has.Length.EqualTo(1), path);
                    Assert.That(authoring[0].gameObject, Is.SameAs(entities[0].gameObject), path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        [Test]
        [Category("AssetContract")]
        public void ChessDeclaresTheTableFootprintTheV1ExtentAndThePlayingSurface()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(ChessPath);
            try
            {
                SpatialAuthoringPlan plan = Compile(root);
                SpatialElementMapping region = plan.Mappings[0];
                SpatialElementMapping table = plan.Mappings[1];
                SpatialElementMapping playable = plan.Mappings.Single(
                    mapping => mapping.kind == SpatialHierarchyNodeKind.Affordance);

                // Extent: the v1 PRESET, two seated sides along Z. It is declared, not footprint plus a margin.
                Assert.That(plan.ExtentIsDeclared, Is.True);
                AssertBounds(region.polygon, -0.5f, 0.5f, -0.9f, 0.9f);
                // Footprint: the table top, the symmetric envelope of the mesh whichever way X is mirrored.
                AssertBounds(table.polygon, -0.43f, 0.43f, -0.41f, 0.41f);
                Assert.That(playable.tag, Is.EqualTo("Playable"));
                Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(playable.surface).y, Is.GreaterThan(0.99f));

                // The footprint covers the collider mesh of the root, which is the table.
                Bounds tableMesh = root.GetComponent<MeshCollider>().sharedMesh.bounds;
                Assert.That(table.polygon.Min(point => point.x), Is.LessThanOrEqualTo(tableMesh.min.x + 0.02f));
                Assert.That(table.polygon.Max(point => point.x), Is.GreaterThanOrEqualTo(tableMesh.max.x - 0.02f));
                Assert.That(table.polygon.Min(point => point.y), Is.LessThanOrEqualTo(tableMesh.min.z + 0.02f));
                Assert.That(table.polygon.Max(point => point.y), Is.GreaterThanOrEqualTo(tableMesh.max.z - 0.02f));

                // The playing surface is the top of the board mesh, in the root frame.
                Transform board = root.transform.Find("Chess Board");
                Assert.That(board, Is.Not.Null);
                Bounds boardMesh = board.GetComponent<MeshFilter>().sharedMesh.bounds;
                float boardTop = board.localPosition.y + boardMesh.max.y * board.localScale.y;
                Assert.That(playable.surface.All(vertex => Mathf.Abs(vertex.y - boardTop) < 0.005f), Is.True,
                    "The playing surface is the board's top face (" + boardTop + ").");
                float halfX = boardMesh.extents.x * board.localScale.x;
                float halfZ = boardMesh.extents.z * board.localScale.z;
                // Eight squares fit inside the board mesh, which also carries the frame.
                Assert.That(playable.surface.All(vertex =>
                    Mathf.Abs(vertex.x - board.localPosition.x) <= halfX &&
                    Mathf.Abs(vertex.z - board.localPosition.z) <= halfZ), Is.True);
                Assert.That(playable.surface.Max(vertex => vertex.x) - playable.surface.Min(vertex => vertex.x),
                    Is.EqualTo(0.396f).Within(0.01f));
                // The surface is on the footprint, the footprint is in the extent.
                Assert.That(playable.surface.All(vertex => Mathf.Abs(vertex.x) <= 0.43f && Mathf.Abs(vertex.z) <= 0.41f), Is.True);
                Assert.That(root.GetComponent<AffordanceVolume>(), Is.Null, "T3.6: the declaration replaces the volume.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [Test]
        [Category("AssetContract")]
        public void WhiteboardDeclaresThePanelSlabTheV1ExtentAndTheDrawingPlane()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(WhiteboardPath);
            try
            {
                SpatialAuthoringPlan plan = Compile(root);
                SpatialElementMapping region = plan.Mappings[0];
                SpatialElementMapping panel = plan.Mappings[1];
                SpatialElementMapping face = plan.Mappings.Single(
                    mapping => mapping.kind == SpatialHierarchyNodeKind.Affordance);

                Assert.That(plan.ExtentIsDeclared, Is.True);
                // Extent: the v1 one-sided PRESET, 0.1 m past each panel edge and 0.8 m out in front of the face.
                AssertBounds(region.polygon, -0.6f, 0.6f, -0.8f, 0.1f);
                // Footprint: the unit panel, thickened 4 cm behind the face so the polygon is not degenerate.
                AssertBounds(panel.polygon, -0.5f, 0.5f, 0f, 0.04f);
                Assert.That(face.tag, Is.EqualTo("Writable"));

                // The drawing plane is the panel's own box collider: the unit quad at z = 0.
                BoxCollider collider = root.GetComponentInChildren<BoxCollider>(true);
                Assert.That(collider, Is.Not.Null);
                Assert.That(face.surface.Max(vertex => vertex.x) - face.surface.Min(vertex => vertex.x),
                    Is.EqualTo(collider.size.x).Within(1e-3f));
                Assert.That(face.surface.Max(vertex => vertex.y) - face.surface.Min(vertex => vertex.y),
                    Is.EqualTo(collider.size.y).Within(1e-3f));
                Assert.That(face.surface.All(vertex => Mathf.Abs(vertex.z - collider.center.z) < 1e-4f), Is.True);
                // The face looks to -Z, which is the side the extent reaches into and the side a quad shows.
                Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(face.surface).z, Is.LessThan(-0.99f));
                Assert.That(panel.polygon.Min(point => point.y), Is.EqualTo(face.surface[0].z).Within(1e-4f),
                    "The footprint's front edge is the writable face.");
                Assert.That(root.GetComponentInChildren<AffordanceVolume>(true), Is.Null, "T3.6: the declaration replaces the volume.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [Test]
        [Category("AssetContract")]
        public void GlbContentDeclaresItsRootColliderAndTheV1ContentMargin()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(ContentPath);
            try
            {
                SbSpatialAuthoring authoring = root.GetComponent<SbSpatialAuthoring>();
                Assert.That(authoring.footprint, Is.SameAs(root.GetComponent<BoxCollider>()));
                Assert.That(authoring.declaredExtent, Is.Null.Or.Empty,
                    "The extent of loaded content cannot be written into a prefab.");
                Assert.That(authoring.fallbackMarginMeters, Is.EqualTo(0.15f));
                SpatialAuthoringPlan plan = Compile(root);
                Assert.That(plan.ExtentIsDeclared, Is.False);
                Assert.That(plan.AppliedMarginMeters, Is.EqualTo(0.15f));

                // The default collider is the unit box the loader replaces with the mesh bounds.
                AssertBounds(plan.Mappings[1].polygon, -0.5f, 0.5f, -0.5f, 0.5f);
                AssertBounds(plan.Mappings[0].polygon, -0.65f, 0.65f, -0.65f, 0.65f);
                SpatialElementMapping top = plan.Mappings.Single(
                    mapping => mapping.kind == SpatialHierarchyNodeKind.Affordance);
                Assert.That(top.tag, Is.EqualTo("Placeable"));
                Assert.That(top.surface.All(vertex => Mathf.Abs(vertex.y - 0.5f) < 1e-4f), Is.True);
                Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(top.surface).y, Is.GreaterThan(0.99f));

                // Each side fits the same root collider to the same mesh: identical declaration, identical signature.
                Assert.That(Compile(root).LocalGeometrySignature, Is.EqualTo(plan.LocalGeometrySignature));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [Test]
        [Category("AssetContract")]
        public void EveryContentPrefabKeepsIdenticalLocalGeometryWhateverItsPlacement()
        {
            foreach (string path in new[] { ChessPath, WhiteboardPath, ContentPath })
            {
                string signature = null;
                foreach (Matrix4x4 pose in new[]
                {
                    Matrix4x4.identity,
                    Matrix4x4.TRS(new Vector3(2f, 0f, 3f), Quaternion.Euler(0f, 90f, 0f), Vector3.one),
                    Matrix4x4.TRS(new Vector3(-5f, 0.2f, 1.5f), Quaternion.Euler(0f, 200f, 0f), Vector3.one),
                })
                {
                    GameObject root = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        root.transform.SetPositionAndRotation(pose.GetPosition(), pose.rotation);
                        string current = Compile(root).LocalGeometrySignature;
                        signature = signature ?? current;
                        Assert.That(current, Is.EqualTo(signature), path);
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
            }
        }

        // --- TABLE: no prefab. The MRUK anchor box is written by SceneAnchorSpatialAuthoring.

        [Test]
        public void TableDeclarationStandsTheMrukAnchorUp()
        {
            // MRUK volume: local +Z is up, the box extends backward from the top face.
            var center = new Vector3(0.1f, -0.2f, -0.35f);
            var size = new Vector3(1.2f, 0.8f, 0.7f);
            var anchor = new GameObject("TABLE");
            try
            {
                SbSpatialAuthoring authoring = SceneAnchorSpatialAuthoring.Apply(anchor, center, size);
                Assert.That(authoring, Is.Not.Null);
                Assert.That(authoring.frameEulerAngles, Is.EqualTo(SceneAnchorSpatialAuthoring.MrukFrameEulerAngles));
                Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                    Is.True, error);
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                    Is.True, error);

                SpatialElementMapping region = plan.Mappings[0];
                SpatialElementMapping table = plan.Mappings[1];
                SpatialElementMapping top = plan.Mappings[2];
                Assert.That(plan.ExtentIsDeclared, Is.True);
                AssertBounds(table.polygon, -0.5f, 0.7f, -0.2f, 0.6f);
                // Extent: the tabletop plus the 0.5 m stand-off on every side.
                AssertBounds(region.polygon, -1.0f, 1.2f, -0.7f, 1.1f);
                Assert.That(top.tag, Is.EqualTo("Tabletop"));
                Assert.That(top.surface.All(vertex => Mathf.Abs(vertex.y) < 1e-4f), Is.True, "The top face is at local z = 0.");

                // Back in the anchor frame, the top face is local z = 0 and its normal is the anchor's +Z.
                Vector3 topCenterInAnchor = plan.FrameToObject.MultiplyPoint3x4(new Vector3(0.1f, 0f, 0.2f));
                Assert.That((topCenterInAnchor - new Vector3(0.1f, -0.2f, 0f)).magnitude, Is.LessThan(1e-4f));
                Vector3 normalInAnchor = plan.FrameToObject.MultiplyVector(
                    SpatialAuthoringCompiler.SurfaceNormalUnity(top.surface));
                Assert.That((normalInAnchor - Vector3.forward).magnitude, Is.LessThan(1e-4f));
                Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(top.surface).y, Is.GreaterThan(0.99f));

                // Applying again replaces the declaration, it does not stack components.
                string signature = plan.LocalGeometrySignature;
                Assert.That(SceneAnchorSpatialAuthoring.Apply(anchor, center, size), Is.SameAs(authoring));
                Assert.That(anchor.GetComponents<SbSpatialAuthoring>(), Has.Length.EqualTo(1));
                Assert.That(authoring.TryBuildDeclaration(out declaration, out error), Is.True, error);
                Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out plan, out error), Is.True, error);
                Assert.That(plan.LocalGeometrySignature, Is.EqualTo(signature));
            }
            finally { Object.DestroyImmediate(anchor); }
        }

        [Test]
        public void TableDeclarationRefusesAnEmptyOrNonFiniteBox()
        {
            var anchor = new GameObject("TABLE-empty");
            try
            {
                Assert.That(SceneAnchorSpatialAuthoring.Apply(anchor, Vector3.zero, new Vector3(0f, 1f, 1f)), Is.Null);
                Assert.That(SceneAnchorSpatialAuthoring.Apply(anchor, new Vector3(float.NaN, 0f, 0f), Vector3.one), Is.Null);
                Assert.That(SceneAnchorSpatialAuthoring.Apply(null, Vector3.zero, Vector3.one), Is.Null);
                Assert.That(anchor.GetComponent<SbSpatialAuthoring>(), Is.Null);
            }
            finally { Object.DestroyImmediate(anchor); }
        }

        [Test]
        public void ColliderFootprintFollowsTheAuthoringFrame()
        {
            var anchor = new GameObject("TABLE-collider");
            try
            {
                var box = anchor.AddComponent<BoxCollider>();
                box.center = new Vector3(0.1f, -0.2f, -0.35f);
                box.size = new Vector3(1.2f, 0.8f, 0.7f);
                var authoring = anchor.AddComponent<SbSpatialAuthoring>();
                authoring.footprint = box;
                authoring.frameEulerAngles = SceneAnchorSpatialAuthoring.MrukFrameEulerAngles;
                authoring.affordances = new[]
                {
                    new AuthoredAffordanceSurface { id = "TableTop", tag = "Tabletop", supportId = "table.top", surface = box },
                };
                Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                    Is.True, error);
                AssertBounds(declaration.footprint, -0.5f, 0.7f, -0.2f, 0.6f);
                Assert.That(declaration.affordances[0].surface.All(vertex => Mathf.Abs(vertex.y) < 1e-4f), Is.True);
                Assert.That(SpatialAuthoringCompiler.SurfaceNormalUnity(declaration.affordances[0].surface).y,
                    Is.GreaterThan(0.99f));
            }
            finally { Object.DestroyImmediate(anchor); }
        }

        private static SpatialAuthoringPlan Compile(GameObject root)
        {
            SbSpatialAuthoring authoring = root.GetComponentInChildren<SbSpatialAuthoring>(true);
            Assert.That(authoring, Is.Not.Null);
            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                Is.True, error);
            return plan;
        }

        private static void AssertBounds(Vector2[] polygon, float minX, float maxX, float minZ, float maxZ)
        {
            Assert.That(polygon.Min(point => point.x), Is.EqualTo(minX).Within(1e-3f));
            Assert.That(polygon.Max(point => point.x), Is.EqualTo(maxX).Within(1e-3f));
            Assert.That(polygon.Min(point => point.y), Is.EqualTo(minZ).Within(1e-3f));
            Assert.That(polygon.Max(point => point.y), Is.EqualTo(maxZ).Within(1e-3f));
        }
    }
}
#endif
