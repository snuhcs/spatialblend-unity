// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// T3.6. The Editor-only helper turns a volume left on old content into an SbSpatialAuthoring
    /// declaration. It is the one place that may still read the volume.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public class AffordanceVolumeMigrationTests
    {
        private GameObject _object;

        [TearDown]
        public void TearDown()
        {
            if (_object != null) Object.DestroyImmediate(_object);
        }

        [Test]
        public void ToDeclaredExtent_PlacesTheVolumeInTheObjectFrameAndRemovesTheScale()
        {
            var points = new[] { new Vector2(1f, 1f), new Vector2(-1f, 1f), new Vector2(-1f, -1f) };
            Vector2[] unscaled = AffordanceVolumeMigration.ToDeclaredExtent(
                points, new Vector2(0.5f, 0f), Vector2.one, Vector2.one);
            Assert.That(unscaled[0], Is.EqualTo(new Vector2(1.5f, 1f)));
            Assert.That(unscaled[1], Is.EqualTo(new Vector2(-0.5f, 1f)));

            // A PRESET volume was scaled by the entity's lossy scale; the authoring frame is in local units.
            Vector2[] preset = AffordanceVolumeMigration.ToDeclaredExtent(
                points, Vector2.zero, new Vector2(2f, 4f), new Vector2(2f, 4f));
            Assert.That(preset.Select(p => p.x), Is.EqualTo(points.Select(p => p.x)));
            Assert.That(preset.Select(p => p.y), Is.EqualTo(points.Select(p => p.y)));
        }

        [Test]
        public void APresetVolumeBecomesTheDeclaredExtentAndTheColliderBecomesTheFootprint()
        {
            _object = new GameObject("old-content");
            var box = _object.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.1f, 1.6f);
            var volume = _object.AddComponent<AffordanceVolume>();
            volume.type = AffordanceVolume.AffordanceVolumeType.PRESET;
            volume.SourceCollider = box;
            volume.CustomPoints = new[]
            {
                new Vector2(0.6f, 0.9f), new Vector2(0.6f, -0.9f), new Vector2(-0.6f, -0.9f), new Vector2(-0.6f, 0.9f),
            };

            Assert.That(AffordanceVolumeMigration.TryConvert(volume, out SbSpatialAuthoring authoring, out string error),
                Is.True, error);
            Assert.That(authoring.footprint, Is.SameAs(box));
            Assert.That(authoring.declaredExtent, Is.EqualTo(volume.CustomPoints));
            Assert.That(authoring.TryGetRegionPolygon(out Vector2[] region), Is.True);
            Assert.That(region.Max(p => p.x), Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(region.Max(p => p.y), Is.EqualTo(0.9f).Within(1e-4f));
        }

        [Test]
        public void AnExistingDeclarationKeepsItsExtent()
        {
            _object = new GameObject("old-content");
            var box = _object.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.1f, 1f);
            var authoring = _object.AddComponent<SbSpatialAuthoring>();
            authoring.footprint = box;
            authoring.declaredExtent = new[]
            {
                new Vector2(1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, -1f), new Vector2(-1f, 1f),
            };
            var volume = _object.AddComponent<AffordanceVolume>();
            volume.type = AffordanceVolume.AffordanceVolumeType.PRESET;
            volume.CustomPoints = new[]
            {
                new Vector2(3f, 3f), new Vector2(3f, -3f), new Vector2(-3f, -3f), new Vector2(-3f, 3f),
            };

            Assert.That(AffordanceVolumeMigration.TryConvert(volume, out SbSpatialAuthoring result, out _), Is.True);
            Assert.That(result, Is.SameAs(authoring));
            Assert.That(result.declaredExtent[0], Is.EqualTo(new Vector2(1f, 1f)));
        }

        [Test]
        public void AVolumeWithNoPolygonIsRefused()
        {
            _object = new GameObject("old-content");
            var volume = _object.AddComponent<AffordanceVolume>();
            volume.type = AffordanceVolume.AffordanceVolumeType.PRESET;
            Assert.That(AffordanceVolumeMigration.TryConvert(volume, out SbSpatialAuthoring authoring, out string error),
                Is.False);
            Assert.That(authoring, Is.Null);
            Assert.That(error, Is.Not.Empty);
            Assert.That(_object.GetComponent<SbSpatialAuthoring>(), Is.Null);
        }
    }
}
#endif
