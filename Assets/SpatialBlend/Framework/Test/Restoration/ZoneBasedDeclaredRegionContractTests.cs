// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T2.1b. ZoneBased display reads the declared Containment Region from
    /// SbSpatialAuthoring and never the v1 scene volume component.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class ZoneBasedDeclaredRegionContractTests
    {
        private const string EntityPath = "Assets/SpatialBlend/Framework/Scripts/SbNetworkAvatarEntity.cs";
        private const string ZonePath = "Assets/SpatialBlend/Avatar/Scripts/DeclaredRegionZone.cs";
        private const string AuthoringPath = "Assets/SpatialBlend/Framework/Scripts/SbSpatialAuthoring.cs";
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; --i)
            {
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }

        [TestCase(EntityPath)]
        [TestCase(ZonePath)]
        [TestCase(AuthoringPath)]
        public void ZoneBasedSourcesNeverReferenceTheAffordanceVolume(string path)
        {
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Not.Contain("AffordanceVolume"), path);
            Assert.That(text, Does.Not.Contain("PolygonCollider2D"), path);
            Assert.That(text, Does.Not.Contain("AffordancePolygonContainment"), path);
        }

        [Test]
        public void ZoneBasedTriggerIsNotAPerFrameSceneScan()
        {
            string entity = File.ReadAllText(EntityPath);
            Assert.That(entity, Does.Not.Contain("FindObjectsByType<Affordance"));
            Assert.That(entity, Does.Contain("DeclaredRegionZone.Switch"));
            Assert.That(entity, Does.Contain("IsTransferInteracting"), "The core's value, never a point test of the app's own.");
            Assert.That(entity, Does.Not.Contain("SbSpatialAuthoring.ActiveRegions"));
            Assert.That(File.Exists("Assets/SpatialBlend/Avatar/Scripts/AffordancePolygonContainment.cs"), Is.False);
        }

        [Test]
        public void ZoneBasedHasNoDwellOrMarginOfItsOwn()
        {
            string zone = File.ReadAllText(ZonePath);
            foreach (string word in new[] { "dwell", "Dwell", "hysteresis", "Hysteresis", "Time.", "margin" })
            {
                // The only mentions allowed are the comments that say it has none.
                foreach (string line in zone.Split('\n'))
                {
                    string code = line.TrimStart();
                    if (code.StartsWith("///") || code.StartsWith("//")) continue;
                    Assert.That(code, Does.Not.Contain(word), line);
                }
            }
        }

        [Test]
        public void ZoneBasedIgnoresTheCoreInteractionEventBeforeItTouchesTheDisplayState()
        {
            string entity = File.ReadAllText(EntityPath);
            int handler = entity.IndexOf("private void OnAvatarInteraction(", System.StringComparison.Ordinal);
            Assert.That(handler, Is.GreaterThan(0));
            int guard = entity.IndexOf("if (IsZoneBasedMode)", handler, System.StringComparison.Ordinal);
            int apply = entity.IndexOf("SetInteractionState", handler, System.StringComparison.Ordinal);
            Assert.That(guard, Is.GreaterThan(handler));
            Assert.That(apply, Is.GreaterThan(guard), "The ZoneBased guard must return before SetInteractionState.");
        }

        [Test]
        public void AuthoringExposesItsRegionAndTheCompilerSharesTheSameResolver()
        {
            SbSpatialAuthoring authoring = NewBoard(Vector3.zero, Quaternion.identity);
            Assert.That(authoring.TryGetRegionPolygon(out Vector2[] region), Is.True);

            Assert.That(authoring.TryBuildDeclaration(out SpatialAuthoringDeclaration declaration, out string error),
                Is.True, error);
            Assert.That(SpatialAuthoringCompiler.TryCompile(declaration, out SpatialAuthoringPlan plan, out error),
                Is.True, error);
            Vector2[] compiled = plan.Mappings[0].polygon;
            Assert.That(region.Length, Is.EqualTo(compiled.Length));
            for (int i = 0; i < region.Length; ++i)
            {
                Assert.That(Vector2.Distance(region[i], compiled[i]), Is.LessThan(1e-5f), "vertex " + i);
            }
        }

        [Test]
        public void AuthoredRegionIsTheFootprintPlusTheDefaultMarginInTheObjectFrame()
        {
            // Footprint x -0.5..0.5, z -1..1. Default margin 0.5 m: x -1..1, z -1.5..1.5.
            SbSpatialAuthoring board = NewBoard(new Vector3(10f, 0f, 5f), Quaternion.Euler(0f, 90f, 0f));
            var regions = new IDeclaredRegion[] { board };

            Assert.That(Inside(new Vector3(11.4f, 0f, 5f), regions), Is.True,
                "1.4 m along the object's +Z is inside the 1.5 m region half-depth.");
            Assert.That(Inside(new Vector3(11.6f, 0f, 5f), regions), Is.False);
            Assert.That(Inside(new Vector3(10f, 0f, 5.9f), regions), Is.True);
            Assert.That(Inside(new Vector3(10f, 0f, 6.1f), regions), Is.False);
            // The footprint edge (0.5 m) is not the zone edge.
            Assert.That(Inside(new Vector3(10f, 0f, 5.6f), regions), Is.True);
        }

        [Test]
        public void ExplicitRegionPolygonOverridesTheMargin()
        {
            SbSpatialAuthoring board = NewBoard(Vector3.zero, Quaternion.identity);
            board.declaredExtent = new[]
            {
                new Vector2(-0.6f, -0.9f), new Vector2(0.6f, -0.9f),
                new Vector2(0.6f, 0.1f), new Vector2(-0.6f, 0.1f),
            };
            board.InvalidateRegion();
            var regions = new IDeclaredRegion[] { board };
            Assert.That(Inside(new Vector3(0f, 0f, -0.8f), regions), Is.True);
            Assert.That(Inside(new Vector3(0f, 0f, 0.5f), regions), Is.False,
                "A one-sided v1 PRESET region does not extend behind the object.");
        }

        [Test]
        public void ASceneAffordanceVolumeIsNotADeclaredRegion()
        {
            var far = new Vector3(1000f, 0f, 1000f);
            var volumeObject = new GameObject("scene-volume");
            _objects.Add(volumeObject);
            volumeObject.transform.position = far;
            var volume = volumeObject.AddComponent<AffordanceVolume>();
            volume.type = AffordanceVolume.AffordanceVolumeType.PRESET;
            volume.CustomPoints = new[]
            {
                new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f),
            };

            Assert.That(Inside(far, SbSpatialAuthoring.ActiveRegions), Is.False, "An undeclared volume is not a region.");

            SbSpatialAuthoring board = NewBoard(far, Quaternion.identity);
            Assert.That(Inside(far, new IDeclaredRegion[] { board }), Is.True, "The declared region at the same spot is.");
        }

        // Test-only point test: it checks the AUTHORED region geometry the core is given. The product holds none.
        private static bool Inside(Vector3 world, System.Collections.Generic.IEnumerable<IDeclaredRegion> regions)
        {
            foreach (IDeclaredRegion region in regions)
            {
                if (!region.TryGetRegion(out Vector2[] polygon, out Matrix4x4 worldToLocal)) continue;
                Vector3 local = worldToLocal.MultiplyPoint3x4(world);
                bool inside = false;
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    if ((polygon[i].y > local.z) != (polygon[j].y > local.z) &&
                        local.x < (polygon[j].x - polygon[i].x) * (local.z - polygon[i].y) /
                        (polygon[j].y - polygon[i].y) + polygon[i].x)
                    {
                        inside = !inside;
                    }
                }
                if (inside) return true;
            }
            return false;
        }

        private SbSpatialAuthoring NewBoard(Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("zone-board");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(position, rotation);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.1f, 2f);
            var authoring = go.AddComponent<SbSpatialAuthoring>();
            authoring.footprint = box;
            return authoring;
        }
    }
}
#endif
