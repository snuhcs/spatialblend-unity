// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using UnityEngine;
using System.Threading.Tasks;

namespace SpatialBlend.Samples.Objects
{
    public abstract class NativeLoader
    {
        public static NativeLoader GetLoader(NativeContentType type)
        {
            switch (type)
            {
                case NativeContentType.GLTF:
                    return new GltfNativeLoader();
                    
                // TODO(dongho): Implement other loaders
                    
                default:
                    Debug.LogError($"[NativeLoader] Unsupported Type: {type}");
                    return null;
            }
        }

        public abstract GameObject CreateTemplate(string objectName);
        public abstract Task LoadContentAsync(GameObject target, byte[] data);

    }
}