// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using NUnit.Framework;
using TMPro;

namespace SpatialBlend.Tests
{
    /// <summary>
    /// A fresh checkout has no Assets/TextMesh Pro (git-ignored) until tools/setup_sdks.py imports it. Without it the player's
    /// TMP_Settings is null and the control panel never builds (v1.0 froze on the splash for exactly this reason).
    /// </summary>
    [Category("FreeformRestoration")]
    public sealed class TmpEssentialsTests
    {
        [Test]
        public void TmpSettingsAndDefaultFontAssetExistInTheProject()
        {
            Assert.That(TMP_Settings.instance, Is.Not.Null, "TMP Settings missing: run python3 tools/setup_sdks.py");
            Assert.That(TMP_Settings.defaultFontAsset, Is.Not.Null, "TMP default font asset missing: run python3 tools/setup_sdks.py");
        }
    }
}
#endif
