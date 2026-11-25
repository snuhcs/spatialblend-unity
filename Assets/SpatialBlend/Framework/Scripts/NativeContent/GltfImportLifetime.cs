// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

using GLTFast;
using UnityEngine;

namespace SpatialBlend.Objects
{
    // Owns GLTFast import assets for exactly as long as the promoted runtime
    // scene. GltfImport.Dispose destroys imported meshes/materials, so disposal
    // must happen when this content root is destroyed, not at load completion.
    public sealed class GltfImportLifetime : MonoBehaviour
    {
        private GltfImport _import;

        internal void Retain(GltfImport gltfImport)
        {
            _import = gltfImport;
        }

        private void OnDestroy()
        {
            _import?.Dispose();
            _import = null;
        }
    }
}
