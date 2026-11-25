// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Generates the TMP font assets for the UI typeface from the committed Pretendard OTF files, so the assets can be
    /// rebuilt from source. They are dynamic SDF assets with an empty atlas: every glyph (Latin, Hangul, anything else a
    /// display name may contain) is generated from the OTF on first use, so the committed files stay small and do not
    /// depend on which text happened to be drawn when they were saved. Existing assets are overwritten in place, which keeps
    /// their GUIDs (the prefabs and the font set reference them).
    /// Menu: SpatialBlend/UI/Generate Pretendard TMP Fonts. Batch: -executeMethod ...UiFontAssetBuilder.GenerateBatch.
    /// </summary>
    public static class UiFontAssetBuilder
    {
        private const string FontDir = "Assets/SpatialBlend/Samples/Fonts/Pretendard/";
        private const string OutDir = "Assets/SpatialBlend/Samples/UI/Fonts/";

        public static void GenerateBatch()
        {
            Generate();
            EditorApplication.Exit(0);
        }

        [MenuItem("SpatialBlend/UI/Generate Pretendard TMP Fonts")]
        public static void Generate()
        {
            Directory.CreateDirectory(OutDir);
            var regular = Build("Pretendard-Regular");
            var semiBold = Build("Pretendard-SemiBold");
            var set = ScriptableObject.CreateInstance<UiFontSet>();
            set.Regular = regular;
            set.SemiBold = semiBold;
            AssetDatabase.CreateAsset(set, UiFonts.SetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static TMP_FontAsset Build(string name)
        {
            AssetDatabase.ImportAsset(FontDir + name + ".otf", ImportAssetOptions.ForceSynchronousImport);
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontDir + name + ".otf");
            if (font == null) throw new FileNotFoundException(FontDir + name + ".otf");
            string path = OutDir + name + " SDF.asset";

            var asset = TMP_FontAsset.CreateFontAsset(font, 72, 8, GlyphRenderMode.SDFAA, 512, 512,
                AtlasPopulationMode.Dynamic, true);
            asset.name = name + " SDF";
            AssetDatabase.CreateAsset(asset, path);
            asset.atlasTexture.name = name + " Atlas";
            asset.material.name = name + " Material";
            AssetDatabase.AddObjectToAsset(asset.atlasTexture, asset);
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            EditorUtility.SetDirty(asset);
            EditorUtility.SetDirty(asset.material);
            Debug.Log($"[UiFontAssetBuilder] {path}: {asset.characterTable.Count} glyphs (dynamic: filled on first use)");
            return asset;
        }
    }
}
#endif
