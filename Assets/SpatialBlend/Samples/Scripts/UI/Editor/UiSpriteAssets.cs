// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Samples.FreeformCollaboration.UI.Editor
{
    /// <summary>
    /// Writes the generated rounded-rectangle sprites as PNG assets so prefabs can reference them (the runtime sprites
    /// in <see cref="UiSprites"/> are not serialisable). Same signed-distance bake, same 9-slice border, so a prefab and
    /// the code-built panel have identical corners. Files are named by radius and rebuilt byte-for-byte.
    /// </summary>
    public static class UiSpriteAssets
    {
        public const string Dir = "Assets/SpatialBlend/Samples/UI/Sprites/";

        public static string PathFor(float radius) => Dir + "rounded-r" + Mathf.RoundToInt(radius * 10f) + ".png";

        public static Sprite RoundedRect(float radius)
        {
            string path = PathFor(radius);
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;
            Directory.CreateDirectory(Dir);
            int n = UiSprites.Size(radius, 0f);
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false);
            texture.SetPixels32(UiSprites.Bake(radius, 0f, UiSprites.Mode.Fill, 0f));
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = UiSprites.Supersample * UiSprites.ReferencePpu;
            float border = UiSprites.Border(radius, 0f);
            importer.spriteBorder = new Vector4(border, border, border, border);
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
    }
}
#endif
