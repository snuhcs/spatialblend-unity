// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.IO;
using SpatialBlend.Samples.FreeformCollaboration.UI;
using UnityEditor;
using UnityEngine;

namespace SpatialBlend.Editor
{
    /// <summary>
    /// Builds the two "pending" indicators the scan flow shows (<c>Sam3dPendingIndicatorManager</c>): a column of rising sparks in the
    /// accent colour while a scan of your own object is being built, and the same in the shared-object violet while an object that
    /// someone else shared is loading. They replaced the Asset Store "Beautiful Portals VFX" prefabs, which cannot be redistributed:
    /// everything here (the soft-dot texture, the material and the particle settings) is made by this builder, so the assets are the
    /// repository's own. Run <c>SpatialBlend &gt; Build &gt; Pending Indicator Prefabs</c>, or in batchmode
    /// <c>-executeMethod SpatialBlend.Editor.PendingIndicatorPrefabs.Build</c>; it overwrites the files in place and keeps their GUIDs.
    /// </summary>
    public static class PendingIndicatorPrefabs
    {
        public const string Folder = "Assets/SpatialBlend/Samples/Scanning/Indicators";
        public const string TexturePath = Folder + "/PendingSoftDot.png";
        public const string MaterialPath = Folder + "/PendingSpark.mat";
        public const string ScanPrefabPath = Folder + "/ScanPendingIndicator.prefab";
        public const string SharedPrefabPath = Folder + "/SharedObjectPendingIndicator.prefab";

        private const int DotSize = 64;

        [MenuItem("SpatialBlend/Build/Pending Indicator Prefabs")]
        public static void Build()
        {
            Directory.CreateDirectory(Folder);
            Texture2D dot = BuildDotTexture();
            Material material = BuildMaterial(dot);
            BuildPrefab(ScanPrefabPath, "ScanPendingIndicator", UiTheme.Palette.Accent, material);
            BuildPrefab(SharedPrefabPath, "SharedObjectPendingIndicator", UiTheme.Palette.SharedReal, material);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // A white disc with a smooth falloff to transparent: the sprite of one spark.
        private static Texture2D BuildDotTexture()
        {
            var pixels = new Color32[DotSize * DotSize];
            float centre = (DotSize - 1) * 0.5f;
            for (int y = 0; y < DotSize; y++)
            {
                for (int x = 0; x < DotSize; x++)
                {
                    float r = Mathf.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre)) / centre;
                    float a = Mathf.Clamp01(1f - r);
                    pixels[y * DotSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * a * a));
                }
            }
            var texture = new Texture2D(DotSize, DotSize, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            File.WriteAllBytes(TexturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        }

        private static Material BuildMaterial(Texture2D dot)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Shader shader = Shader.Find("Particles/Standard Unlit");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            material.shader = shader;
            material.mainTexture = dot;
            // Additive blending weighted by the soft dot's alpha, as for a glow (Particles/Standard Unlit: _Mode 4 = Additive).
            material.SetFloat("_Mode", 4f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void BuildPrefab(string path, string name, Color colour, Material material)
        {
            var root = new GameObject(name);
            try
            {
                var particles = root.AddComponent<ParticleSystem>();
                var main = particles.main;
                main.loop = true;
                main.prewarm = true;
                main.duration = 2f;
                main.startLifetime = 1.6f;
                main.startSpeed = 0.35f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.05f);
                main.startColor = colour;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.maxParticles = 200;
                main.playOnAwake = true;

                var emission = particles.emission;
                emission.rateOverTime = 45f;

                // A ring on the floor (the object's footprint at scale 1 m), sparks rising from it.
                var shape = particles.shape;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = 0.25f;
                shape.radiusThickness = 0.15f;
                shape.rotation = new Vector3(-90f, 0f, 0f);

                var fade = particles.colorOverLifetime;
                fade.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) });
                fade.color = gradient;

                var renderer = root.GetComponent<ParticleSystemRenderer>();
                renderer.sharedMaterial = material;
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
