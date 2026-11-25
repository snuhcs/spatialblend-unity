// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using System;
using UnityEngine;

namespace SpatialBlend.Objects
{
    /// <summary>
    /// Runtime loader for native-synced content. Implementations should avoid depending on Samples.
    /// </summary>
    public abstract class NativeLoader
    {
        public static NativeLoader GetLoader(NativeContentType type)
        {
            switch (type)
            {
                case NativeContentType.GLTF:
                    return new GltfNativeLoader();

                // TODO: Implement other loaders (Gaussian splats, asset bundles).
                default:
                    Debug.LogError($"[NativeLoader] Unsupported Type: {type}");
                    return null;
            }
        }

        /// <summary>
        /// Creates a GameObject template that has the expected components for this content type.
        /// </summary>
        public abstract GameObject CreateTemplate(string objectName);

        /// <summary>Legacy two-argument loader contract retained for existing subclasses.</summary>
        public abstract Task LoadContentAsync(GameObject target, byte[] data);

        /// <summary>
        /// Cancellation-aware overload. New loaders should override this before
        /// mutating a target after an await; legacy loaders remain source-compatible.
        /// </summary>
        public virtual Task LoadContentAsync(GameObject target, byte[] data,
                                             Func<bool> shouldContinue) =>
            LoadContentAsync(target, data);

        /// <summary>
        /// Result-aware path for loaders that can distinguish failed parsing or
        /// promotion. The legacy Task-only contract remains available to existing
        /// content loaders; its default result means only that it completed while
        /// the owner still accepted work.
        /// </summary>
        public virtual async Task<bool> TryLoadContentAsync(GameObject target, byte[] data,
                                                            Func<bool> shouldContinue)
        {
            await LoadContentAsync(target, data, shouldContinue);
            return target != null && (shouldContinue == null || shouldContinue());
        }
    }
}


