// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;

namespace SpatialBlend.Tests
{
    [TestFixture]
    [Category("FreeformRestoration")]
    public sealed class SbPermissionManagerTests
    {
        // v1 SbPermissionManager.RuntimePermissions at commit 7144448.
        private static readonly string[] V1Permissions =
        {
            "android.permission.CAMERA",
            "horizonos.permission.HEADSET_CAMERA",
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.RECORD_AUDIO",
            "com.oculus.permission.USE_SCENE",
        };

        [Test]
        public void RequestedPermissionsMatchV1Exactly()
        {
            var actual = new List<string>(SbPermissionManager.RequestedPermissions);
            Assert.That(actual, Is.EqualTo(V1Permissions));
        }
    }
}
#endif
