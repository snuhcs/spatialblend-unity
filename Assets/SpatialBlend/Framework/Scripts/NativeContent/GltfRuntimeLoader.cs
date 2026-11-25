// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using System;
using UnityEngine;
using GLTFast;

namespace SpatialBlend.Objects
{
    /// <summary>
    /// Minimal GLTFast-based runtime loader used by <see cref="GltfNativeLoader"/>.
    /// This intentionally avoids Samples-specific preset/addressable plumbing.
    /// </summary>
    public static class GltfRuntimeLoader
    {
        public static async Task<bool> LoadModelFromBytes(GameObject target, byte[] data,
                                                           Func<bool> shouldContinue = null)
        {
            GltfImport gltfImport = null;
            GameObject staging = null;
            try
            {
                if (!CanContinue(target, shouldContinue)) return false;
                if (!LooksLikeCompleteGlb(data))
                {
                    Debug.LogError("[GltfRuntimeLoader] Failed to parse GLTF binary");
                    return false;
                }
                // GLTFast's default defer agent owns a DontDestroyOnLoad helper.
                // That is correct while playing, but invalid in EditMode tests and
                // tooling. The installed uninterrupted agent has no hidden object.
                // Materials take the occlusion shader so the environment depth hides content behind real surfaces.
                var materials = GltfOcclusionMaterial.CreateGenerator();
                gltfImport = Application.isPlaying
                    ? new GltfImport(materialGenerator: materials)
                    : new GltfImport(null, new UninterruptedDeferAgent(), materials);
                bool success = await gltfImport.LoadGltfBinary(data);
                if (!success)
                {
                    Debug.LogError("[GltfRuntimeLoader] Failed to parse GLTF binary");
                    return false;
                }
                // Parsing is not cancellable in GLTFast, but retirement is checked
                // before touching the Unity target or raising completion.
                if (!CanContinue(target, shouldContinue)) return false;

                // Instantiate into an inactive staging child. GLTFast instantiation
                // can span frames, so cancellation must never leave partial geometry
                // under the live replica.
                staging = new GameObject("__SpatialBlendGltfStaging");
                staging.SetActive(false);
                staging.transform.SetParent(target.transform, false);
                var instSettings = new InstantiationSettings
                {
                    SceneObjectCreation = SceneObjectCreation.Always
                };
                var instantiator = new GameObjectInstantiator(gltfImport, staging.transform,
                                                              null, instSettings);
                success = await gltfImport.InstantiateMainSceneAsync(instantiator);
                if (!success || !CanContinue(target, shouldContinue)) return false;

                // Dispose would destroy GLTFast's meshes/materials while the promoted
                // scene still uses them. Transfer disposal to the staging root, which
                // now becomes the content root and is destroyed with the replica.
                staging.AddComponent<GltfImportLifetime>().Retain(gltfImport);
                gltfImport = null;
                RetirePreviousPromotedContent(target.transform, staging.transform);
                staging.name = "GLTF";
                staging.SetActive(true);
                staging = null;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GltfRuntimeLoader] Exception during load: {e.Message}");
                return false;
            }
            finally
            {
                if (staging != null) DestroyOwned(staging);
                gltfImport?.Dispose();
            }
        }

        private static bool CanContinue(GameObject target, Func<bool> shouldContinue)
        {
            return target != null && (shouldContinue == null || shouldContinue());
        }

        private static bool LooksLikeCompleteGlb(byte[] data)
        {
            if (data == null || data.Length < 20) return false;
            uint magic = ReadUInt32(data, 0);
            uint version = ReadUInt32(data, 4);
            uint declaredLength = ReadUInt32(data, 8);
            uint firstChunkLength = ReadUInt32(data, 12);
            uint firstChunkType = ReadUInt32(data, 16);
            return magic == 0x46546C67 && version == 2 && declaredLength == (uint)data.Length &&
                firstChunkType == 0x4E4F534A && firstChunkLength <= (uint)(data.Length - 20);
        }

        private static uint ReadUInt32(byte[] source, int offset) =>
            (uint)(source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16 |
                   source[offset + 3] << 24);

        private static void RetirePreviousPromotedContent(Transform target, Transform current)
        {
            for (int i = target.childCount - 1; i >= 0; --i)
            {
                Transform child = target.GetChild(i);
                if (child != current && child.GetComponent<GltfImportLifetime>() != null)
                {
                    DestroyOwned(child.gameObject);
                }
            }
        }

        private static void DestroyOwned(GameObject owned)
        {
            if (owned == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(owned);
            else UnityEngine.Object.DestroyImmediate(owned);
        }
    }
}


