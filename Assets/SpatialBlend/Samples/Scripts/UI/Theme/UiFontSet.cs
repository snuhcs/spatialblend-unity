// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using TMPro;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>The UI typeface as two TMP font assets, referenced by <see cref="ControlPanel"/> so a build always carries them.</summary>
    [CreateAssetMenu(menuName = "SpatialBlend/UI Font Set")]
    public sealed class UiFontSet : ScriptableObject
    {
        public TMP_FontAsset Regular;
        public TMP_FontAsset SemiBold;
    }
}
