// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// As in v1, an entity root is on the layer the
    /// object prefabs, GltfLoader._targetLayer and PointerHandlerMotif.targetLayer already use, VirtualObject
    /// (layer 7). abi4 kept "SbEntity" (layer 9), so every entity root was moved off the layer the whiteboard
    /// pointer and the placement rays mask.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class EntityLayerTests
    {
        private string _savedName;
        private bool _savedAuto;

        [SetUp]
        public void Save()
        {
            _savedName = SbEntity.EntityLayerName;
            _savedAuto = SbEntity.AutoSetLayer;
        }

        [TearDown]
        public void Restore()
        {
            SbEntity.EntityLayerName = _savedName;
            SbEntity.AutoSetLayer = _savedAuto;
        }

        [Test]
        public void TheEntityLayerIsVirtualObjectAtLayerSeven()
        {
            // Nothing in the project assigns EntityLayerName, so SetUp read the initializer's value.
            Assert.That(_savedName, Is.EqualTo("VirtualObject"));
            Assert.That(LayerMask.NameToLayer(_savedName), Is.EqualTo(7));
        }

        [Test]
        public void ASpawnedEntityRootLandsOnTheVirtualObjectLayer()
        {
            SbEntity.AutoSetLayer = true;
            var go = new GameObject("entity");
            try
            {
                go.layer = LayerMask.NameToLayer("Default");
                var entity = go.AddComponent<SbNativeEntity>();
                entity.Awake(); // EditMode does not run Awake
                Assert.That(go.layer, Is.EqualTo(7));
                Assert.That(LayerMask.LayerToName(go.layer), Is.EqualTo("VirtualObject"));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheEntityMaskIsTheMaskTheObjectRaysAlreadyUse()
        {
            // GltfLoader._targetLayer is NameToLayer("VirtualObject"); PointerHandlerMotif.targetLayer defaults to 1 << 7.
            Assert.That(SbEntity.GetEntityLayerMask().value, Is.EqualTo(1 << 7));
        }
    }
}
#endif
