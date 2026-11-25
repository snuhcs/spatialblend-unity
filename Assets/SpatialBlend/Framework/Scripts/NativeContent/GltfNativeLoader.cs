// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using System;
using UnityEngine;

namespace SpatialBlend.Objects
{
    /// <summary>
    /// Minimal runtime GLTF loader for <see cref="SbNativeEntity"/> content.
    /// </summary>
    public sealed class GltfNativeLoader : NativeLoader
    {
        /// <summary>
        /// The app's object template for a received copy (v1 made the replica from the scan template, ObjectPreset: its collider, ray
        /// interactable and placement motif). The Framework does not depend on Samples, so the app registers it
        /// (<c>SpatialBlend.Samples.Objects.GltfNativeLoader</c>). Without one, or when it gives no <see cref="SbNativeEntity"/>, a bare
        /// object is made: it shows the model but cannot be grabbed, so a partner's received scan could not be placed.
        /// The same bare object stands in when the hook throws, so the peer's object is never lost: it is logged as an error with
        /// the exception and its stack, because that copy has no collider, ray interactable or placement motif and the receiver
        /// cannot grab or place it (it stays bare; a working hook is what makes it grabbable).
        /// </summary>
        public static Func<string, GameObject> TemplateFactory { get; set; }

        public override GameObject CreateTemplate(string objectName)
        {
            GameObject made = null;
            try
            {
                made = TemplateFactory?.Invoke(objectName);
            }
            catch (Exception e)
            {
                // A failing app template must not lose the peer's object (the share would be dropped): it arrives bare.
                // The exception with its stack: a bare copy cannot be grabbed or placed, so this is a fault to fix, not a fallback.
                Debug.LogError($"[GltfNativeLoader] The app's template for '{objectName}' failed; a bare object is used (it cannot be grabbed or placed).\n{e}");
                made = null;
            }
            if (made != null)
            {
                if (made.GetComponent<SbNativeEntity>() != null) return made;
                Debug.LogError($"[GltfNativeLoader] The app's template for '{objectName}' has no SbNativeEntity; a bare object is used.");
                if (Application.isPlaying) UnityEngine.Object.Destroy(made); else UnityEngine.Object.DestroyImmediate(made);
            }
            var go = new GameObject(string.IsNullOrEmpty(objectName) ? "SyncedObject" : objectName);
            // SbNativeEntity is a MonoBehaviour; add it to the template so the sync pipeline works.
            if (go.GetComponent<SbNativeEntity>() == null)
            {
                go.AddComponent<SbNativeEntity>();
            }
            return go;
        }

        public override async Task LoadContentAsync(GameObject target, byte[] data)
        {
            await TryLoadContentAsync(target, data, null);
        }

        public override async Task LoadContentAsync(GameObject target, byte[] data,
                                                    Func<bool> shouldContinue)
        {
            await TryLoadContentAsync(target, data, shouldContinue);
        }

        public override async Task<bool> TryLoadContentAsync(GameObject target, byte[] data,
                                                              Func<bool> shouldContinue)
        {
            if (target == null || data == null || data.Length == 0) return false;
            bool loaded = await GltfRuntimeLoader.LoadModelFromBytes(target, data, shouldContinue);
            if (loaded) ScanObjectFit.FitReplica(target);
            return loaded;
        }
    }
}


