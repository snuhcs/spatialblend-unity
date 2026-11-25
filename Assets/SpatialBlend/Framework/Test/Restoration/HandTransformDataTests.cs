// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// The foundation gate does not reference Fusion, so the struct round-trip
    /// stays on the full-project gate. The layout change means a v1 build and a
    /// v2 build cannot share a room.
    /// </summary>
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class HandTransformDataTests
    {
        [Test]
        public void HandTransformData_RoundTripsPositionAndRotation()
        {
            var written = new HandTransformData
            {
                LeftPosition = new Vector3(0.11f, -0.22f, 0.33f),
                RightPosition = new Vector3(-0.44f, 0.55f, -0.66f),
                LeftRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f),
                RightRotation = new Quaternion(-0.4f, 0.3f, -0.2f, 0.1f),
            };

            int size = Marshal.SizeOf(typeof(HandTransformData));
            System.IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(written, buffer, false);
                var read = Marshal.PtrToStructure<HandTransformData>(buffer);
                Assert.That(read.LeftPosition, Is.EqualTo(written.LeftPosition));
                Assert.That(read.RightPosition, Is.EqualTo(written.RightPosition));
                Assert.That(read.LeftRotation, Is.EqualTo(written.LeftRotation));
                Assert.That(read.RightRotation, Is.EqualTo(written.RightRotation));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Test]
        public void NetworkedAvatarPrefab_UsesTheSingleHandStruct()
        {
            string prefab = File.ReadAllText(Path.Combine(
                Application.dataPath, "SpatialBlend/Avatar/Prefabs/MetaAvatarPrefab.prefab"));
            Assert.That(prefab, Does.Contain("_HandData:"));
            Assert.That(prefab, Does.Contain("LeftPosition:"));
            Assert.That(prefab, Does.Contain("RightRotation:"));
            Assert.That(prefab, Does.Not.Contain("_LeftHandRelativePosition"));
            Assert.That(prefab, Does.Not.Contain("_RightHandRelativePosition"));
            Assert.That(prefab, Does.Not.Contain("_LeftHandRelativeRotation"));
            Assert.That(prefab, Does.Not.Contain("_RightHandRelativeRotation"));
        }
    }
}
#endif
