// Copyright (c) 2026 Seoul National University
// SPDX-License-Identifier: MIT

namespace SpatialBlend.Objects
{
    /// <summary>
    /// Content types supported by <see cref="SbNativeEntity"/> for syncing and runtime loading.
    /// Kept in runtime (non-Samples) so the framework does not depend on the Samples assembly.
    /// </summary>
    public enum NativeContentType : int
    {
        Unknown = 0,
        GLTF = 1,
        GaussianSplat = 2,
        AssetBundle = 3,
    }
}


