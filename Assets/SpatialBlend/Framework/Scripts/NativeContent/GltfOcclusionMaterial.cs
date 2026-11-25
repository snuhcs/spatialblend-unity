// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;

namespace SpatialBlend.Objects
{
    /// <summary>
    /// Material generator for runtime-loaded GLB content (scanned and shared objects). It lets glTFast build its
    /// built-in-pipeline materials, then swaps their shader for the occlusion variant so the Meta environment
    /// depth hides the part of an object that stands behind a real surface (as v1, parity row E3).
    /// glTFast keeps every material property, so only the shader changes. When the shader is missing from the
    /// build, the loader falls back to glTFast's default materials.
    /// </summary>
    public static class GltfOcclusionMaterial
    {
        /// <summary>Shader name declared in glTFPbrMetallicRoughnessOccluded.shader.</summary>
        public const string ShaderName = "SpatialBlend/GLTF/PbrMetallicRoughnessOccluded";

        /// <summary>The glTFast shader the occlusion shader replaces.</summary>
        public const string SourceShaderName = "glTF/PbrMetallicRoughness";

        /// <summary>True when a material on this glTFast shader is switched to the occlusion shader.</summary>
        public static bool Overrides(Shader source) => source != null && source.name == SourceShaderName;

        /// <summary>The generator for a runtime import. Never null.</summary>
        public static IMaterialGenerator CreateGenerator()
        {
            return CreateGenerator(Shader.Find(ShaderName));
        }

        /// <summary>The generator for an explicit shader. A null shader returns the default generator.</summary>
        public static IMaterialGenerator CreateGenerator(Shader overrideShader)
        {
            IMaterialGenerator inner = new BuiltInMaterialGenerator();
            if (overrideShader == null)
            {
                Debug.LogWarning($"[GltfOcclusionMaterial] Shader not found: {ShaderName}. Using glTFast default materials.");
                return inner;
            }
            return new OverrideShaderMaterialGenerator(inner, overrideShader);
        }

        /// <summary>
        /// The material glTFast gives a primitive that has none, after the shader override. It is shared, so the
        /// caller must not destroy it. This is the one place the generator's output is observable without
        /// importing a GLB.
        /// </summary>
        public static Material CreateDefaultMaterial(Shader overrideShader)
        {
            return CreateGenerator(overrideShader).GetDefaultMaterial();
        }

        private static Material s_defaultOverride;

        private sealed class OverrideShaderMaterialGenerator : IMaterialGenerator
        {
            private readonly IMaterialGenerator _inner;
            private readonly Shader _shader;

            public OverrideShaderMaterialGenerator(IMaterialGenerator inner, Shader shader)
            {
                _inner = inner;
                _shader = shader;
            }

            public Material GetDefaultMaterial(bool pointsSupport = false)
            {
                // glTFast hands every importer one static default material. Switching its shader would change
                // that shared object for imports that do not use this generator, so the override keeps its own.
                Material source = _inner.GetDefaultMaterial(pointsSupport);
                if (source == null || !Overrides(source.shader)) return source;
                if (s_defaultOverride == null || s_defaultOverride.shader != _shader)
                {
                    s_defaultOverride = new Material(source)
                    {
                        name = source.name + "-Occluded",
                        hideFlags = HideFlags.DontSave,
                    };
                    s_defaultOverride.shader = _shader;
                }
                return s_defaultOverride;
            }

            public Material GenerateMaterial(GLTFast.Schema.MaterialBase gltfMaterial, IGltfReadable gltf,
                                             bool pointsSupport = false)
            {
                return Apply(_inner.GenerateMaterial(gltfMaterial, gltf, pointsSupport));
            }

            public void SetLogger(ICodeLogger logger)
            {
                _inner.SetLogger(logger);
            }

            // Only the metallic-roughness material is swapped. The occlusion shader reads the metallic-roughness
            // properties, so an unlit or specular-glossiness material switched to it would lose its colour and
            // render black. Those keep glTFast's own shader and are not occluded.
            private Material Apply(Material material)
            {
                if (material != null && Overrides(material.shader))
                {
                    material.shader = _shader;
                }
                return material;
            }
        }
    }
}
