// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The coordinate frames of the sb_set_element_* setters (spatialblend.h, ABI 3.1): geometry is element-local, with two
    /// world-baked exceptions, a NAVIGABLE polygon and a surface. These tests pin the call sites to that rule.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SceneFrameRulesTests
    {
        private static string[] RuntimeSources() =>
            Directory.GetFiles("Assets/SpatialBlend", "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Replace('\\', '/').Contains("/Test/") && !path.Replace('\\', '/').Contains("/Editor/"))
                .ToArray();

        [Test]
        public void TheOnlyNavigablePolygonIsTheRootRoomsRingAndItsWallsAreObstaclesUnderThatRoot()
        {
            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            // The ring is read as WORLD by the core; the room is a root element, whose local frame is the world.
            Assert.That(manager, Does.Contain("SetWalkablePolygon(roomElement, (int)SbGeometryRole.Navigable, room.Ring)"));
            // A wall is registered as its pieces (the wall minus the physical objects that cross it), all Obstacles.
            Assert.That(manager, Does.Contain("SetWalkablePolygon(element, (int)SbGeometryRole.Obstacle, piece)"));
            Assert.That(manager, Does.Contain("AddWallElement(roomElement)"), "Each wall's group is a child of the root room, whose frame is the world.");
            Assert.That(manager, Does.Contain("AddWallElement(_wallElementIDs[i])"), "A wall's pieces hang alone under its group (walls overlap at corners).");
            int navigablePolygonSites = 0;
            foreach (string path in RuntimeSources())
            {
                string text = File.ReadAllText(path);
                navigablePolygonSites += Regex.Matches(text, @"SetWalkablePolygon\([^;]*SbGeometryRole\.Navigable").Count +
                                         Regex.Matches(text, @"SetElementPolygon\([^;]*SbGeometryRole\.Navigable").Count;
            }
            Assert.That(navigablePolygonSites, Is.EqualTo(1), "A nested NAVIGABLE zone would have to be authored in world coordinates.");
        }

        [Test]
        public void SurfacesAreOnlyEverHandedOverInWorldCoordinatesThroughTheWorldSurfaceSetter()
        {
            foreach (string path in RuntimeSources())
            {
                if (path.EndsWith("SpatialBlendAPI.SceneInputs.cs")) continue;
                Assert.That(File.ReadAllText(path), Does.Not.Contain("sb_set_element_surface"), path);
            }
            string hierarchy = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.SpatialHierarchy.cs");
            Assert.That(hierarchy, Does.Contain("BakeSurface(mapping.surface, frameWorld)"), "Baked into the world at registration...");
            Assert.That(hierarchy, Does.Contain("RefreshAuthoredSupports"), "...and again when the object moves.");
        }

        [Test]
        public void AColliderPickedFromAChildIsComposedIntoTheElementsFrame()
        {
            string manager = File.ReadAllText("Assets/SpatialBlend/Framework/Scripts/SpatialBlendManager.cs");
            Assert.That(manager, Does.Contain("SetElementCollider(_spaceID, entityID, footprintCollider, sbEntity.EntityTransform)"));
        }

        // sb_add_sub_region places a patch in the owning definition's own frame, for a surface its plane chart (centroid origin,
        // x = first edge, z = (v1 - v0) x (v2 - v0), y = z x x), derived in the core's Z-up frame. Unity sends the authored
        // patch polygon unconverted with an identity transform, so it is authored in that chart. No shipped prefab declares one.
        private static readonly Vector3[] ChessTop =
        {
            new Vector3(-0.4f, 0.05f, -0.4f), new Vector3(0.4f, 0.05f, -0.4f),
            new Vector3(0.4f, 0.05f, 0.4f), new Vector3(-0.4f, 0.05f, 0.4f),
        };

        [Test]
        public void TheChartOfACounterClockwiseHorizontalSurfaceIsItsXZAboutTheCentroid()
        {
            Assert.That(SurfaceChart.TryProject(ChessTop, new Vector3(0.2f, 0.05f, 0.2f), out Vector2 chart), Is.True);
            Assert.That(chart.x, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(chart.y, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(SurfaceChart.TryProject(ChessTop, new Vector3(0f, 0.05f, 0f), out Vector2 centre), Is.True);
            Assert.That(centre.magnitude, Is.LessThan(1e-6f), "The origin is the centroid.");
        }

        [Test]
        public void TheChartFollowsTheVertexOrderAndTheCentroidNotTheObjectOrWorldAxes()
        {
            // The same square wound the other way flips the normal, so chart y points the other way.
            Vector3[] reversed = { ChessTop[3], ChessTop[2], ChessTop[1], ChessTop[0] };
            Assert.That(SurfaceChart.TryProject(reversed, new Vector3(0.2f, 0.05f, 0.2f), out Vector2 chart), Is.True);
            Assert.That(chart.x, Is.EqualTo(0.2f).Within(1e-5f), "x is the first edge, v0 to v1.");
            Assert.That(chart.y, Is.EqualTo(-0.2f).Within(1e-5f));

            // Shifted off the object origin: the same corner is the same chart point, an XZ polygon would not be.
            var shifted = new Vector3[ChessTop.Length];
            for (int i = 0; i < ChessTop.Length; ++i) shifted[i] = ChessTop[i] + new Vector3(3f, 0f, -2f);
            Assert.That(SurfaceChart.TryProject(shifted, new Vector3(3.2f, 0.05f, -1.8f), out Vector2 offCentre), Is.True);
            Assert.That(offCentre.x, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(offCentre.y, Is.EqualTo(0.2f).Within(1e-5f));

            // A vertical board (the whiteboard face): object XZ cannot describe it, the chart can.
            Vector3[] board =
            {
                new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
            };
            Assert.That(SurfaceChart.TryProject(board, new Vector3(1f, 1f, 0f), out Vector2 corner), Is.True);
            Assert.That(corner.x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(corner.y, Is.EqualTo(0.5f).Within(1e-5f), "Half the height above the centroid.");
        }

        [Test]
        public void ADegenerateSurfaceHasNoChart()
        {
            Assert.That(SurfaceChart.TryProject(new[] { Vector3.zero, Vector3.right, Vector3.right * 2f }, Vector3.zero, out _), Is.False);
            Assert.That(SurfaceChart.TryProject(null, Vector3.zero, out _), Is.False);
        }
    }
}
#endif
