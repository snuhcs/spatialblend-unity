// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using TMPro;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI
{
    /// <summary>
    /// The UI typeface: Pretendard (SIL OFL 1.1, Latin and Korean), generated from the committed OTF files into
    /// Assets/SpatialBlend/Samples/UI/Fonts by the Editor menu "SpatialBlend/UI/Generate Pretendard TMP Fonts". The font
    /// set is registered by the ControlPanel that owns it; in the Editor it is also found by path. With no set the TMP
    /// default font is used.
    /// </summary>
    public static class UiFonts
    {
        public const string SetPath = "Assets/SpatialBlend/Samples/UI/Fonts/UiFontSet.asset";

        private static UiFontSet _set;

        public static void Register(UiFontSet set)
        {
            if (set != null) _set = set;
        }

        public static UiFontSet Set
        {
            get
            {
#if UNITY_EDITOR
                if (_set == null) _set = UnityEditor.AssetDatabase.LoadAssetAtPath<UiFontSet>(SetPath);
#endif
                return _set;
            }
        }

        public static TMP_FontAsset Regular => Set != null ? Set.Regular : null;
        public static TMP_FontAsset SemiBold => Set != null ? Set.SemiBold : null;

        public static void Apply(TMP_Text text, bool semiBold)
        {
            var font = semiBold ? SemiBold : Regular;
            if (font != null) text.font = font;
        }
    }
}
